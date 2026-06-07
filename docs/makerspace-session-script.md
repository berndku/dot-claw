# DotClaw MakerSpace Session — "Build Your Own AI Agent in 60 Minutes"

## Session Overview

**Duration:** 60 minutes  
**Audience:** ~10 people, mixed (devs + non-devs), via Teams  
**Format:** Follow-along workshop — presenter builds, participants follow on their own machines  
**Goal:** Understand agent loops, build a working AI agent, chat with it on Telegram

**Key Idea:** Everyone uses GitHub Copilot CLI to generate the code. You don't need to be a C# expert — the AI writes the code for you. Meta! 🤯

---

## Prerequisites (send with invitation!)

Participants need BEFORE the session:
- [ ] **GitHub CLI** installed and logged in (`gh auth login`)
- [ ] **.NET 9 SDK** installed (`dotnet --version`)
- [ ] **VS Code** with C# Dev Kit extension
- [ ] **GitHub Copilot** (CLI or VS Code extension)
- [ ] **Telegram** app on phone + bot created via @BotFather (takes 30 seconds)

### Quick setup check (participants run this before joining):
```powershell
gh auth status          # Should show "Logged in"
dotnet --version        # Should show 9.x
```

### Create Telegram bot (30 seconds):
1. Open Telegram → search for `BotFather`
2. Send `/newbot` → pick a name → pick a username (must end in `bot`)
3. Copy the token — you'll need it in Part 4

---

## Part 1: What is an Agent Loop? (10 min) — PRESENTER TALKS

### The Big Idea (3 min)

> "ChatGPT is a chatbot. An Agent is a chatbot that can DO things."

Show this diagram:

```
┌──────────────────────────────────────────┐
│              AGENT LOOP                  │
│                                          │
│   User ──► LLM ──► Tool Call?            │
│                      │                   │
│                Yes ◄─┘──► No ──► Reply   │
│                 │                        │
│           Execute Tool                   │
│                 │                        │
│           Feed result back to LLM        │
│                 │                        │
│           LLM ──► Tool Call? (repeat)    │
│                                          │
└──────────────────────────────────────────┘
```

Key insight: **The LLM decides what to do.** It picks tools, reads results, decides when it's done.

### What is OpenClaw? (3 min)

- Open-source personal AI assistant (github.com/openclaw)
- Has personality (SOUL.md), memory, tools, multiple chat channels
- **DotClaw** = our C# rebuild, using Microsoft Agent Framework

### The 3 Building Blocks (4 min)

1. **Tools** — ReadFile, WriteFile, Exec (the agent's hands)
2. **Memory** — SOUL.md (personality), USER.md (who you are), session history
3. **Gateway** — How you talk to it (CLI, Telegram, Teams, etc.)

> "We'll build all three today. Everyone on their own machine."

---

## Part 2: Build the Agent Loop (20 min) — EVERYONE CODES

### 2a. Scaffold the project (3 min)

**Everyone runs:**
```powershell
mkdir DotClaw && cd DotClaw
dotnet new console
dotnet add package Microsoft.Agents.AI --prerelease
dotnet add package Microsoft.Extensions.AI
dotnet add package Microsoft.Extensions.AI.OpenAI
dotnet add package Azure.AI.OpenAI --prerelease
dotnet add package Spectre.Console
```

> **Explain while they type:** "Microsoft.Agents.AI is the Microsoft Agent Framework. It handles the tool loop for us."

### 2b. Build the tools (5 min)

> **Everyone prompts Copilot (CLI or VS Code chat):**
>
> "Create a file Tools/AgentTools.cs with three static methods: ReadFile(string path), WriteFile(string path, string content), and Exec(string command). Each should have [Description] attributes. ReadFile reads a file, WriteFile writes content creating dirs as needed, Exec runs a shell command with 60s timeout. Include a static CreateAll() method returning a list of AIFunction via AIFunctionFactory.Create(). Use namespace DotClaw.Tools."

**Presenter shows their result.** Highlight:
- `[Description]` = how the LLM knows what tools do
- `AIFunctionFactory.Create()` = MAF turns plain C# into callable tools
- The LLM **chooses** which tool to call — no if/else needed

### 2c. Wire up the agent (7 min)

> **Everyone prompts:**
>
> "Replace Program.cs with: 1) A ResolveGitHubToken() method that tries GITHUB_TOKEN env var, then falls back to running `gh auth token`. 2) Create an OpenAI client pointing to endpoint https://models.inference.ai.azure.com with model gpt-4o. 3) Use .AsIChatClient().AsAIAgent() with system prompt 'You are DotClaw, a helpful AI assistant with access to file and shell tools.' and tools from AgentTools.CreateAll(). 4) Interactive console loop: read user input, call agent.RunAsync(), print response. Use Spectre.Console for pretty output."

**Wait for everyone to have a compiling project.** Help anyone stuck.

### 2d. First test! (3 min)

**Everyone runs:**
```powershell
dotnet run
```
Then type:
```
What files are in the current directory?
```

The agent should call `Exec("dir")` autonomously. 🎉

> **Everyone:** Look at your console — see the tool call? The LLM decided to use that tool on its own.

### 2e. Self-correction demo (2 min)

Type: `List files using ls`

On Windows, `ls` may fail → the agent retries with `dir`.

> "It made a mistake, saw the error, fixed itself. That's the agent loop."

**Pause — anyone stuck? Help them catch up.**

---

## Part 3: Personality & Memory (15 min) — EVERYONE CODES

### 3a. The concept (3 min)

> "Our agent works, but it's generic. OpenClaw's secret: it has a *soul*."

Explain:
- **SOUL.md** — personality, tone, values
- **IDENTITY.md** — agent's name, emoji
- **USER.md** — about the human
- **BOOTSTRAP.md** — first-run onboarding where agent and human meet

### 3b. Add memory + context (7 min)

> **Everyone prompts:**
>
> "Create Agent/MemoryManager.cs: manages workspace at ~/.dotclaw/workspace/. Constructor seeds template .md files from WorkspaceTemplates/ build output on first run (when SOUL.md doesn't exist). ReadAll() returns Dictionary<string,string> of filename to content for all .md files."

> **Prompt:**
>
> "Create Agent/ContextBuilder.cs: static BuildSystemPrompt(MemoryManager memory) that builds a system prompt string including current UTC time, workspace path, and appends each workspace file as a ## section. If SOUL.md exists, add instruction to embody its persona."

**Presenter shares their template files** (SOUL.md, BOOTSTRAP.md, etc.) via Teams chat. Participants copy them to `WorkspaceTemplates/` folder.

### 3c. Bootstrap! (5 min) — THE WOW MOMENT

**Everyone:** Delete `~/.dotclaw/` if it exists, then `dotnet run`.

The agent reads BOOTSTRAP.md and asks: *"Hey. I just came online. Who am I? Who are you?"*

**Everyone names their own bot!** Give it a personality, a vibe, an emoji.

> "Look at ~/.dotclaw/workspace/IDENTITY.md — it wrote that file itself."

---

## Part 4: Telegram Gateway (10 min) — EVERYONE FOLLOWS

### 4a. The gateway concept (2 min)

> "Same agent, different front door. CLI → Telegram → Teams → anything."

### 4b. Create the Telegram project (3 min)

**Everyone runs:**
```powershell
cd ..
dotnet new console -n DotClaw.Telegram
cd DotClaw.Telegram
dotnet add package Telegram.Bot
dotnet add reference ..\DotClaw\DotClaw.csproj
```

> **Prompt Copilot:**
>
> "Replace Program.cs with a Telegram long-polling bot. Read TELEGRAM_BOT_TOKEN from env var. Use Telegram.Bot package to poll for updates. For each text message, create an agent via DotClawAgentFactory.CreateAsync, load session history, call agent.RunAsync(), send response back. Handle messages concurrently with Task.Run."

### 4c. Live on Telegram! (5 min) — SECOND WOW MOMENT

**Everyone:**
```powershell
$env:TELEGRAM_BOT_TOKEN = "your-token-from-botfather"
dotnet run
```

Open Telegram on phone. Message your bot. It responds. 🎉

**Fun things to try:**
- "What's my name?" (tests USER.md)
- "Read my SOUL.md"
- "Remember that I love pizza" → writes to MEMORY.md
- "Run `dotnet --version`" → tool calling through Telegram!

> **Everyone shows their phone to camera** — 10 bots, 10 personalities. 📱

---

## Part 4.5: Approve Before It Acts on Your Behalf (8 min) — EVERYONE CODES

### The concept (2 min)

So far the agent *acts immediately*. But some actions are risky — sending a message,
deleting a file, running a shell command. **Human-in-the-loop (HITL) approval** puts a
person in the loop: the agent **asks first**, and only acts after you say yes.

MAF makes this a one-liner: wrap a tool in **`ApprovalRequiredAIFunction`**. The tool is
*not* executed — instead the run returns a `ToolApprovalRequestContent`. You inspect it,
ask the human, and reply with `request.CreateResponse(approved)`. The framework's
`FunctionInvokingChatClient` then either runs the real tool (approved) or backs off
(denied). **Which tools require approval is configuration, not code** — DotClaw reads the
env var `DOTCLAW_APPROVAL_TOOLS` (comma-separated tool names; defaults to `send_message`).

### The demo tool: `send_message` (1 min)

DotClaw ships a self-contained **`send_message`** tool — "text a contact on your behalf."
The send is *simulated*: it prints a line and appends to `~/.dotclaw/outbox.log`, so the
side effect is **visible and only happens after approval**.

### CLI demo (3 min)

```powershell
cd DotClaw
dotnet run -- "text my manager Sarah that I'll be 10 minutes late to standup"
```

The agent drafts the message, then **pauses**:

```
┌─ 🔐 approval required ───────────────────────┐
│ tool       send_message                      │
│ recipient  Sarah                             │
│ message    I'll be 10 minutes late to standup│
└──────────────────────────────────────────────┘
Approve send_message? [y/n]:
```

- **y** → `✅ Message sent to Sarah` + a new line in `~/.dotclaw/outbox.log`.
- **n** → the agent backs off, **no** outbox line.

**Configurability (the "aha"):** flip the env var and shell commands now need approval too:

```powershell
$env:DOTCLAW_APPROVAL_TOOLS = "send_message,exec"
dotnet run -- "run dotnet --version"   # now Exec asks first
```

### Telegram demo (2 min) — THE WOW MOMENT 📱

With the gateway running, message your bot:

> "Tell my manager Sarah I'll be 10 minutes late"

Instead of just replying, the bot sends the drafted message with two buttons:

`[ ✅ Approve ]  [ ❌ Deny ]`

Tap **Approve** → it sends (buttons become "✅ Approved", outbox line appears).
Tap **Deny** → it backs off. **You just approved your agent's action from your phone.**

> Same `CreateResponse(bool)` semantics as the CLI — only the input channel differs
> (button tap vs. console `y/n`).

---

## Part 5: Wrap-Up (5 min) — PRESENTER TALKS

### What We Built
- Agent loop with tool calling (Microsoft Agent Framework)
- Personality & memory (bootstrap ritual)
- Telegram gateway (no cloud infra needed)
- Human-in-the-loop approval (CLI `y/n` + Telegram buttons), configurable per tool
- Used AI to build AI 🤖

### What's Next (Teasers)
- Heartbeat — agent reaches out proactively
- Sub-agents — spawning child agents
- Persisted approvals — survive a restart (today's pending approvals are in-memory)
- Production deployment to Azure

### Resources (drop in Teams chat)
- **OpenClaw:** github.com/openclaw
- **Tutorial:** github.com/jcdeichmann/rebuilding-openclaw-tutorial
- **MAF:** nuget.org/packages/Microsoft.Agents.AI
- **GitHub Models:** models.inference.ai.azure.com (free for MS employees)

---

## Timing Summary

| Part | Topic | Duration | Everyone codes? |
|------|-------|----------|-----------------|
| 1 | What is an Agent Loop? | 10 min | No — listen |
| 2 | Build the Agent Loop | 20 min | Yes |
| 3 | Personality & Memory | 15 min | Yes |
| 4 | Telegram Gateway | 10 min | Yes |
| 4.5 | Approval (HITL) | 8 min | Yes |
| 5 | Wrap-Up | 5 min | No — discuss |
| | **Total** | **68 min** | |

---

## Presenter Tips

1. **Share template files** (SOUL.md etc.) via Teams chat at the start of Part 3
2. **Have a "rescue repo"** — if someone is stuck, they can clone the finished project
3. **Delete `~/.dotclaw/`** right before Part 3 for fresh bootstrap
4. **Telegram token** — remind everyone to have it ready (from prerequisites)
5. **Slow typers** — the Copilot prompts are the equalizer. Everyone gets working code regardless of typing speed
6. **If GitHub Models is slow** — have participants use `gpt-4o-mini` as fallback
7. **The bootstrap is the star** — let people share their bot's name and emoji in Teams chat
