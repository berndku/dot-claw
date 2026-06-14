# 🦞 DotClaw

A personal playground for experimenting with the Microsoft Agent Framework in C#/.NET — a hobby
reimagining of [OpenClaw](https://github.com/openclaw)'s personal AI assistant. Built for learning and
tinkering, not production.


## Requirements

- .NET 10.0+
- **Azure OpenAI** resource. Use Microsoft Entra ID authentication (`az login` locally or managed identity
  in Azure) or configure an optional API key.
- (Optional) **Azure Speech** resource for Telegram voice message transcription. Keyless auth is supported
  here too; just grant the appropriate data plane role to your user or managed identity.

### For MXC
- Node.js ≥ 18 if you use the `sandboxmcp` tool mode.
- **Windows 11 24H2+ (build 26100+)** for MXC's default `processcontainer` backend.
- **One-time admin host-prep** so a shell can start inside an AppContainer. Without it you'll see
  `BaseContainer is unavailable; DACL fallback requires write-DAC permission ...`. Run once from an
  **elevated** prompt.

For `sandboxmcp`, the binary ships in the npm package:

  ```powershell
  $bin = "DotClaw.SandboxMcp\node_modules\@microsoft\mxc-sdk\bin\x64"
  & "$bin\wxc-host-prep.exe" prepare-system-drive   # once per machine
  & "$bin\wxc-host-prep.exe" prepare-null-device    # once per boot
  ```

For `csharp-sandbox`, download `mxc-release-binaries.zip` from
[microsoft/mxc releases](https://github.com/microsoft/mxc/releases), unzip it, and set `MXC_BIN_DIR`
to the folder that contains `<arch>\wxc-exec.exe`.

- MXC is an **early preview** — Microsoft states its profiles are **not** security boundaries yet.
  Treat this as a demonstration of sandboxed tool execution, not a hardened isolation guarantee.

#### Troubleshooting: `E_NOTIMPL` / velocity-key gate

DotClaw pins the policy schema to `0.6.0-alpha` (in `DotClaw.SandboxMcp/src/server.ts` and
`DotClaw/Tools/CSharpSandboxTools.cs`), which selects MXC's gated **`base-container`** tier. That tier
sits behind a Windows Feature Store gate, so on builds where the gate is closed the executor returns
`E_NOTIMPL` even though the kernel API is present:

```
Experimental_CreateProcessInSandbox returned E_NOTIMPL. The following velocity keys are not enabled:
61389575, 61155944. Enable them and retry, or use schema version '0.4.0-alpha' to fall back to the
AppContainer backend.
```

You have two options:

**Option A — ViVeTool (light up the `base-container` tier).** Download
[ViVeTool](https://github.com/thebookisclosed/ViVe) for your CPU arch, run **elevated**, then
**reboot**:

  ```powershell
  # Use comma-separated IDs — repeated /id: flags are rejected.
  .\ViVeTool.exe /enable /id:61389575,61155944
  .\ViVeTool.exe /query  /id:61389575   # should report Enabled before you retry
  ```

**Option B — fall back to AppContainer (no ViVeTool).** Schema `0.4.0-alpha` takes the ungated
AppContainer path. In DotClaw that means changing the pinned schema constants (`SCHEMA_VERSION` /
`SchemaVersion`) to `0.4.0-alpha` — but you then still need the DACL host-prep
(`wxc-host-prep.exe prepare-system-drive` / `prepare-null-device`) above, otherwise you'll hit
`BaseContainer is unavailable; DACL fallback requires write-DAC permission ...`.


## Configuration

DotClaw uses the standard .NET configuration stack. Settings are loaded in this order (later wins):

1. **`appsettings.json`** — checked into the repo with documented defaults
2. **`appsettings.local.json`** — gitignored, holds your actual secrets for local dev

### Quick start

```bash
# Copy the template and fill in your values
cp appsettings.json appsettings.local.json
```

Edit `appsettings.local.json`:
```json
{
  "AzureOpenAI": {
    "Endpoint": "https://your-resource.openai.azure.com/",
    "Model": "gpt-5.4-mini",
    "Key": ""
  },
  "Telegram": {
    "BotToken": "123456:ABC-your-token",
    "Voice": {
      "TranscriptionConcurrency": 2
    }
  },
  "AzureSpeech": {
    "Endpoint": "https://your-resource.cognitiveservices.azure.com/",
    "Key": "",
    "ApiVersion": "2025-10-15",
    "Locales": [ "de-DE", "en-US" ]
  },
  "DotClaw": {
    "Heartbeat": false,
    "HeartbeatIntervalSeconds": 45,
    "ToolMode": "sandboxmcp",
    "WebSearch": true,
    "ApprovalTools": [ "send_message" ]
  }
}
```

Azure service keys are optional. When `AzureOpenAI:Key` or `AzureSpeech:Key` is empty, DotClaw uses
`DefaultAzureCredential`, so local development can use `az login` and Azure hosting can use managed
identity. If you provide a key, DotClaw uses key-based authentication for that service instead.

## Run

Restore once, then decide which command/tool execution mode to use before picking a frontend. Both
frontends share the same agent, configuration, and workspace.

```bash
dotnet restore
```

### 1) Pick a tool execution mode

Configure `DotClaw:ToolMode` with one of:

| Value | Behavior |
| --- | --- |
| `cmd` | Runs the built-in C# tools directly on the host. This is cmd without a sandbox. |
| `sandboxmcp` | Runs tools through the Node/TypeScript MCP sidecar wrapping `@microsoft/mxc-sdk`. This is the default. |
| `csharp-sandbox` | Runs tools in-process through `Sabbour.Mxc.Sdk`, without the MCP/Node sidecar. |

The legacy `DotClaw:Sandbox` boolean is still accepted when `DotClaw:ToolMode` is not set:
`true` maps to `sandboxmcp`, and `false` maps to `cmd`.

#### `sandboxmcp` mode (default)

By default DotClaw runs its `exec` tool inside a
[**MXC**](https://github.com/microsoft/mxc) (Microsoft eXecution Container) process sandbox instead of
directly on the host. The **Node/TypeScript MCP server** (`DotClaw.SandboxMcp/`) wraps
`@microsoft/mxc-sdk` and DotClaw consumes it over stdio MCP.


**Sandbox on (default).** Build the Node sandbox server once — this works for both the CLI and the
Telegram gateway:

```powershell
cd DotClaw.SandboxMcp
npm install
npm run build      # → dist/server.js
```

The `@microsoft/mxc-sdk` npm package bundles the native `wxc-exec.exe`, so no Rust build is required.
C# locates the server at `DotClaw.SandboxMcp/dist/server.js` by default; override with
`DOTCLAW_SANDBOX_MCP_DIR`. See [the MXC requirements](#for-mxc) above for the one-time host prep.

#### `csharp-sandbox` mode

Use the experimental in-process .NET MXC SDK path by setting:

```json
{
  "DotClaw": {
    "ToolMode": "csharp-sandbox"
  }
}
```

This keeps the same tool surface (`exec`, `read_file`, `write_file`) but calls `Sabbour.Mxc.Sdk`
directly from the DotClaw process instead of spawning the MCP sidecar. It still requires the MXC
native executor and host prep described in [For MXC](#for-mxc).

#### `cmd` mode

Skip MXC and run tools directly on the host by setting:

```json
{
  "DotClaw": {
    "ToolMode": "cmd"
  }
}
```


### 2) Pick a frontend

#### a) CLI (interactive console)

```bash
cd DotClaw
dotnet run
```

Chat with the agent directly in your terminal.

#### b) Telegram gateway

```bash
cd DotClaw.Telegram
dotnet run
```

Connects to your bot using `Telegram:BotToken` and serves chat (and voice notes, if Azure Speech is
configured). Run this instead of the CLI when you want to talk to DotClaw from your phone.

## Web search

DotClaw ships with built-in web search via [Parallel](https://parallel.ai/)'s **hosted Search MCP**
server (`https://search.parallel.ai/mcp`) — the same free provider OpenClaw uses for `parallel-free`.
It is **free to use anonymously**, requires **no account or API key**, and is **on by default** for
both the CLI and the Telegram gateway, in every tool execution mode.

The remote server exposes two tools to the agent:

| Tool | Purpose |
| --- | --- |
| `web_search` | General-purpose web search returning LLM-optimized, ranked excerpts across multiple sources. |
| `web_fetch` | Pulls token-efficient markdown from a specific URL (use after `web_search` narrows candidates). |

Unlike the `exec`/`read_file`/`write_file` tools, web search is a plain network call and does **not**
run through the MXC sandbox, so it works the same regardless of `DotClaw:ToolMode`.

**Fail-soft.** If Parallel's MCP endpoint can't be reached at startup, DotClaw prints a warning and
simply starts without the web tools — agent startup is never blocked.

Disable it by setting `DotClaw:WebSearch` to `false` (or the `DOTCLAW_WEB_SEARCH` env var):

```json
{
  "DotClaw": {
    "WebSearch": false
  }
}
```

Point it at a compatible/proxy MCP endpoint with the `DOTCLAW_WEBSEARCH_MCP_URL` environment
variable (defaults to `https://search.parallel.ai/mcp`).

## Tracing (OpenTelemetry → Aspire dashboard)

DotClaw can export **OpenTelemetry traces** for every agent turn so you can watch the Microsoft
Agent Framework's GenAI activity in the [**Aspire dashboard**](https://aspiredashboard.com/). Set
`DotClaw:Otel:Enabled = true` to turn it on (off by default); `DotClaw:Otel:CaptureMessageContent`
(default `true`) controls whether spans include the LLM request/response content — the Agent
Framework's `EnableSensitiveData` flag.

### Start the Aspire dashboard

Install the [Aspire CLI](https://learn.microsoft.com/dotnet/aspire/cli/install) and run the
standalone dashboard:

```bash
aspire dashboard run
```

It serves the UI on `http://localhost:18888` (open the login URL it prints) and ingests OTLP on
`4317` (gRPC) and `4318` (HTTP) — the defaults DotClaw exports to. The dashboard is optional: if it
isn't running, the CLI and Telegram gateway work exactly as before and simply emit no traces.

