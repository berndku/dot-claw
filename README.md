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
- Node.js ≥ 18 
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
    "Sandbox": true,
    "ApprovalTools": [ "send_message" ]
  }
}
```

Azure service keys are optional. When `AzureOpenAI:Key` or `AzureSpeech:Key` is empty, DotClaw uses
`DefaultAzureCredential`, so local development can use `az login` and Azure hosting can use managed
identity. If you provide a key, DotClaw uses key-based authentication for that service instead.

## Run

Restore once, then decide whether to use the MXC sandbox before picking a frontend. Both frontends
share the same agent, configuration, and workspace.

```bash
dotnet restore
```

### 1) The MXC sandbox (recommended, default)

By default DotClaw runs its `exec` tool inside a
[**MXC**](https://github.com/microsoft/mxc) (Microsoft eXecution Container) process sandbox instead of
directly on the host. MXC has no .NET SDK — only a TypeScript one — so a tiny **Node/TypeScript MCP
server** (`DotClaw.SandboxMcp/`) wraps `@microsoft/mxc-sdk` and DotClaw consumes it over stdio MCP.


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

**Sandbox off.** Skip the Node build and run tools directly on the host by setting `DotClaw:Sandbox`
to `false` in `appsettings.local.json`.


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

