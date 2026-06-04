# 🦞 DotClaw

A C#/.NET port of the [OpenClaw](https://github.com/openclaw) personal AI assistant, based on the [Rebuilding OpenClaw tutorial](https://github.com/jcdeichmann/rebuilding-openclaw-tutorial).

## What's Built (Parts 1 & 2)

### Part 1 — The Agent Loop
- **`Agent/AgentLoop.cs`** — the core tool loop: send message → LLM responds → execute tools → repeat
- **`Tools/ReadFileTool.cs`** — read files from disk
- **`Tools/WriteFileTool.cs`** — write files to disk
- **`Tools/ExecTool.cs`** — run shell commands (with dangerous-pattern blocking)
- **`Session/SessionManager.cs`** — persist conversation history to `~/.dotclaw/sessions/`
- **`Program.cs`** — interactive CLI and single-shot mode

### Part 2 — Personality: Soul + Identity
- **`Agent/MemoryManager.cs`** — seeds workspace templates on first run, reads workspace files
- **`Agent/ContextBuilder.cs`** — builds the system prompt with tagged workspace sections
- **`WorkspaceTemplates/`** — SOUL.md, USER.md, BOOTSTRAP.md, AGENTS.md, MEMORY.md

## Requirements

- .NET 9.0+
- GitHub CLI (`gh`) logged in — that's it! Token is auto-resolved.
  - Microsoft employees get unlimited tokens via their GitHub account.

## Setup

```bash
cd DotClaw
dotnet restore
```

No API keys to configure! If you're logged into `gh`, it just works.

Alternatively, you can set `GITHUB_TOKEN` manually:
```powershell
$env:GITHUB_TOKEN = "ghp_your_pat_here"
```

## Run

**Interactive mode:**
```bash
dotnet run
```

**Single-shot mode:**
```bash
dotnet run -- "what files are in the current directory"
dotnet run -- "write hello world to a file then read it back"
```

## How It Works

1. Your message is sent to GPT-4o via GitHub Models (free for MS employees)
2. If the LLM wants to call a tool, the agent executes it and feeds the result back
3. This loops up to 20 iterations until the LLM returns a plain text response
4. On first run, `BOOTSTRAP.md` triggers a personality setup flow — the agent asks your name, timezone, and preferred personality, then writes `SOUL.md` and `USER.md`
5. Every subsequent session loads workspace files into the system prompt automatically

## Architecture

```
Program.cs              → Entry point (CLI + single-shot)
                          MAF pipeline: OpenAI → FunctionInvocation → Build
Agent/
  ContextBuilder.cs     → System prompt assembly from workspace files
  MemoryManager.cs      → Workspace seeding + file reading
Tools/
  AgentTools.cs         → All tools as plain C# methods (read_file, write_file, exec)
                          Registered via AIFunctionFactory.Create() — used when DOTCLAW_SANDBOX=off
  SandboxTools.cs       → Owns the single long-lived MCP client to the MXC sandbox server
                          (DotClaw.SandboxMcp); used when DOTCLAW_SANDBOX=on (default)
Session/
  SessionManager.cs     → JSONL conversation persistence
WorkspaceTemplates/     → First-run template files (SOUL, BOOTSTRAP, IDENTITY, etc.)

DotClaw.SandboxMcp/     → Node/TS MCP server wrapping @microsoft/mxc-sdk (sandboxed tools)
```

### MAF Pipeline

The entire agent is built in 4 lines — no manual tool loop:

```csharp
IChatClient client = new ChatClientBuilder(openAiChatClient.AsIChatClient())
    .UseFunctionInvocation()   // ← MAF handles the tool loop automatically
    .Build();

var response = await client.GetResponseAsync(messages, chatOptions);  // ← one call does it all
```

## Coming Later

- **Part 4** — Heartbeat / cron-based proactive behavior

## Sandboxed Tools (MXC) — demo

By default DotClaw runs its `read_file`, `write_file`, and `exec` tools inside a
[**MXC**](https://github.com/microsoft/mxc) (Microsoft eXecution Container) process sandbox instead of
directly on the host. MXC has no .NET SDK — only a TypeScript one — so a tiny **Node/TypeScript MCP
server** (`DotClaw.SandboxMcp/`) wraps `@microsoft/mxc-sdk` and DotClaw consumes it over stdio MCP
(the pattern Microsoft Agent Framework supports natively).

```
DotClaw (C#, MAF)  --stdio MCP-->  DotClaw.SandboxMcp (Node/TS)  --@microsoft/mxc-sdk-->  wxc-exec.exe --> AppContainer
```

**Lifetime:** the Node MCP server is spawned **once** and reused for the whole run (so the Telegram
gateway, which builds an agent per message, doesn't leak a process). Each individual tool call runs in
a **fresh, ephemeral** MXC sandbox that exits when the command finishes.

### Switch it on/off

`DOTCLAW_SANDBOX` controls which tools the agent uses (default **on**):

```powershell
# Sandboxed tools via MXC (default)
$env:DOTCLAW_SANDBOX = "on"      # or just leave it unset

# In-process C# tools (Tools/AgentTools.cs) — current host behavior
$env:DOTCLAW_SANDBOX = "off"
```

### Build the sandbox server (needed when sandbox is on)

```powershell
cd DotClaw.SandboxMcp
npm install
npm run build      # → dist/server.js
```

The `@microsoft/mxc-sdk` npm package bundles the native `wxc-exec.exe`, so no Rust build is required.
C# locates the server at `DotClaw.SandboxMcp/dist/server.js` by default; override with
`DOTCLAW_SANDBOX_MCP_DIR`.

### Requirements & caveats (it's a makerspace demo)

- **Node.js ≥ 18** on `PATH`.
- **Windows 11 24H2+ (build 26100+)** for MXC's default `processcontainer` backend.
- **One-time admin host-prep** so a shell can start inside an AppContainer. Without it you'll see
  `BaseContainer is unavailable; DACL fallback requires write-DAC permission ...`. Run once from an
  **elevated** prompt (binary ships in the npm package):

  ```powershell
  $bin = "DotClaw.SandboxMcp\node_modules\@microsoft\mxc-sdk\bin\x64"
  & "$bin\wxc-host-prep.exe" prepare-system-drive   # once per machine
  & "$bin\wxc-host-prep.exe" prepare-null-device    # once per boot
  ```

- MXC is an **early preview** — Microsoft states its profiles are **not** security boundaries yet.
  Treat this as a demonstration of sandboxed tool execution, not a hardened isolation guarantee.
