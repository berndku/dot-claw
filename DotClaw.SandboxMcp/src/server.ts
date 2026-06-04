// DotClaw sandbox MCP server.
//
// Exposes three tools — exec, read_file, write_file — that run on the host
// inside a Microsoft eXecution Container (MXC) process sandbox instead of
// directly on the machine. DotClaw (C#) talks to this server over stdio MCP.
//
// Lifetime model (see plan): this Node process is long-lived (spawned once by
// the C# host and reused). Each tool call spawns a FRESH, ephemeral MXC
// sandbox (one `wxc-exec.exe` per command) which exits when the command
// finishes — there is no warm container reused across calls.
//
// NOTE: makerspace demo. MXC is an early preview and its profiles are not yet
// security boundaries.

import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { StdioServerTransport } from "@modelcontextprotocol/sdk/server/stdio.js";
import { z } from "zod";
import {
  createConfigFromPolicy,
  spawnSandboxFromConfig,
  getPlatformSupport,
} from "@microsoft/mxc-sdk";
import type { ChildProcess } from "node:child_process";
import { promises as fs } from "node:fs";
import * as os from "node:os";
import * as path from "node:path";

const SCHEMA_VERSION = "0.6.0-alpha";
const TIMEOUT_MS = 60_000;
const MAX_TOOL_OUTPUT_CHARS = 8_000;

// %SystemRoot%\System32 is granted to ALL APPLICATION PACKAGES on every Windows
// install, so the sandbox can see it implicitly and MXC never has to rewrite an
// ACL for it (no WRITE_DAC, no admin). It holds cmd.exe and the built-in tools.
const SYSTEM_ROOT = process.env.SystemRoot ?? process.env.windir ?? "C:\\Windows";
const SYSTEM32 = path.join(SYSTEM_ROOT, "System32");

// The persistent workspace mirrors DotClaw.Agent.MemoryManager in C#.
// This is where SOUL.md, MEMORY.md, USER.md, etc. are seeded and edited.
const WORKSPACE_DIR = resolveWorkspaceDir();

interface SandboxResult {
  stdout: string;
  stderr: string;
  code: number | null;
}

/**
 * Build the baseline MXC policy:
 *
 *  - readwrite: the user's persistent DotClaw workspace, so file changes survive
 *    restarts and match the files loaded into the system prompt.
 *  - readonly: only %SystemRoot%\System32. It is already granted to ALL
 *    APPLICATION PACKAGES on every Windows install, so MXC never has to rewrite
 *    an ACL (no WRITE_DAC, no admin). This is enough to resolve cmd.exe and the
 *    built-in Windows tools (hostname, findstr, tar, robocopy, ...). A few
 *    token/privilege-dependent tools (e.g. whoami) crash inside the AppContainer
 *    in this MXC preview, but the common ones work.
 *
 * We deliberately do NOT scan PATH (getAvailableToolsPolicy): third-party tool
 * directories differ per machine and would expand both the sandbox surface and
 * the amount of per-host access setup.
 */
function basePolicy() {
  return {
    version: SCHEMA_VERSION,
    filesystem: {
      readonlyPaths: [SYSTEM32],
      readwritePaths: [WORKSPACE_DIR],
    },
    network: { allowOutbound: false },
    timeoutMs: TIMEOUT_MS,
  };
}

function resolveWorkspaceDir(): string {
  const fromEnv = process.env.DOTCLAW_WORKSPACE_DIR;
  if (fromEnv && fromEnv.trim().length > 0) {
    return path.resolve(fromEnv);
  }

  const home = process.env.USERPROFILE ?? process.env.HOME ?? os.homedir();
  return path.join(home, ".dotclaw", "workspace");
}

/**
 * Normalize a Windows path for containment comparison: absolute, lower-cased,
 * no trailing separators.
 */
function normPath(p: string): string {
  return path.resolve(p).toLowerCase().replace(/[\\/]+$/, "");
}

/** True if `child` is the same as, or nested under, `parent`. */
function isWithin(child: string, parent: string): boolean {
  const c = normPath(child);
  const pa = normPath(parent);
  return c === pa || c.startsWith(pa + "\\");
}

/**
 * Resolve a user-supplied path against the persistent workspace and confirm it
 * stays inside it. Relative paths land in the workspace; an absolute path or a
 * "..\\.." traversal that escapes the workspace returns null. This keeps the
 * file tools scoped to the same directory DotClaw loads into its prompt.
 */
function resolveInWorkspace(p: string): string | null {
  const target = path.resolve(WORKSPACE_DIR, p);
  return isWithin(target, WORKSPACE_DIR) ? target : null;
}

function truncateToolOutput(output: string): string {
  if (output.length <= MAX_TOOL_OUTPUT_CHARS) {
    return output;
  }

  const half = Math.floor(MAX_TOOL_OUTPUT_CHARS / 2);
  return (
    output.slice(0, half) +
    `\n\n... [truncated ${output.length - MAX_TOOL_OUTPUT_CHARS} chars] ...\n\n` +
    output.slice(-half)
  );
}

function appendBounded(current: string, chunk: string): string {
  const combined = current + chunk;
  if (combined.length <= MAX_TOOL_OUTPUT_CHARS * 2) {
    return combined;
  }

  const keep = MAX_TOOL_OUTPUT_CHARS;
  return combined.slice(0, keep) + combined.slice(-keep);
}

async function ensureWorkspace(): Promise<void> {
  await fs.mkdir(WORKSPACE_DIR, { recursive: true });
}

/**
 * Run a single command line inside a fresh MXC process sandbox and collect
 * its output. Uses pipe mode (usePty:false) for separated stdout/stderr and a
 * reliable exit code. The policy is fixed (System32 read-only + persistent
 * DotClaw workspace read-write); there are no dynamic per-call grants.
 */
async function runInSandbox(commandLine: string): Promise<SandboxResult> {
  await ensureWorkspace();

  const policy = basePolicy();
  const config = createConfigFromPolicy(policy, "process");
  config.process!.commandLine = commandLine;
  // Start the sandboxed command in the in-policy workspace. Without this,
  // cmd.exe inherits the Node host's cwd (not in the policy) and aborts with
  // "The current directory is invalid." See WORKSPACE_DIR note above.
  config.process!.cwd = WORKSPACE_DIR;

  const child = spawnSandboxFromConfig(config, { usePty: false }) as ChildProcess;

  return new Promise<SandboxResult>((resolve) => {
    let stdout = "";
    let stderr = "";
    child.stdout?.on("data", (d: Buffer) => (stdout = appendBounded(stdout, d.toString())));
    child.stderr?.on("data", (d: Buffer) => (stderr = appendBounded(stderr, d.toString())));
    child.on("error", (err: Error) =>
      resolve({ stdout, stderr: stderr + `\n[sandbox spawn error] ${err.message}`, code: -1 }),
    );
    child.on("close", (code: number | null) => resolve({ stdout, stderr, code }));
  });
}

/** Format a sandbox result into a single text block for the model. */
function formatResult(res: SandboxResult): string {
  const out = (res.stdout + res.stderr).trim();
  const body = out.length > 0 ? out : "(no output)";
  const formatted = res.code === 0 || res.code === null ? body : `${body}\n(exit code ${res.code})`;
  return truncateToolOutput(formatted);
}

function textContent(text: string) {
  return { content: [{ type: "text" as const, text }] };
}

/**
 * Describe the sandbox workspace for tool descriptions and error messages.
 */
const WORKSPACE_HINT =
  "Paths are interpreted relative to the persistent DotClaw workspace; paths that escape it are refused.";

const server = new McpServer({ name: "dotclaw-sandbox", version: "0.1.0" });

server.registerTool(
  "exec",
  {
    description:
      "Run a shell command inside an MXC sandbox and return its output. " +
      "Network is blocked; only the persistent DotClaw workspace and the built-in Windows tools in System32 are reachable.",
    inputSchema: { command: z.string().describe("The shell command to execute.") },
  },
  async ({ command }) => {
    const res = await runInSandbox(`cmd /c ${command}`);
    return textContent(formatResult(res));
  },
);

server.registerTool(
  "read_file",
  {
    description:
      "Read the contents of a file inside the persistent DotClaw workspace. " + WORKSPACE_HINT,
    inputSchema: { path: z.string().describe("Path to the file to read, relative to the DotClaw workspace.") },
  },
  async (args) => {
    const target = resolveInWorkspace(args.path);
    if (!target) {
      return textContent(`Refused: read_file only allows paths inside the DotClaw workspace (${WORKSPACE_DIR}).`);
    }
    const res = await runInSandbox(`cmd /c type "${target}"`);
    return textContent(formatResult(res));
  },
);

server.registerTool(
  "write_file",
  {
    description:
      "Write content to a file inside the persistent DotClaw workspace. " +
      "Content is staged to a host temp file and copied into the sandbox to avoid shell escaping. " +
      WORKSPACE_HINT,
    inputSchema: {
      path: z.string().describe("Path to the file to write, relative to the DotClaw workspace."),
      content: z.string().describe("The content to write to the file."),
    },
  },
  async (args) => {
    const target = resolveInWorkspace(args.path);
    if (!target) {
      return textContent(`Refused: write_file only allows paths inside the DotClaw workspace (${WORKSPACE_DIR}).`);
    }
    const dir = path.dirname(target);

    await ensureWorkspace();

    // Stage the content to a host file (inside the workspace, so it is
    // already covered by the base read-write grant), then copy it to the target.
    const staging = path.join(WORKSPACE_DIR, `dotclaw-write-${Date.now()}-${Math.random().toString(36).slice(2)}.tmp`);
    await fs.writeFile(staging, args.content, "utf8");
    try {
      // The target always lives inside the workspace (validated above), which is
      // read-write in the base policy, so no per-call grant is needed. Only mkdir
      // a sub-folder when the target is nested; join it with & so the copy always
      // runs, and gate the success echo on the copy with &&.
      const mkdirPart =
        normPath(dir) !== normPath(WORKSPACE_DIR) ? `if not exist "${dir}" mkdir "${dir}" & ` : "";
      const res = await runInSandbox(
        `cmd /c ${mkdirPart}copy /Y "${staging}" "${target}" >nul && echo Wrote ${Buffer.byteLength(args.content, "utf8")} bytes to "${target}"`,
      );
      return textContent(formatResult(res));
    } finally {
      await fs.rm(staging, { force: true });
    }
  },
);

async function main() {
  await ensureWorkspace();

  const support = getPlatformSupport();
  if (!support.isSupported) {
    // Log to stderr (stdout is the MCP channel). Tools will surface a clear
    // error when invoked on an unsupported host.
    console.error("[dotclaw-sandbox] WARNING: MXC reports this platform as unsupported. Tool calls will fail.");
  } else {
    console.error("[dotclaw-sandbox] MXC sandbox ready.");
  }
  const transport = new StdioServerTransport();
  await server.connect(transport);
}

main().catch((err) => {
  console.error("[dotclaw-sandbox] fatal:", err);
  process.exit(1);
});
