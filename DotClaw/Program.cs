using DotClaw.Agent;
using DotClaw.Runtime;
using DotClaw.Session;
using DotClaw.Tools;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Spectre.Console;

const int MaxHistoryTokens = 16000;

// ── OpenTelemetry tracing (opt-in) ─────────────────────────────
// Exports agent + chat-completion GenAI spans over OTLP (e.g. to the Aspire dashboard). No-op when
// DotClaw:Otel:Enabled is false. Kept alive for the whole process; disposal flushes pending spans.
using var tracerProvider = Telemetry.TryInitialize();

// ── Build the MAF Agent (via shared factory) ───────────────────
var approvalPolicy = ApprovalPolicy.FromConfiguration(defaults: [MessagingTools.ToolName]);
await DotClawAgentFactory.LogStartupStatusAsync(approvalPolicy);
var (agent, memory) = await DotClawAgentFactory.CreateAsync(
    approvalPolicy: approvalPolicy);

// ── Session ────────────────────────────────────────────────────
var sessionStore = new SessionManager("cli:default");

// History lives in ONE place: our own savedHistory (persisted to JSONL, replayed below) which we
// pass in full on every turn. The agent session must therefore be created FRESH per turn — a
// ChatClientAgent's session carries an in-memory ChatHistoryProvider that accumulates each run's
// input+response and then MERGES that with whatever messages we pass next time. Reusing one session
// across turns while also replaying the full history double-feeds every prior message, so the model
// sees its last reply twice and parrots it verbatim. Mirrors AgentRunner (Telegram), which already
// creates a session per turn.

// Replay saved history as input messages for the first call
var savedHistory = new List<ChatMessage>();
foreach (var entry in sessionStore.Load())
{
    if (!entry.TryGetProperty("role", out var roleProp)) continue;
    var role = roleProp.GetString();
    var content = entry.TryGetProperty("content", out var contentProp)
        ? contentProp.GetString() ?? "" : "";
    if (role is "user") savedHistory.Add(new ChatMessage(ChatRole.User, content));
    else if (role is "assistant") savedHistory.Add(new ChatMessage(ChatRole.Assistant, content));
}

// ── Single-shot mode ───────────────────────────────────────────
if (args.Length > 0)
{
    var userMessage = string.Join(" ", args);
    savedHistory.Add(new ChatMessage(ChatRole.User, userMessage));
    var trimmed = HistoryTrimmer.Trim(savedHistory, MaxHistoryTokens);
    var agentSession = await agent.CreateSessionAsync();
    var response = await RunWithApprovalsAsync(agent, trimmed, agentSession);
    var reply = FinalAssistantText(response);
    ShowWebSearchCalls(response);
    ShowResponse(reply);
    sessionStore.Append([
        new { role = "user", content = userMessage },
        new { role = "assistant", content = reply },
    ]);
    return;
}

// ── Heartbeat: ambient memory safety-net ───────────────────────
// A background timer that periodically runs a silent "anything worth remembering?" turn against the
// live history, so the agent can persist durable user facts to USER.md/MEMORY.md even when the user
// has gone quiet. It shares `turnLock` with the interactive loop, so a heartbeat turn and a user turn
// never run — or write workspace files — concurrently. Unlike cron it never speaks to the user: its
// only effect is the workspace writes its tools perform mid-turn. A HEARTBEAT_OK (or empty) reply
// means "nothing new"; any other reply is swallowed rather than printed.
var turnLock = new SemaphoreSlim(1, 1);
using var heartbeatCts = new CancellationTokenSource();

if (DotClawConfig.HeartbeatEnabled)
{
    var (heartbeatAgent, heartbeatMemory) = await DotClawAgentFactory.CreateAsync(
        source: TurnSource.Heartbeat);
    _ = Task.Run(() => HeartbeatLoopAsync(
        heartbeatAgent, heartbeatMemory, savedHistory, turnLock,
        DotClawConfig.HeartbeatInterval, MaxHistoryTokens, heartbeatCts.Token));
}

// ── Interactive loop ───────────────────────────────────────────
var heartbeatNote = DotClawConfig.HeartbeatEnabled
    ? $" — [dim]🫀 memory heartbeat every {DotClawConfig.HeartbeatInterval.TotalSeconds:0}s[/]"
    : "";
AnsiConsole.MarkupLine($"[bold]🦞 DotClaw AI Assistant[/] — type [dim]exit[/] or [dim]quit[/] to stop{heartbeatNote}\n");

while (true)
{
    AnsiConsole.Markup("[bold cyan]you[/] ");
    var userInput = Console.ReadLine()?.Trim();

    if (string.IsNullOrEmpty(userInput)) continue;
    if (userInput.Equals("exit", StringComparison.OrdinalIgnoreCase) ||
        userInput.Equals("quit", StringComparison.OrdinalIgnoreCase))
    {
        heartbeatCts.Cancel();
        AnsiConsole.MarkupLine("[dim]Goodbye.[/]");
        break;
    }

    // Serialize against the heartbeat so the two never run — or write workspace files — at once.
    await turnLock.WaitAsync();
    try
    {
        savedHistory.Add(new ChatMessage(ChatRole.User, userInput));

        // Trim history to stay within token budget before each API call
        var trimmed = HistoryTrimmer.Trim(savedHistory, MaxHistoryTokens);
        // Fresh session per turn — see the note above where savedHistory is declared. The same session
        // is reused only within this turn (across approval round-trips) by RunWithApprovalsAsync.
        var agentSession = await agent.CreateSessionAsync();
        var response = await RunWithApprovalsAsync(agent, trimmed, agentSession);

        var responseText = FinalAssistantText(response);
        savedHistory.Add(new ChatMessage(ChatRole.Assistant, responseText));

        ShowWebSearchCalls(response);
        ShowResponse(responseText);
        sessionStore.Append([
            new { role = "user", content = userInput },
            new { role = "assistant", content = responseText },
        ]);
    }
    finally
    {
        turnLock.Release();
    }
    AnsiConsole.WriteLine();
}

// ── Helpers ────────────────────────────────────────────────────

// The ambient heartbeat loop (interactive mode only). Every `interval`, it runs one silent agent turn
// whose sole purpose is to let the model persist anything new worth remembering — it produces no
// console output of its own. Serialized with user turns via `turnLock`. A null prompt means
// HEARTBEAT.md has no actionable (non-comment) content, so the tick is skipped. Exceptions are
// swallowed so a transient failure (e.g. a network blip) never tears down the CLI.
static async Task HeartbeatLoopAsync(
    AIAgent heartbeatAgent, WorkspaceMemoryProvider heartbeatMemory,
    List<ChatMessage> history, SemaphoreSlim turnLock,
    TimeSpan interval, int maxHistoryTokens, CancellationToken ct)
{
    using var timer = new PeriodicTimer(interval);
    try
    {
        while (await timer.WaitForNextTickAsync(ct))
        {
            // Re-read HEARTBEAT.md each tick so live edits take effect without a restart.
            var prompt = AgentRunner.BuildHeartbeatPrompt(heartbeatMemory);
            if (prompt is null)
                continue; // empty / comments-only — nothing to check.

            await turnLock.WaitAsync(ct);
            try
            {
                // Snapshot the live transcript (Trim returns a copy) and append the heartbeat
                // instruction as a system turn — `history` itself is left untouched.
                var snapshot = HistoryTrimmer.Trim(history, maxHistoryTokens);
                snapshot.Add(new ChatMessage(ChatRole.System, prompt));

                // Fresh session per tick (avoids the double-feed described in the main loop). The
                // heartbeat agent is built with ApprovalPolicy.None, so any write_file runs ungated
                // and silently; we intentionally ignore the reply and never print it.
                var session = await heartbeatAgent.CreateSessionAsync();
                await heartbeatAgent.RunAsync(snapshot, session);
            }
            finally
            {
                turnLock.Release();
            }
        }
    }
    catch (OperationCanceledException) { /* CLI shutting down — expected. */ }
    catch (Exception ex)
    {
        AnsiConsole.MarkupLine($"[dim]🫀 heartbeat stopped: {Markup.Escape(ex.Message)}[/]");
    }
}

// Returns only the final assistant message's text. AgentResponse.Text concatenates the text of
// every message produced during a turn — including intermediate narration the model emits alongside
// its tool calls — so multi-step (tool-using) turns would otherwise render the reply twice. The
// user-facing answer is the last assistant message that actually carries text.
static string FinalAssistantText(AgentResponse response)
{
    var text = response.Messages
        .Where(m => m.Role == ChatRole.Assistant)
        .Select(m => m.Text)
        .LastOrDefault(t => !string.IsNullOrWhiteSpace(t));
    return text ?? response.Text ?? "";
}

// Runs the agent and resolves any human-in-the-loop approval requests synchronously,
// re-running on the same session until a final (approval-free) response is produced.
static async Task<AgentResponse> RunWithApprovalsAsync(
    AIAgent agent, IList<ChatMessage> messages, AgentSession session)
{
    var response = await agent.RunAsync(messages, session);

    while (true)
    {
        var requests = response.Messages
            .SelectMany(m => m.Contents)
            .OfType<ToolApprovalRequestContent>()
            .ToList();
        if (requests.Count == 0)
            return response;

        var approvals = new List<AIContent>();
        foreach (var req in requests)
            approvals.Add(req.CreateResponse(PromptApproval(req)));

        // The pending approval requests already live in the session from the run above,
        // so we only need to send the user's decision(s) back on the same session.
        response = await agent.RunAsync(
            new ChatMessage(ChatRole.User, approvals), session);
    }
}

static bool PromptApproval(ToolApprovalRequestContent request)
{
    var call = request.ToolCall as FunctionCallContent;
    var name = call?.Name ?? "tool";

    var grid = new Grid();
    grid.AddColumn(new GridColumn().NoWrap());
    grid.AddColumn();
    grid.AddRow("[bold]tool[/]", $"[white]{Markup.Escape(name)}[/]");
    if (call?.Arguments is { } args)
        foreach (var (key, value) in args)
            grid.AddRow($"[dim]{Markup.Escape(key)}[/]", Markup.Escape(value?.ToString() ?? ""));

    var panel = new Panel(grid)
    {
        Header = new PanelHeader("[bold yellow]🔐 approval required[/]", Justify.Left),
        Border = BoxBorder.Rounded,
        BorderStyle = new Style(Color.Yellow),
    };
    AnsiConsole.Write(panel);

    return AnsiConsole.Confirm($"Approve [bold]{Markup.Escape(name)}[/]?", defaultValue: false);
}

static void ShowResponse(string? text)
{
    var panel = new Panel(Markup.Escape(text ?? ""))
    {
        Header = new PanelHeader("[bold green]assistant[/]", Justify.Left),
        Border = BoxBorder.Rounded,
        BorderStyle = new Style(Color.Green),
    };
    AnsiConsole.Write(panel);
}

// Surfaces any web_search/web_fetch tool calls the agent made during the turn, so the user can see
// when DotClaw reached out to the web. The CLI turn isn't streamed, so these are printed after the
// run completes, just before the assistant's reply. Web search being off/unavailable yields no
// loaded tool names, so nothing is shown.
static void ShowWebSearchCalls(AgentResponse response)
{
    var calls = response.Messages
        .SelectMany(m => m.Contents)
        .OfType<FunctionCallContent>()
        .Where(c => WebSearchTools.IsWebSearchTool(c.Name))
        .ToList();

    foreach (var call in calls)
    {
        var args = FormatToolArgs(call.Arguments);
        var suffix = string.IsNullOrEmpty(args) ? "" : $" [dim]{Markup.Escape(args)}[/]";
        AnsiConsole.MarkupLine($"  [magenta]🔎 web[/] [white]{Markup.Escape(call.Name)}[/]{suffix}");
    }
}

static string FormatToolArgs(IDictionary<string, object?>? arguments)
{
    if (arguments is null || arguments.Count == 0)
        return "";

    static string Compact(string value)
    {
        var oneLine = value.Replace("\r", " ").Replace("\n", " ").Trim();
        return oneLine.Length > 120 ? oneLine[..117] + "..." : oneLine;
    }

    return string.Join("  ", arguments
        .Select(kv => $"{kv.Key}: {Compact(kv.Value?.ToString() ?? "")}"));
}
