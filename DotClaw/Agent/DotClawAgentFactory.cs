namespace DotClaw.Agent;

using Azure.AI.OpenAI;
using Azure.Identity;
using DotClaw.Tools;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OpenAI;

/// <summary>
/// Shared factory for creating the DotClaw AIAgent.
/// Used by both the CLI (Program.cs) and Teams gateway (DotClawBot.cs).
/// </summary>
public static class DotClawAgentFactory
{
    /// <summary>
    /// Creates a fully configured AIAgent with tools and system prompt.
    /// Reads configuration from environment variables:
    ///   AZURE_OPENAI_ENDPOINT  – Azure OpenAI resource endpoint (required)
    ///   AZURE_OPENAI_API_KEY   – API key (optional; uses DefaultAzureCredential if not set)
    ///   AZURE_OPENAI_MODEL     – Deployment name (default: gpt-4.1-mini)
    /// </summary>
    public static async Task<(AIAgent Agent, MemoryManager Memory)> CreateAsync(
        string? channel = null, string? chatId = null)
    {
        var endpoint = Environment.GetEnvironmentVariable("AZURE_OPENAI_ENDPOINT")
            ?? throw new InvalidOperationException(
                "AZURE_OPENAI_ENDPOINT is not set.\n" +
                "Set it to your Azure OpenAI resource URL, e.g.:\n" +
                "  https://my-resource.openai.azure.com/");

        var modelDeployment = Environment.GetEnvironmentVariable("AZURE_OPENAI_MODEL")
            ?? "gpt-4.1-mini";

        var apiKey = Environment.GetEnvironmentVariable("AZURE_OPENAI_API_KEY");

        AzureOpenAIClient client = string.IsNullOrEmpty(apiKey)
            ? new AzureOpenAIClient(new Uri(endpoint), new DefaultAzureCredential())
            : new AzureOpenAIClient(new Uri(endpoint), new System.ClientModel.ApiKeyCredential(apiKey));

        var memory = new MemoryManager();
        var systemPrompt = ContextBuilder.BuildSystemPrompt(memory, channel, chatId);

        var sandboxEnabled = SandboxEnabled();
        var tools = sandboxEnabled
            ? await SandboxTools.GetToolsAsync()
            : AgentTools.CreateAll().Cast<AITool>().ToList();

        Console.WriteLine(sandboxEnabled
            ? "[DotClaw] tools: MXC sandbox (via MCP)"
            : "[DotClaw] tools: in-process C# (DOTCLAW_SANDBOX=off)");

        Console.WriteLine($"[DotClaw] model: {modelDeployment} @ {endpoint}");

        var agent = client
            .GetChatClient(modelDeployment)
            .AsIChatClient()
            .AsAIAgent(
                instructions: systemPrompt,
                name: "DotClaw",
                tools: tools);

        return (agent, memory);
    }

    /// <summary>
    /// Whether tools run in the MXC sandbox (via the MCP server) or in-process.
    /// Controlled by the <c>DOTCLAW_SANDBOX</c> env var; defaults to ON.
    /// Set to <c>0</c>/<c>false</c>/<c>off</c>/<c>no</c> to use the C# tools.
    /// </summary>
    private static bool SandboxEnabled()
    {
        var v = Environment.GetEnvironmentVariable("DOTCLAW_SANDBOX");
        if (string.IsNullOrWhiteSpace(v))
            return true;
        return v.Trim().ToLowerInvariant() is not ("0" or "false" or "off" or "no");
    }

}
