using DotClaw.Agent;
using DotClaw.Session;
using DotClaw.Tools;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Spectre.Console;

const int MaxHistoryTokens = 16000;

// ── Build the MAF Agent (via shared factory) ───────────────────
var (agent, memory) = await DotClawAgentFactory.CreateAsync(
    approvalPolicy: ApprovalPolicy.FromEnvironment(defaults: [MessagingTools.ToolName]));

// ── Session ────────────────────────────────────────────────────
var sessionStore = new SessionManager("cli:default");
AgentSession agentSession = await agent.CreateSessionAsync();

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
    var response = await RunWithApprovalsAsync(agent, trimmed, agentSession);
    ShowResponse(response.Text);
    sessionStore.Append([
        new { role = "user", content = userMessage },
        new { role = "assistant", content = response.Text ?? "" },
    ]);
    return;
}

// ── Interactive loop ───────────────────────────────────────────
AnsiConsole.MarkupLine("[bold]🦞 DotClaw AI Assistant[/] — type [dim]exit[/] or [dim]quit[/] to stop\n");

while (true)
{
    AnsiConsole.Markup("[bold cyan]you[/] ");
    var userInput = Console.ReadLine()?.Trim();

    if (string.IsNullOrEmpty(userInput)) continue;
    if (userInput.Equals("exit", StringComparison.OrdinalIgnoreCase) ||
        userInput.Equals("quit", StringComparison.OrdinalIgnoreCase))
    {
        AnsiConsole.MarkupLine("[dim]Goodbye.[/]");
        break;
    }

    savedHistory.Add(new ChatMessage(ChatRole.User, userInput));

    // Trim history to stay within token budget before each API call
    var trimmed = HistoryTrimmer.Trim(savedHistory, MaxHistoryTokens);
    var response = await RunWithApprovalsAsync(agent, trimmed, agentSession);

    var responseText = response.Text ?? "";
    savedHistory.Add(new ChatMessage(ChatRole.Assistant, responseText));

    ShowResponse(responseText);
    sessionStore.Append([
        new { role = "user", content = userInput },
        new { role = "assistant", content = responseText },
    ]);
    AnsiConsole.WriteLine();
}

// ── Helpers ────────────────────────────────────────────────────

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
