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
- **`Agent/WorkspaceMemoryProvider.cs`** — seeds workspace templates on first run, reads workspace files, and injects them as context each turn
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
  WorkspaceMemoryProvider.cs → Workspace seeding + file reading + per-turn context injection
Tools/
  AgentTools.cs         → All tools as plain C# methods (read_file, write_file, exec)
                          Registered via AIFunctionFactory.Create() — used when DOTCLAW_SANDBOX=off
  SandboxTools.cs       → Owns the single long-lived MCP client to the MXC sandbox server
                          (DotClaw.SandboxMcp); used when DOTCLAW_SANDBOX=on (default)
Session/
  SessionManager.cs     → JSONL conversation persistence
Runtime/                → Channel-based gateway plumbing + turn execution
  AgentRunner.cs        → Runs a User/Heartbeat/Cron turn and delivers via an IMessageSink
  HeartbeatRunner.cs    → Ambient PeriodicTimer producer (proactive "pulse")
  DotClawConfig.cs      → Env-var config (heartbeat on/off + interval)
  Route / InboundItem / TurnSource / IMessageSink
Cron/                   → Scheduled, self-delivering reminders
  CronService.cs        → Self-re-arming timer (port of OpenClaw's armTimer) + persistence
  CronTools.cs          → cron_add / cron_list / cron_remove (route-bound, User turns only)
  CronSchedule.cs / CronJob.cs → in:1m / every:30m grammar + persisted job (~/.dotclaw/cron.json)
WorkspaceTemplates/     → First-run template files (SOUL, BOOTSTRAP, IDENTITY, HEARTBEAT, etc.)

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

## Proactive Butlering — Cron + Heartbeat

DotClaw re-implements two of OpenClaw's "the agent acts on its own" concepts. Both are separate
producers into the gateway's single inbound channel; cron additionally runs **concurrently** in
isolated sessions and delivers itself.

### Cron — scheduled, self-delivering reminders

Ask Link (e.g. on Telegram): *"Remind me in 1 minute to stretch."* The LLM calls the **`cron_add`**
tool, which persists a job (with the chat's route baked in) to `~/.dotclaw/cron.json`. When it's due,
`CronService` runs the agent in an **isolated `cron-{id}` session** and delivers the reminder itself —
mirroring OpenClaw's *isolated + announce* cron model. The timer is a single self-re-arming loop (a
port of OpenClaw's `armTimer`) that sleeps until the next due job, clamped to a ~60s watchdog.

- Grammar: `in:<dur>` (one-shot) / `every:<dur>` (recurring); units `s` / `m` / `h` / `d`.
- Tools (only offered on **user** turns, never to cron/heartbeat turns — anti-recursion):
  `cron_add`, `cron_list`, `cron_remove`. Jobs survive restart (startup catch-up re-fires overdue ones).

### Heartbeat — an ambient pulse with restraint (opt-in)

When enabled, a `PeriodicTimer` ticks every interval and runs a turn against the rule in
`HEARTBEAT.md`. If there's nothing worth saying, Link replies exactly `HEARTBEAT_OK` and **stays
silent**; only a genuine, state-aware reason produces a message. Off by default.

```powershell
$env:DOTCLAW_HEARTBEAT = "on"            # enable the heartbeat (default: off)
$env:DOTCLAW_HEARTBEAT_INTERVAL = "30"   # tick interval in seconds (default: 45)
```

`HEARTBEAT.md` is seeded into the workspace but **excluded** from the normal per-turn context, so it
only drives the heartbeat — not every reply.

> Concurrency: cron runs use their own session files, so they never collide with chat history. The one
> shared mutable surface — the workspace `*.md` files — is guarded by a process-wide reader/writer lock
> plus atomic writes.

## Coming Later

- **Managed scheduling:** Azure AI Foundry **Routines** (Timer/Recurring triggers) as the hosted cron.
- **Durable / eternal orchestrations** (Azure Functions Durable Task for MAF) for an enterprise heartbeat.
- **Sub-agents** and hardened MXC + Azure deployment.

## Human-in-the-Loop Approval

Some actions should ask before they happen. DotClaw ships a simulated **`send_message`**
tool ("text a contact on your behalf") that appends to `~/.dotclaw/outbox.log` — and gates
it behind human approval using MAF's `ApprovalRequiredAIFunction`.

**Which tools require approval is configurable at runtime** via `DOTCLAW_APPROVAL_TOOLS`
(comma-separated, case-insensitive tool names; defaults to `send_message`):

```powershell
# Default — only send_message needs approval
dotnet run -- "text my manager Sarah that I'll be 10 minutes late"

# Gate shell commands too (proves approval is config, not code)
$env:DOTCLAW_APPROVAL_TOOLS = "send_message,exec"
```

- **CLI** asks synchronously with a `y/n` prompt (`AnsiConsole.Confirm`). Approve → the tool
  runs and writes the outbox line; deny → the agent backs off, nothing is written.
- **Telegram** asks asynchronously: the bot replies with inline **`✅ Approve` / `❌ Deny`**
  buttons; tapping resolves the pending action and the buttons freeze to "✅ Approved" /
  "❌ Denied". Approve your agent's actions from your phone.

Under the hood, `ApprovalRequiredAIFunction` is just a marker — the
`FunctionInvokingChatClient` (already in the pipeline via `.UseFunctionInvocation()`) turns a
gated call into a `ToolApprovalRequestContent`, then runs or rejects it on the next turn based
on `request.CreateResponse(approved)`. Pending Telegram approvals are kept in-memory (demo-grade),
so a bot restart drops them (handled gracefully as "expired").

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
