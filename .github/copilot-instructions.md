# DotClaw — Copilot Instructions

## What This Project Is

DotClaw is a minimal .NET clone of [OpenClaw](https://docs.openclaw.ai/) — a personal AI assistant framework. It is built for **study purposes only** using C# and the [Microsoft Agent Framework (MAF)](https://learn.microsoft.com/dotnet/ai/microsoft-extensions-ai). The code is intentionally simple and not production-grade.

The goal is to learn four core concepts from OpenClaw by rebuilding them in .NET:

| Part | Concept | OpenClaw Equivalent |
|------|---------|---------------------|
| 1 | **Agent Loop + Tools** — LLM ↔ tool execution loop | Core agent loop |
| 2 | **Personality** — Soul, Identity, Bootstrap files that give the agent character | Workspace personality files |
| 3 | **Channels** — Multiple surfaces (CLI, Telegram) sharing one agent core | Gateway / channel system |
| 4 | **Heartbeat** — Timer-driven proactive behavior | Heartbeat system |

## Architecture

- **`DotClaw/`** — Shared library. Contains the agent factory, tools, context builder, memory manager, heartbeat runner, session persistence, and workspace templates. Referenced by both CLI and Gateway.
- **`DotClaw.CLI/`** — CLI channel. Interactive REPL and single-shot mode. References `DotClaw` as a project dependency.
- **`DotClaw.Gateway/`** — Channel gateway. Currently implements Telegram via long-polling. References `DotClaw` as a project dependency.

## Key Design Decisions

- **MAF handles the tool loop.** We do NOT write a manual tool loop. `AIAgent.RunAsync()` does it all — the LLM calls tools, MAF executes them, feeds results back, and repeats until a plain text response is returned.
- **Tools are plain C# methods** decorated with `[Description]` attributes. They are registered via `AIFunctionFactory.Create()`.
- **GitHub Models** is the LLM backend (GPT-4o). Authentication is via `gh auth token` or `GITHUB_TOKEN` env var — no API keys to manage.
- **Workspace files** (`SOUL.md`, `USER.md`, `BOOTSTRAP.md`, etc.) are seeded on first run and loaded into the system prompt on every session. This is how personality persists.
- **Session history** is stored as JSONL in `~/.dotclaw/sessions/`.
- **Heartbeat** uses a simple `System.Threading.Timer`. `HeartbeatRunner` reads `HEARTBEAT.md`, skips empty task lists, creates a fresh agent session per tick, and delivers non-trivial responses via a callback. Each channel (CLI, Gateway) wires its own delivery function.

## Code Style

- Keep it minimal. This is a learning project — favor clarity over robustness.
- Top-level statements for `Program.cs` files (no `Main` method).
- No dependency injection containers — just static factories and direct construction.
- No unnecessary abstractions. If something is used once, inline it.
- Comments should explain *why*, not *what*.

## When Adding Features

- New tools go in `DotClaw/Tools/AgentTools.cs` as static methods with `[Description]`.
- New channels go in `DotClaw.Gateway/` (or a new project) and reuse `DotClawAgentFactory.CreateAsync()`. Reference `DotClaw` as a project dependency.
- New workspace template files go in `DotClaw/WorkspaceTemplates/` and need to be added to the `MemoryManager.ReadAll()` file list.
- The system prompt is assembled in `ContextBuilder.BuildSystemPrompt()`.

## Do NOT

- Over-engineer. No interfaces for single implementations. No generics for one type.
- Add production concerns (retry policies, structured logging, health checks) unless explicitly asked.
- Change the authentication flow — it must stay zero-config via `gh` CLI.
- Add NuGet packages without a clear reason.
