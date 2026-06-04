# 🦞 DotClaw

A minimal .NET clone of [OpenClaw](https://docs.openclaw.ai/) — a personal AI assistant framework. Built for **study purposes** using C# and the [Microsoft Agent Framework (MAF)](https://learn.microsoft.com/dotnet/ai/microsoft-extensions-ai).

The goal is to learn four core concepts from OpenClaw by rebuilding them in .NET:

| Part | Concept | What You'll Learn |
|------|---------|-------------------|
| 1 | **Agent Loop + Tools** | LLM ↔ tool execution loop — the core of any AI agent |
| 2 | **Personality** | Workspace files (Soul, Identity, Bootstrap) that give the agent character |
| 3 | **Channels** | Multiple surfaces (CLI, Telegram) sharing one agent core |
| 4 | **Heartbeat** | Cron-based proactive behavior *(planned)* |

---

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- [GitHub CLI](https://cli.github.com/) (`gh`) — logged in (`gh auth login`)
- [Telegram](https://telegram.org/) app (for Part 3)

> **No API keys needed.** DotClaw uses [GitHub Models](https://github.com/marketplace/models) (GPT-4o) — authentication is resolved automatically from `gh auth token`.

---

## Quick Start

```bash
git clone https://github.com/berndku/dot-claw.git
cd dot-claw
dotnet restore
```

### Run the CLI agent

```bash
cd DotClaw.CLI
dotnet run
```

On first run, the agent will introduce itself and ask you to set up its personality — name, vibe, emoji. This is the **Bootstrap** flow (Part 2).

You can also run in single-shot mode:

```bash
dotnet run -- "what files are in the current directory"
dotnet run -- "write hello world to a file then read it back"
```

---

## Part 1 — Agent Loop + Tools

The agent loop is where the LLM and tools work together:

1. You send a message
2. The LLM (GPT-4o) decides whether to respond or call a tool
3. MAF executes the tool and feeds the result back to the LLM
4. This repeats until the LLM returns a plain text response

**Key insight:** MAF handles the entire loop automatically. No manual iteration needed:

```csharp
var agent = client
    .GetChatClient(ModelId)
    .AsIChatClient()
    .AsAIAgent(instructions: systemPrompt, name: "DotClaw", tools: tools);

var response = await agent.RunAsync(history, session);  // ← one call does it all
```

### Tools

Tools are plain C# methods with `[Description]` attributes in `DotClaw/Tools/AgentTools.cs`:

- **`ReadFile`** — read files from disk
- **`WriteFile`** — write files to disk (creates directories as needed)
- **`Exec`** — run shell commands (with dangerous-pattern blocking for safety)

**Try it:** Ask the agent to *"list all files in the current directory"* or *"create a file called hello.txt with some content, then read it back"*.

---

## Part 2 — Personality

OpenClaw gives agents character through workspace files. DotClaw does the same:

| File | Purpose |
|------|---------|
| `SOUL.md` | Core personality — tone, values, boundaries |
| `IDENTITY.md` | Name, creature type, emoji, vibe |
| `USER.md` | Info about you — name, timezone, preferences |
| `BOOTSTRAP.md` | First-run script — triggers the personality setup conversation |
| `MEMORY.md` | Persistent notes the agent writes to remember things |
| `AGENTS.md` | Multi-agent definitions *(future)* |

These files live in `~/.dotclaw/workspace/` and are loaded into the system prompt on every session. The agent can read and write them using its tools — this is how personality persists across sessions.

**Try it:** After the bootstrap conversation, open `~/.dotclaw/workspace/SOUL.md` to see what the agent wrote. Edit it manually and restart — the agent's personality will change.

---

## Part 3 — Channels (Telegram Gateway)

The same agent core runs on multiple surfaces. The CLI and Telegram bot share `DotClawAgentFactory.CreateAsync()` — the only difference is the input/output transport.

### Setting Up a Telegram Bot

1. Open Telegram and talk to **[@BotFather](https://t.me/BotFather)**
2. Send `/newbot` and follow the prompts to create a bot
3. Copy the bot token BotFather gives you

### Running the Gateway

```bash
cd DotClaw.Gateway
dotnet user-secrets set TELEGRAM_BOT_TOKEN "your-token-from-botfather"
dotnet run
```

The gateway connects via long-polling and listens for messages. Each Telegram chat gets its own session with full tool access.

**Try it:** Send your bot a message on Telegram. Then send the same message via the CLI. Both channels share the same personality files but maintain separate conversation histories.

---

## Part 4 — Heartbeat

Proactive behavior — the agent wakes up on a timer and acts without being asked. This mirrors OpenClaw's heartbeat system.

### How It Works

A `HeartbeatRunner` fires every 30 minutes (configurable). On each tick it:

1. Reads `~/.dotclaw/workspace/HEARTBEAT.md` for pending tasks
2. Skips the tick if the file is effectively empty (only headers, comments, blank lines)
3. Sends a synthetic prompt to a fresh agent session: *"The heartbeat has fired. Review HEARTBEAT.md and act on any pending tasks."*
4. Filters the response — if the agent replies `HEARTBEAT_OK`, nothing is delivered
5. Otherwise delivers the message to the active channel (CLI panel or Telegram chat)

### Editing HEARTBEAT.md

Open `~/.dotclaw/workspace/HEARTBEAT.md` and add tasks under the `## Tasks` section:

```markdown
## Tasks
- Remind me to stretch every hour
- Check if any new GitHub notifications
```

Leave the section empty (or comment out tasks with `<!-- -->`) to silence the heartbeat.

**Try it:** Add a task to HEARTBEAT.md, then wait for the next tick (or restart the app). The agent will proactively reach out with a response.

---

## Architecture

```
DotClaw/                      ← Shared library (agent core)
  Agent/
    DotClawAgentFactory.cs    → Shared agent creation (used by all channels)
    ContextBuilder.cs         → System prompt assembly from workspace files
    HeartbeatRunner.cs        → Timer-driven proactive agent behavior
    MemoryManager.cs          → Workspace seeding + file reading
  Tools/
    AgentTools.cs             → read_file, write_file, exec (plain C# methods)
  Session/
    SessionManager.cs         → JSONL conversation persistence
  WorkspaceTemplates/         → Template files seeded on first run

DotClaw.CLI/                  ← CLI channel
  Program.cs                  → Interactive CLI + single-shot mode

DotClaw.Gateway/              ← Telegram channel
  Program.cs                  → Long-polling bot, per-chat sessions
```

### How Authentication Works

Zero-config. DotClaw resolves a GitHub token automatically:

1. Checks `GITHUB_TOKEN` environment variable
2. Falls back to `gh auth token` (GitHub CLI)

GitHub Models provides free GPT-4o access for GitHub users.

### Data Storage

Everything lives under `~/.dotclaw/`:

```
~/.dotclaw/
  workspace/          ← Personality files (SOUL.md, USER.md, etc.)
  sessions/           ← Conversation history (JSONL per session)
```

---

## Further Reading

- [OpenClaw Documentation](https://docs.openclaw.ai/) — the original project this is based on
- [Microsoft Agent Framework](https://learn.microsoft.com/dotnet/ai/microsoft-extensions-ai) — the .NET AI framework used here
- [GitHub Models](https://github.com/marketplace/models) — free LLM access via GitHub
