namespace DotClaw.Agent;

using Azure.AI.OpenAI;
using Azure.Identity;
using DotClaw.Tools;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

/// <summary>
/// Shared factory for creating the DotClaw AIAgent.
/// Used by both the CLI (Program.cs) and Teams gateway (DotClawBot.cs).
/// </summary>
public static class DotClawAgentFactory
{
    /// <summary>
    /// Creates a fully configured AIAgent with tools and system prompt.
    /// Configuration is loaded from ~/.dotclaw/config.json (env vars override).
    /// </summary>
    public static async Task<(AIAgent Agent, MemoryManager Memory)> CreateAsync(
        string? channel = null, string? chatId = null)
    {
        var config = DotClawConfig.Load();

        if (string.IsNullOrWhiteSpace(config.AzureOpenAiEndpoint)
            || config.AzureOpenAiEndpoint.Contains("YOUR-RESOURCE"))
        {
            throw new InvalidOperationException(
                "Azure OpenAI endpoint is not configured.\n" +
                "Edit ~/.dotclaw/config.json and set \"azure_openai_endpoint\" to your resource URL.\n" +
                "Or set the AZURE_OPENAI_ENDPOINT environment variable.");
        }

        AzureOpenAIClient client = string.IsNullOrEmpty(config.AzureOpenAiApiKey)
            ? new AzureOpenAIClient(new Uri(config.AzureOpenAiEndpoint), new DefaultAzureCredential())
            : new AzureOpenAIClient(new Uri(config.AzureOpenAiEndpoint),
                new System.ClientModel.ApiKeyCredential(config.AzureOpenAiApiKey));

        var memory = new MemoryManager();
        var systemPrompt = ContextBuilder.BuildSystemPrompt(memory, channel, chatId);

        var sandboxEnabled = config.Sandbox ?? true;
        var tools = sandboxEnabled
            ? await SandboxTools.GetToolsAsync()
            : AgentTools.CreateAll().Cast<AITool>().ToList();

        Console.WriteLine(sandboxEnabled
            ? "[DotClaw] tools: MXC sandbox (via MCP)"
            : "[DotClaw] tools: in-process C# (DOTCLAW_SANDBOX=off)");

        Console.WriteLine($"[DotClaw] model: {config.AzureOpenAiModel} @ {config.AzureOpenAiEndpoint}");

        var agent = client
            .GetChatClient(config.AzureOpenAiModel!)
            .AsIChatClient()
            .AsAIAgent(
                instructions: systemPrompt,
                name: "DotClaw",
                tools: tools);

        return (agent, memory);
    }
}
