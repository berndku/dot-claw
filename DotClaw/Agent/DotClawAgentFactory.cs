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
    public const string Endpoint = "https://openai-bgk.openai.azure.com/";
    public const string ModelDeployment = "gpt-5.4-mini";

    /// <summary>
    /// Creates a fully configured AIAgent with tools and system prompt.
    /// Authenticates with DefaultAzureCredential (az login, managed identity, etc.).
    /// </summary>
    public static async Task<(AIAgent Agent, WorkspaceMemoryProvider Memory)> CreateAsync(
        string? channel = null, string? chatId = null, ApprovalPolicy? approvalPolicy = null)
    {
        var client = new AzureOpenAIClient(new Uri(Endpoint), new DefaultAzureCredential());

        var memory = new WorkspaceMemoryProvider();
        var baseInstructions = ContextBuilder.BuildBaseInstructions(memory, channel, chatId);

        var policy = approvalPolicy ?? ApprovalPolicy.None;

        var sandboxEnabled = SandboxEnabled();
        var baseTools = sandboxEnabled
            ? await SandboxTools.GetToolsAsync()
            : AgentTools.CreateAll().Cast<AITool>().ToList();

        // Copy into a fresh list (the sandbox list is a cached singleton) and always offer
        // the send_message demo tool alongside the sandbox/in-process tools.
        var tools = new List<AITool>(baseTools) { MessagingTools.Create() };

        // Gate the tools named by the policy by wrapping them in ApprovalRequiredAIFunction.
        tools = ApplyApprovalPolicy(tools, policy);

        Console.WriteLine(sandboxEnabled
            ? "[DotClaw] tools: MXC sandbox (via MCP)"
            : "[DotClaw] tools: in-process C# (DOTCLAW_SANDBOX=off)");
        if (!policy.IsEmpty)
            Console.WriteLine("[DotClaw] approval-required tools: " + string.Join(", ", policy.GatedNames));

        Console.WriteLine($"[DotClaw] model: {ModelDeployment} @ {Endpoint}");

        var options = new ChatClientAgentOptions
        {
            Name = "DotClaw",
            ChatOptions = new ChatOptions
            {
                Instructions = baseInstructions,
                Tools = tools,
                // When gating is active, force sequential tool calls so each approval is
                // isolated (otherwise one gated call in a batch gates the whole batch).
                AllowMultipleToolCalls = policy.IsEmpty ? null : false,
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

    private static List<AITool> ApplyApprovalPolicy(IEnumerable<AITool> tools, ApprovalPolicy policy)
    {
        if (policy.IsEmpty)
            return tools.ToList();

        return tools
            .Select(t => t is AIFunction f && policy.RequiresApproval(f.Name)
                ? (AITool)new ApprovalRequiredAIFunction(f)
                : t)
            .ToList();
    }

    private static bool SandboxEnabled()
    {
        var v = Environment.GetEnvironmentVariable("DOTCLAW_SANDBOX");
        if (string.IsNullOrWhiteSpace(v))
            return true;
        return v.Trim().ToLowerInvariant() is not ("0" or "false" or "off" or "no");
    }
}
