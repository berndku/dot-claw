namespace DotClaw.Agent;

using System.Diagnostics;
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
    public const string Endpoint = "https://models.inference.ai.azure.com";
    public const string ModelId = "gpt-4o";

    /// <summary>
    /// Creates a fully configured AIAgent with tools and system prompt.
    /// </summary>
    public static async Task<(AIAgent Agent, MemoryManager Memory)> CreateAsync(
        string? channel = null, string? chatId = null)
    {
        var apiKey = await ResolveGitHubToken();

        var client = new OpenAIClient(
            new System.ClientModel.ApiKeyCredential(apiKey),
            new OpenAIClientOptions { Endpoint = new Uri(Endpoint) });

        var memory = new MemoryManager();
        var systemPrompt = ContextBuilder.BuildSystemPrompt(memory, channel, chatId);
        var tools = AgentTools.CreateAll().Cast<AITool>().ToList();

        var agent = client
            .GetChatClient(ModelId)
            .AsIChatClient()
            .AsAIAgent(
                instructions: systemPrompt,
                name: "DotClaw",
                tools: tools);

        return (agent, memory);
    }

    /// <summary>
    /// Resolves a GitHub token: GITHUB_TOKEN env var → gh auth token CLI fallback.
    /// Zero config for anyone already logged into gh CLI.
    /// </summary>
    public static async Task<string> ResolveGitHubToken()
    {
        var envToken = Environment.GetEnvironmentVariable("GITHUB_TOKEN");
        if (!string.IsNullOrEmpty(envToken))
            return envToken;

        try
        {
            var psi = new ProcessStartInfo("gh", "auth token")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var process = Process.Start(psi)!;
            var token = (await process.StandardOutput.ReadToEndAsync()).Trim();
            await process.WaitForExitAsync();

            if (process.ExitCode == 0 && !string.IsNullOrEmpty(token))
                return token;
        }
        catch { }

        throw new InvalidOperationException(
            "No GitHub token found. Either:\n" +
            "  1. Log in with: gh auth login\n" +
            "  2. Or set GITHUB_TOKEN environment variable");
    }
}
