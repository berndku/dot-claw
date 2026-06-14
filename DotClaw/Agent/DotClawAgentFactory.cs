namespace DotClaw.Agent;

using Azure;
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
/// Uses keyless authentication by default, with optional API key auth for local development.
/// </summary>
public static class DotClawAgentFactory
{
    public static string Endpoint =>
        AppConfiguration.Instance["AzureOpenAI:Endpoint"]
        ?? throw new InvalidOperationException(
            "AzureOpenAI:Endpoint not configured. Copy appsettings.json to appsettings.local.json and fill in your values.");

    public static string ModelDeployment =>
        AppConfiguration.Instance["AzureOpenAI:Model"]
        ?? throw new InvalidOperationException(
            "AzureOpenAI:Model not configured. Copy appsettings.json to appsettings.local.json and fill in your values.");

    /// <summary>
    /// Writes the process-start configuration summary. Runtime agent creation stays quiet so
    /// Telegram message, heartbeat, approval, and cron turns do not repeat these status lines.
    /// </summary>
    public static async Task LogStartupStatusAsync(ApprovalPolicy? approvalPolicy = null)
    {
        var key = AzureOpenAIKey();
        var toolMode = DotClawConfig.ToolMode;
        var webSearchEnabled = DotClawConfig.WebSearchEnabled;
        var webSearchTools = webSearchEnabled
            ? await WebSearchTools.GetToolsAsync()
            : [];

        Console.WriteLine(toolMode switch
        {
            ToolExecutionMode.Cmd => "[DotClaw] tools: cmd (no sandbox)",
            ToolExecutionMode.SandboxMcp => "[DotClaw] tools: MXC sandbox (via MCP)",
            ToolExecutionMode.CSharpSandbox => "[DotClaw] tools: MXC sandbox (in-process C#)",
            _ => $"[DotClaw] tools: {toolMode}",
        });

        var policy = approvalPolicy ?? ApprovalPolicy.None;
        if (!policy.IsEmpty)
            Console.WriteLine("[DotClaw] approval-required tools: " + string.Join(", ", policy.GatedNames));

        Console.WriteLine(webSearchEnabled
            ? (webSearchTools.Count > 0
                ? $"[DotClaw] web search: Parallel Search MCP ({webSearchTools.Count} tool(s))"
                : "[DotClaw] web search: enabled but unavailable (remote MCP unreachable)")
            : "[DotClaw] web search: disabled");

        Console.WriteLine($"[DotClaw] model: {ModelDeployment} @ {Endpoint}");
        Console.WriteLine(string.IsNullOrWhiteSpace(key)
            ? "[DotClaw] Azure OpenAI auth: Microsoft Entra ID"
            : "[DotClaw] Azure OpenAI auth: key");
    }

    /// <summary>
    /// Creates a fully configured AIAgent with tools and system prompt.
    /// Authenticates with DefaultAzureCredential unless AzureOpenAI:Key is configured.
    /// </summary>
    public static async Task<(AIAgent Agent, WorkspaceMemoryProvider Memory)> CreateAsync(
        string? channel = null, string? chatId = null,
        CronService? cron = null, TurnSource source = TurnSource.User,
        ApprovalPolicy? approvalPolicy = null)
    {
        var key = AzureOpenAIKey();
        var client = string.IsNullOrWhiteSpace(key)
            ? new AzureOpenAIClient(new Uri(Endpoint), new DefaultAzureCredential())
            : new AzureOpenAIClient(new Uri(Endpoint), new AzureKeyCredential(key));

        var memory = new WorkspaceMemoryProvider();
        var baseInstructions = ContextBuilder.BuildBaseInstructions(memory, channel, chatId);

        var policy = approvalPolicy ?? ApprovalPolicy.None;

        var toolMode = DotClawConfig.ToolMode;
        var baseTools = toolMode switch
        {
            ToolExecutionMode.Cmd => AgentTools.CreateAll().Cast<AITool>().ToList(),
            ToolExecutionMode.SandboxMcp => await SandboxTools.GetToolsAsync(),
            ToolExecutionMode.CSharpSandbox => CSharpSandboxTools.CreateAll().Cast<AITool>().ToList(),
            _ => throw new InvalidOperationException($"Unsupported tool execution mode: {toolMode}"),
        };

        // Copy: SandboxTools returns a shared cached list — never mutate it in place.
        // Always offer the send_message demo tool alongside the sandbox/in-process tools.
        var tools = new List<AITool>(baseTools) { MessagingTools.Create() };

        // Free Parallel hosted Search MCP (web_search/web_fetch). Sandbox-independent, so it's
        // offered in every tool mode and to both frontends (CLI + Telegram). Fail-soft: an
        // unreachable endpoint yields an empty list rather than blocking agent startup.
        var webSearchEnabled = DotClawConfig.WebSearchEnabled;
        var webSearchTools = webSearchEnabled
            ? await WebSearchTools.GetToolsAsync()
            : [];
        tools.AddRange(webSearchTools);

        // Route-bound cron tools, only for real user turns. Cron- and heartbeat-triggered turns
        // must NOT be able to schedule more reminders (anti-recursion).
        if (source == TurnSource.User && cron is not null
            && channel is not null && chatId is not null)
        {
            var route = new Route(channel, chatId);
            tools.AddRange(new CronTools(cron, route).AsTools());
        }

        // Gate the tools named by the policy by wrapping them in ApprovalRequiredAIFunction.
        tools = ApplyApprovalPolicy(tools, policy);

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

        // Build the chat client, optionally wrapping it with OpenTelemetry so each model round-trip
        // (including tool-calling iterations) is traced. EnableSensitiveData adds the LLM
        // request/response content to the spans — see DotClawConfig.OtelCaptureMessageContent.
        var otelEnabled = DotClawConfig.OtelEnabled;
        var captureContent = DotClawConfig.OtelCaptureMessageContent;

        var chatClient = client
            .GetChatClient(ModelDeployment)
            .AsIChatClient();

        if (otelEnabled)
        {
            chatClient = chatClient
                .AsBuilder()
                .UseOpenTelemetry(
                    sourceName: Telemetry.ActivitySourceName,
                    configure: cfg => cfg.EnableSensitiveData = captureContent)
                .Build();
        }

        AIAgent agent = chatClient.AsAIAgent(options);

        // Wrap the agent so the whole turn surfaces as a single `invoke_agent` span that parents the
        // chat-completion spans above.
        if (otelEnabled)
        {
            agent = agent
                .AsBuilder()
                .UseOpenTelemetry(
                    sourceName: Telemetry.ActivitySourceName,
                    configure: cfg => cfg.EnableSensitiveData = captureContent)
                .Build();
        }

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

    private static string? AzureOpenAIKey()
    {
        var value = AppConfiguration.Instance["AzureOpenAI:Key"];
        if (!string.IsNullOrWhiteSpace(value))
            return value;

        value = Environment.GetEnvironmentVariable("AZURE_OPENAI_API_KEY");
        return string.IsNullOrWhiteSpace(value) ? Environment.GetEnvironmentVariable("AZURE_OPENAI_KEY") : value;
    }
}
