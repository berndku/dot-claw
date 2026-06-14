namespace DotClaw.Tools;

using Microsoft.Extensions.AI;
using ModelContextProtocol.Client;
using Spectre.Console;

/// <summary>
/// Owns the single, long-lived MCP client that talks to Parallel's <b>hosted</b> Search MCP
/// (<c>https://search.parallel.ai/mcp</c>, Streamable HTTP). This mirrors how OpenClaw's free
/// <c>parallel-free</c> web-search provider works: there is no local/prebuilt binary — the agent
/// simply connects to Parallel's remote MCP server, which is free to use anonymously (no API key).
///
/// The server exposes two tools that are surfaced directly to the agent:
/// <list type="bullet">
///   <item><c>web_search</c> — LLM-optimized ranked web excerpts.</item>
///   <item><c>web_fetch</c> — token-efficient markdown from a specific URL.</item>
/// </list>
///
/// Lifetime mirrors <see cref="SandboxTools"/>: the HTTP MCP client is created ONCE and reused for
/// the whole app run (DotClaw.Telegram builds an agent per message and must not leak a client each
/// time), then disposed on process exit.
///
/// Fail-soft: if the remote MCP cannot be reached or enumerated at startup, a warning is printed and
/// an empty tool list is returned so the agent still starts — web search is simply unavailable for
/// that run.
/// </summary>
public static class WebSearchTools
{
    /// <summary>Parallel's free, anonymous hosted Search MCP endpoint.</summary>
    public const string DefaultEndpoint = "https://search.parallel.ai/mcp";

    private static readonly Lazy<Task<IList<AITool>>> Lazy = new(InitAsync);
    private static McpClient? _client;

    private static readonly HashSet<string> LoadedToolNames = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Names of the web-search tools actually loaded from the remote MCP (typically
    /// <c>web_search</c> and <c>web_fetch</c>). Empty until <see cref="GetToolsAsync"/> has run, or
    /// when web search is disabled/unavailable. Frontends use this to recognize web-search tool
    /// calls in an agent response so they can surface them in the transcript.
    /// </summary>
    public static IReadOnlyCollection<string> LoadedTools => LoadedToolNames;

    /// <summary>True when <paramref name="name"/> matches one of the loaded web-search tools.</summary>
    public static bool IsWebSearchTool(string? name) =>
        !string.IsNullOrEmpty(name) && LoadedToolNames.Contains(name);

    /// <summary>
    /// Returns the remote web-search tools, connecting to Parallel's Search MCP on first call.
    /// Never throws: on failure it logs a warning and returns an empty list (fail-soft).
    /// </summary>
    public static Task<IList<AITool>> GetToolsAsync() => Lazy.Value;

    private static async Task<IList<AITool>> InitAsync()
    {
        var endpoint = ResolveEndpoint();

        try
        {
            var transport = new HttpClientTransport(new HttpClientTransportOptions
            {
                Name = "ParallelSearch",
                Endpoint = new Uri(endpoint),
                // Parallel's Search MCP speaks Streamable HTTP; AutoDetect would also work but
                // pinning avoids a redundant probe request on startup.
                TransportMode = HttpTransportMode.StreamableHttp,
            });

            var client = await McpClient.CreateAsync(transport);
            _client = client;

            AppDomain.CurrentDomain.ProcessExit += static (_, _) =>
            {
                try { _client?.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
                catch { /* best effort on shutdown */ }
            };

            var tools = await client.ListToolsAsync();
            var aiTools = tools.Cast<AITool>().ToList();

            // Remember the loaded names so frontends can recognize and display these tool calls.
            foreach (var tool in aiTools)
                LoadedToolNames.Add(tool.Name);

            return aiTools;
        }
        catch (Exception ex)
        {
            // Fail-soft: the remote MCP being unreachable must never stop the agent from starting.
            AnsiConsole.MarkupLine(
                $"  [yellow]⚠ web search disabled[/] [dim]could not reach {Markup.Escape(endpoint)}: {Markup.Escape(ex.Message)}[/]");
            return [];
        }
    }

    /// <summary>
    /// Resolves the Search MCP endpoint: env var <c>DOTCLAW_WEBSEARCH_MCP_URL</c> if set, otherwise
    /// Parallel's free anonymous endpoint.
    /// </summary>
    private static string ResolveEndpoint()
    {
        var fromEnv = Environment.GetEnvironmentVariable("DOTCLAW_WEBSEARCH_MCP_URL");
        return string.IsNullOrWhiteSpace(fromEnv) ? DefaultEndpoint : fromEnv.Trim();
    }
}
