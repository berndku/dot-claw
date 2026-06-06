namespace DotClaw.Agent;

using Azure.AI.OpenAI;
using Azure.Identity;
using DotClaw.Cron;
using DotClaw.Runtime;
using DotClaw.Tools;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

/// <summary>
/// Shared factory for creating the DotClaw AIAgent.
/// Used by both the CLI (Program.cs) and Teams gateway (DotClawBot.cs).
/// Uses DefaultAzureCredential — log in via `az login` before running.
/// </summary>
public static class DotClawAgentFactory
{
    public const string Endpoint = "https://openai-bgk.openai.azure.com/";
    public const string ModelDeployment = "gpt-5.4-mini";

    /// <summary>
    /// Creates a fully configured AIAgent with tools and system prompt.
    /// Authenticates with DefaultAzureCredential (az login, managed identity, etc.).
    /// </summary>
    public static async Task<(AIAgent Agent, WorkspaceMemoryProvider Memory)> CreateAsync(
        string? channel = null, string? chatId = null,
        CronService? cron = null, TurnSource source = TurnSource.User)
    {
        var client = new AzureOpenAIClient(new Uri(Endpoint), new DefaultAzureCredential());

        var memory = new WorkspaceMemoryProvider();
        var baseInstructions = ContextBuilder.BuildBaseInstructions(memory, channel, chatId);

        var sandboxEnabled = SandboxEnabled();
        var baseTools = sandboxEnabled
            ? await SandboxTools.GetToolsAsync()
            : AgentTools.CreateAll().Cast<AITool>().ToList();

        // Copy: SandboxTools returns a shared cached list — never mutate it in place.
        var tools = new List<AITool>(baseTools);

        // Route-bound cron tools, only for real user turns. Cron- and heartbeat-triggered turns
        // must NOT be able to schedule more reminders (anti-recursion).
        if (source == TurnSource.User && cron is not null
            && channel is not null && chatId is not null)
        {
            var route = new Route(channel, chatId);
            tools.AddRange(new CronTools(cron, route).AsTools());
        }

        Console.WriteLine(sandboxEnabled
            ? "[DotClaw] tools: MXC sandbox (via MCP)"
            : "[DotClaw] tools: in-process C# (DOTCLAW_SANDBOX=off)");

        Console.WriteLine($"[DotClaw] model: {ModelDeployment} @ {Endpoint}");

        var options = new ChatClientAgentOptions
        {
            Name = "DotClaw",
            ChatOptions = new ChatOptions
            {
                Instructions = baseInstructions,
                Tools = tools,
            },
            // Workspace memory is injected fresh on every invocation (see provider docs),
            // instead of being baked into the immutable instructions at construction time.
            AIContextProviders = [memory],
        };

        var agent = client
            .GetChatClient(ModelDeployment)
            .AsIChatClient()
            .AsAIAgent(options);

        return (agent, memory);
    }

    private static bool SandboxEnabled()
    {
        var v = Environment.GetEnvironmentVariable("DOTCLAW_SANDBOX");
        if (string.IsNullOrWhiteSpace(v))
            return true;
        return v.Trim().ToLowerInvariant() is not ("0" or "false" or "off" or "no");
    }
}
