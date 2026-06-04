namespace DotClaw.Agent;

using Azure.AI.OpenAI;
using Azure.Identity;
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
    public const string Endpoint = "https://yourresource.openai.azure.com/";
    public const string ModelDeployment = "gpt-4.1-mini";

    /// <summary>
    /// Creates a fully configured AIAgent with tools and system prompt.
    /// Authenticates with DefaultAzureCredential (az login, managed identity, etc.).
    /// </summary>
    public static async Task<(AIAgent Agent, MemoryManager Memory)> CreateAsync(
        string? channel = null, string? chatId = null)
    {
        var client = new AzureOpenAIClient(new Uri(Endpoint), new DefaultAzureCredential());

        var memory = new MemoryManager();
        var systemPrompt = ContextBuilder.BuildSystemPrompt(memory, channel, chatId);

        var sandboxEnabled = SandboxEnabled();
        var tools = sandboxEnabled
            ? await SandboxTools.GetToolsAsync()
            : AgentTools.CreateAll().Cast<AITool>().ToList();

        Console.WriteLine(sandboxEnabled
            ? "[DotClaw] tools: MXC sandbox (via MCP)"
            : "[DotClaw] tools: in-process C# (DOTCLAW_SANDBOX=off)");

        Console.WriteLine($"[DotClaw] model: {ModelDeployment} @ {Endpoint}");

        var agent = client
            .GetChatClient(ModelDeployment)
            .AsIChatClient()
            .AsAIAgent(
                instructions: systemPrompt,
                name: "DotClaw",
                tools: tools);

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
