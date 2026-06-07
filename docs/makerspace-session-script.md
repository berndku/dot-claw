# DotClaw MakerSpace Session — "Meet Link, Your AI Butler"

## Session Overview

**Duration:** ~60 minutes
**Audience:** ~10 people, mixed (devs + non-devs)
**Format:** **Watch-and-follow.** The repo is handed out **pre-built** — nobody writes code. Attendees add two secrets (Azure OpenAI access + a Telegram bot) and follow along on their phones while the presenter narrates the architecture and runs the live demos.
**Goal:** Understand how a real personal-agent (OpenClaw) works by walking through **DotClaw**, a C#/.NET 9 rebuild on the **Microsoft Agent Framework (MAF)** — agent loop, soul/memory, gateway, proactive butlering (cron + heartbeat), and sandboxed tools.

**The star of the show:** **Link** — a butler. *Submissive + funny.* ("As you wish, sir." / "Right away, sir — though I must say, bold choice.") His persona lives in `SOUL.md` / `IDENTITY.md`, and he *looks after you* via a heartbeat and scheduled reminders.

---

## Prerequisites (send with the invitation!)

Attendees need BEFORE the session:
- [ ] **.NET 9 SDK** installed (`dotnet --version` → 9.x)
- [ ] **Azure CLI** installed + `az login` done, with access to the workshop's Azure OpenAI resource (presenter shares how). DotClaw authenticates with `DefaultAzureCredential` — no API keys in code.
- [ ] **The DotClaw repo**, cloned and building (`dotnet build DotClaw.sln`).
- [ ] **Telegram** app on phone + a bot created via **@BotFather** (`/newbot`, takes 30 seconds — keep the token handy).

### One-time auth check
```powershell
dotnet --version    # 9.x
az account show     # confirms you're logged in
```

### The two secrets each attendee sets
```powershell
$env:TELEGRAM_BOT_TOKEN = "your-token-from-botfather"
# Azure OpenAI endpoint/model live in DotClawAgentFactory.cs (Endpoint, ModelDeployment).
# Auth is via `az login` (DefaultAzureCredential) — nothing else to set.
```

> **Note for the presenter:** the Azure OpenAI endpoint + deployment are constants in
> `DotClaw/Agent/DotClawAgentFactory.cs`. Either grant attendees access to the shared resource,
> or have them point those two constants at their own.

---

## Running order (~60 min; setup runs quietly in the background early)

| # | Part | Time | Attendees |
|---|------|------|-----------|
| 1 | The Agent Loop (concept) | 5 min | Listen |
| 2 | Personality & Memory — meet Link | 10 min | Watch + bootstrap their own |
| 3 | The Telegram Gateway | 10 min | Message their bot |
| 4 | Link Looks After You — Cron + Heartbeat | 10 min | Watch + one live reminder |
| 4.5 | Approve Before It Acts (HITL) | 6 min | Watch + tap Approve/Deny |
| 5 | Tools & the Sandbox (MXC) | 10 min | Watch (presenter-only) |
| 6 | Wrap-up & what's next | 5 min | Discuss |

*(Buffer ~10 min for questions / catch-up across the session.)*

---

## Part 1 — The Agent Loop (5 min, concept only)

This is well understood now, so keep it to one diagram and one sentence.

```
┌──────────────────────────────────────────────┐
│                  AGENT LOOP                    │
│   User ─► LLM ─► tool call? ─► run tool        │
│             ▲                      │           │
│             └──── result fed back ─┘           │
│             LLM decides: call again, or reply  │
└──────────────────────────────────────────────┘
```

> "The **LLM decides** what to do — it picks tools, reads results, and decides when it's done. MAF runs that loop for us. Everything else today is what you *wrap around* that loop to turn a chatbot into someone who looks after you."

One sentence on lineage: **OpenClaw** is an open-source personal AI assistant (soul, memory, tools, channels, proactive scheduling). **DotClaw** is our C# rebuild on the Microsoft Agent Framework — we're re-implementing OpenClaw's *coolest concepts*, not every channel.

---

## Part 2 — Personality & Memory: meet Link (10 min)

### The concept (3 min)
> "Our agent works, but a generic assistant is just a search engine with extra steps. OpenClaw's trick is a **soul** — a set of plain Markdown files the agent reads fresh every turn, and **writes to itself**."

The workspace lives at `~/.dotclaw/workspace/`:
- `SOUL.md` — values, tone, how to behave
- `IDENTITY.md` — name, creature, vibe, emoji
- `USER.md` — who the human is
- `MEMORY.md` — durable notes the agent jots down
- `BOOTSTRAP.md` — a first-run "who are we?" script (deletes itself when done)
- `HEARTBEAT.md` — the rule for the proactive pulse (Part 4) — *excluded from normal per-turn context*

### Live bootstrap → Link (4 min) — the wow
**Everyone:** delete `~/.dotclaw/` (fresh slate), then run the CLI (`dotnet run` in `DotClaw/`).

On first run the agent reads `BOOTSTRAP.md` and wakes up *already leaning butler*:

> "Ah — I appear to have just come online. Link, at your service, sir. I don't yet know the first thing about you. Shall we fix that?"

Attendees chat for a few lines — give it their name, timezone, what they care about. Link **writes `IDENTITY.md` and `USER.md` himself** using the `write_file` tool, then deletes `BOOTSTRAP.md`.

> "Look at `~/.dotclaw/workspace/IDENTITY.md` — *he wrote that.* The persona is reliable on every machine because `BOOTSTRAP.md` stacks the deck toward Link, but the files are written live."

### How it's implemented (3 min)
Show the three pieces:
- **`WorkspaceMemoryProvider`** — seeds the templates on first run and, as an MAF `AIContextProvider`, injects the workspace files **fresh on every invocation**. So when Link edits `MEMORY.md` mid-session, the *next* turn already sees it — no rebuild.
- **`ContextBuilder`** — wraps the files (plus current time) into the system prompt, OpenClaw-style.
- **The files themselves** — memory is just inspectable Markdown the user owns. Writes are LLM-driven via `write_file`.

---

## Part 3 — The Telegram Gateway (10 min)

### The concept (2 min)
> "Same Link, different front door. The agent doesn't know or care whether you reached it from the console or your phone."

### Everyone live (5 min) — second wow
```powershell
$env:TELEGRAM_BOT_TOKEN = "your-token"
dotnet run    # in DotClaw.Telegram/
```
Open Telegram, message your bot. Link replies in character. Things to try:
- "What's my name?" (reads `USER.md`)
- "Remember that I take my coffee black." → he writes `MEMORY.md`
- "Read me my SOUL.md."

> **Everyone shows their phone** — 10 butlers, 10 slightly different Links. 🎩

### How it's implemented (3 min)
Show `DotClaw.Telegram/Program.cs` + `DotClawAgentFactory`:
- A long-poll loop turns each Telegram message into an **`InboundItem`** and writes it to a single **`Channel`**.
- **One consumer** drains that channel and runs the agent turn (`AgentRunner`), so user turns for a chat are serialized and share one session (`telegram-{chatId}` JSONL history).
- The same factory builds the agent for *every* surface; the channel/route is just data.

> "Hold that 'single inbound channel, one consumer' picture — in the next part two *more* producers feed it."

---

## Part 4 — Link Looks After You: Cron + Heartbeat (10 min)

**Theme: proactive butlering.** Two distinct ideas, faithful to OpenClaw:

### Beat A — Cron one-shot (the star, ~4 min)
Have one attendee (or the presenter) message their bot:

> "Link, remind me in 1 minute to stretch."

What happens:
1. The LLM calls the **`cron_add`** tool with `schedule:"in:1m"`, `topic:"stretch"`. The job — **with the chat's route baked in** — is saved to `~/.dotclaw/cron.json`.
2. Link confirms in butler voice ("Consider it done, sir. One minute.").
3. ~60s later the **phone buzzes unprompted** with a freshly-worded reminder.

> "That second buzz wasn't a reply to anything — Link reached out *on his own schedule.*"

**Why it's faithful (1 min narration):** in real OpenClaw an *isolated* cron job spawns its **own throwaway session**, runs the agent turn there, and **delivers itself** (`announce`). DotClaw does exactly this: the due job runs **concurrently** with live chat in a separate `cron-{jobId}` session and calls the message sink itself. The single chat consumer is never blocked.

Optional follow-ups (show control + persistence):
- "Link, every 2 minutes nag me to sit up straight." → recurring `every:2m`
- "What reminders do I have?" → `cron_list`
- "Cancel that one." → `cron_remove` (he stops; the job is gone from `cron.json`, surviving restarts)

### Beat B — the Heartbeat (ambient, ~4 min) — demo on screen
Enable it (presenter machine):
```powershell
$env:DOTCLAW_HEARTBEAT = "on"
$env:DOTCLAW_HEARTBEAT_INTERVAL = "30"   # seconds (default 45)
dotnet run    # DotClaw.Telegram
```
Put the **console on screen**. Every ~30s the heartbeat ticks, runs a turn against `HEARTBEAT.md`'s rule, and — when there's nothing to say — prints **`HEARTBEAT_OK`** and stays silent. *Watch the silence.*

Then **change Link's world**: edit one line in `~/.dotclaw/workspace/USER.md` or `MEMORY.md` (e.g. add `Status: hunched over the keyboard since 9am, no water`). On the **next tick**, Link *decides* it's worth a word and the phone buzzes with one caring-but-cheeky line.

> "Cron is a scheduled errand. The heartbeat is Link's **pulse** — the thing that lets him act, and stay quiet, on his own. The wow isn't a timer firing; it's watching him *deliberate, choose, and exercise restraint.*"

### How they relate (1 min)
- **Cron** = a precise, self-delivering scheduled job (isolated session) → great for "remind me in 1 minute."
- **Heartbeat** = an ambient `PeriodicTimer` that's just **another producer into the same inbound channel** from Part 3 → runs on the one consumer, serialized with your chat. State-aware, not scheduled.
- Keep only **one** live phone-buzz (the cron). The heartbeat's identity on stage is the **console** (tick → `HEARTBEAT_OK` → flip a fact → speak), so the two beats don't look redundant.

*(Implementation note for the curious: `CronService` is a single self-re-arming timer — a port of OpenClaw's `armTimer` — that sleeps until the next due job, clamped to a ~60s watchdog. Concurrent isolated runs are safe because each uses its own session file; the shared **workspace files** are guarded by a reader/writer lock + atomic writes.)*

---

## Part 4.5 — Approve Before It Acts on Your Behalf (6 min)

### The concept (1 min)
So far Link *acts immediately*. But some actions are risky — sending a message, deleting a
file, running a shell command. **Human-in-the-loop (HITL) approval** puts a person in the
loop: the agent **asks first**, and only acts after you say yes.

MAF makes this a one-liner: wrap a tool in **`ApprovalRequiredAIFunction`**. The wrapped tool
is *not* executed — the run returns a `ToolApprovalRequestContent`. You inspect it, ask the
human, and reply with `request.CreateResponse(approved)`. The `FunctionInvokingChatClient`
(already in our pipeline) then either runs the real tool (approved) or backs off (denied).
**Which tools require approval is configuration, not code** — DotClaw reads `DOTCLAW_APPROVAL_TOOLS`
(comma-separated tool names; defaults to `send_message`).

### The demo tool: `send_message` (1 min)
DotClaw ships a self-contained **`send_message`** tool — "text a contact on your behalf." The
send is *simulated*: it prints a line and appends to `~/.dotclaw/outbox.log`, so the side
effect is **visible and only happens after approval**.

### CLI demo (2 min)
```powershell
cd DotClaw
dotnet run -- "text my manager Sarah that I'll be 10 minutes late to standup"
```
Link drafts the message, then **pauses**:
```
┌─ 🔐 approval required ───────────────────────┐
│ tool       send_message                      │
│ recipient  Sarah                             │
│ message    I'll be 10 minutes late to standup│
└──────────────────────────────────────────────┘
Approve send_message? [y/n]:
```
- **y** → `✅ Message sent to Sarah` + a new line in `~/.dotclaw/outbox.log`.
- **n** → Link backs off, **no** outbox line.

**Configurability (the "aha"):** flip the env var and shell commands need approval too:
```powershell
$env:DOTCLAW_APPROVAL_TOOLS = "send_message,exec"
dotnet run -- "run dotnet --version"   # now exec asks first
```

### Telegram demo (2 min) — THE WOW MOMENT 📱
With the gateway running, message your bot:

> "Tell my manager Sarah I'll be 10 minutes late"

Instead of just replying, the bot sends the drafted action with two buttons:

`[ ✅ Approve ]  [ ❌ Deny ]`

Tap **Approve** → it sends (buttons freeze to "✅ Approved", outbox line appears).
Tap **Deny** → Link backs off. **You just approved your agent's action from your phone.**

> Same `CreateResponse(bool)` semantics as the CLI — only the input channel differs (button
> tap vs. console `y/n`). Because the gateway can't block a thread waiting for a tap, the turn
> is *parked*: the approval request becomes a Telegram update, the tap is a second update
> (`CallbackQuery`), and the parked state (in-memory, demo-grade) resumes the agent run on the
> same single consumer as normal turns (so history writes stay serialized). Two caveats: a bot
> restart drops outstanding approvals (handled gracefully as "expired"), and you should resolve
> a pending approval before firing off another message in the same chat — one approval in flight
> at a time keeps the demo crisp.

---

## Part 5 — Tools & the Sandbox: MXC (10 min, presenter-only)

### The concept (3 min)
> "Link can run shell commands. Letting an LLM run `exec` on your real machine is terrifying — so DotClaw runs its tools inside **MXC**, a Windows AppContainer, via a small **Node/TypeScript MCP server**. MAF speaks MCP, so the sandbox can be a *different language* than Link (C#), and each tool call runs in a **fresh, ephemeral** sandbox."

Great secondary point: **polyglot tools via MCP** — the agent (C#) and its tools (TypeScript) are decoupled by a protocol.

**Reliability note:** MXC needs one-time **admin host-prep** + Win11 24H2+, so this is **presenter-only**; attendees stay on `DOTCLAW_SANDBOX=off` (in-process C# tools). Lower risk, still a strong watch-the-screen finale.

### Demo — "Link, prove you're in a box" (6 min)
With sandbox **on** (`DOTCLAW_SANDBOX` unset/on):
1. **Restricted identity** — ask Link to run `whoami` and probe which drives/paths he can see → AppContainer identity + limited view, not your user.
2. **Ephemerality** — Link writes a temp file and reads it back in one call (works); a *second* call can't find it → fresh sandbox each time. *"He has no memory of his own crimes, sir."*
3. **The toggle (the aha)** — flip `DOTCLAW_SANDBOX=off`, rerun the same probe → now it's **your** identity / full access. The visible diff *is* the boundary. *"On = padded room. Off = keys to the house."*

### Honesty slide (1 min)
MXC is early preview; profiles aren't a hardened security boundary yet. This demonstrates the **pattern** of sandboxed tool execution, not a finished jail.

---

## Part 6 — Wrap-up & what's next (5 min)

### What we walked through
- The agent loop (MAF runs it)
- A **soul** that the agent reads fresh and **writes itself** (live bootstrap → Link)
- One agent, many front doors (the Telegram gateway + single-consumer channel)
- **Proactive butlering** — precise self-delivering **cron** + ambient, state-aware **heartbeat** with restraint
- **Human-in-the-loop approval** — configurable per tool (CLI `y/n` + Telegram Approve/Deny buttons)
- **Sandboxed, polyglot tools** over MCP (MXC)

### What's next (credibility beats)
- **Managed scheduling in the cloud:** Azure AI **Foundry Routines** (Timer/Recurring triggers) — the hosted equivalent of our cron (note: 5-min minimum interval, Foundry-hosted, no heartbeat concept).
- **Durable / eternal orchestrations** (Azure Functions Durable Task for MAF) for an enterprise heartbeat loop.
- **Persisted approvals** — survive a restart (today's pending approvals are in-memory).
- **Sub-agents** — Link spawning child agents for bigger jobs.
- **Hardened MXC** + Azure deployment.

### Resources
- **OpenClaw** (concepts: soul, memory, cron `armTimer`, heartbeat)
- **Microsoft Agent Framework** — `Microsoft.Agents.AI`
- **MCP** — the protocol bridging Link (C#) and the MXC sandbox (TypeScript)

---

## Environment-variable cheat sheet

| Variable | Purpose | Default |
|----------|---------|---------|
| `TELEGRAM_BOT_TOKEN` | Telegram gateway bot token (required for Part 3+) | — |
| `DOTCLAW_APPROVAL_TOOLS` | Comma-separated tool names that require human approval | `send_message` |
| `DOTCLAW_SANDBOX` | `off`/`0`/`false` → in-process C# tools; otherwise MXC sandbox | on |
| `DOTCLAW_HEARTBEAT` | `on`/`1`/`true` → enable the ambient heartbeat | off |
| `DOTCLAW_HEARTBEAT_INTERVAL` | Heartbeat tick interval, seconds | 45 |
| `DOTCLAW_SANDBOX_MCP_DIR` | Override path to the built MXC MCP server | (relative to build) |

Azure OpenAI auth is via `az login` (`DefaultAzureCredential`); endpoint + deployment are constants in `DotClawAgentFactory.cs`.

---

## Presenter tips

1. **Pre-flight the cron beat** once before the room arrives — the 1-minute buzz is the emotional peak; make sure your bot delivers.
2. **Stage the heartbeat flip:** have the `USER.md`/`MEMORY.md` edit ready to paste so the "tick → silence → flip → speak" sequence lands cleanly. Keep `HEARTBEAT.md`'s rule crisp so a clear state change reliably flips it.
3. **Only one live phone-buzz** (cron). Let the heartbeat live on the **console** so the two don't look like the same trick.
4. **Sandbox is presenter-only.** Confirm `DOTCLAW_SANDBOX=off` works for attendees beforehand; demo the toggle yourself.
5. **Fresh slate for bootstrap:** delete `~/.dotclaw/` right before Part 2 so Link wakes up clean.
6. **Reset between runs:** to re-demo cron from scratch, stop the app and clear `~/.dotclaw/cron.json`.
7. **Auth gotcha:** the most common attendee failure is `az login` / no access to the Azure OpenAI resource — check this in the prerequisites, not live.
