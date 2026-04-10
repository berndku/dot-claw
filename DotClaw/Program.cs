using DotClaw.Agent;
using DotClaw.Session;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Spectre.Console;

// ── Build the MAF Agent (via shared factory) ───────────────────
var (agent, memory) = await DotClawAgentFactory.CreateAsync();

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
    var response = await agent.RunAsync(savedHistory, agentSession);
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

    // Single call — MAF AIAgent handles tool loop + session management
    var response = await agent.RunAsync(savedHistory, agentSession);

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

static void ShowResponse(string? text)
{
    var panel = new Panel(text ?? "")
    {
        Header = new PanelHeader("[bold green]assistant[/]", Justify.Left),
        Border = BoxBorder.Rounded,
        BorderStyle = new Style(Color.Green),
    };
    AnsiConsole.Write(panel);
}
