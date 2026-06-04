namespace DotClaw.Tools;

using Microsoft.Extensions.AI;
using ModelContextProtocol.Client;

/// <summary>
/// Owns the single, long-lived MCP client that talks to the Node/TypeScript
/// MXC sandbox server (DotClaw.SandboxMcp).
///
/// Lifetime: the Node server process is spawned ONCE and reused for the whole
/// app run — important because DotClaw.Telegram builds an agent per message and
/// must not leak a Node process each time. Each individual tool call still runs
/// in a fresh ephemeral MXC sandbox (that lifetime lives in the Node server).
///
/// The client is created lazily on first use and disposed on process exit.
/// </summary>
public static class SandboxTools
{
    private static readonly Lazy<Task<IList<AITool>>> Lazy = new(InitAsync);
    private static McpClient? _client;

    /// <summary>Returns the sandboxed tools, spawning the MCP server on first call.</summary>
    public static Task<IList<AITool>> GetToolsAsync() => Lazy.Value;

    private static async Task<IList<AITool>> InitAsync()
    {
        var dir = ResolveServerDir();
        var serverScript = Path.Combine(dir, "dist", "server.js");
        if (!File.Exists(serverScript))
        {
            throw new FileNotFoundException(
                $"Sandbox MCP server is not built.\n" +
                $"Expected: {serverScript}\n" +
                $"Build it with:  cd \"{dir}\"  &&  npm install  &&  npm run build\n" +
                $"(or set DOTCLAW_SANDBOX=off to use the in-process C# tools instead).",
                serverScript);
        }

        var client = await McpClient.CreateAsync(new StdioClientTransport(new()
        {
            Name = "DotClaw.SandboxMcp",
            Command = "node",
            Arguments = [serverScript],
        }));
        _client = client;

        AppDomain.CurrentDomain.ProcessExit += static (_, _) =>
        {
            try { _client?.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
            catch { /* best effort on shutdown */ }
        };

        var tools = await client.ListToolsAsync();
        return tools.Cast<AITool>().ToList();
    }

    /// <summary>
    /// Resolves the DotClaw.SandboxMcp project directory (the folder containing
    /// <c>dist/server.js</c>): env var <c>DOTCLAW_SANDBOX_MCP_DIR</c> if set,
    /// otherwise a default relative to the build output.
    /// </summary>
    private static string ResolveServerDir()
    {
        var fromEnv = Environment.GetEnvironmentVariable("DOTCLAW_SANDBOX_MCP_DIR");
        if (!string.IsNullOrWhiteSpace(fromEnv))
            return Path.GetFullPath(fromEnv);

        // bin/<cfg>/<tfm>/ → up 4 → repo root → DotClaw.SandboxMcp
        return Path.GetFullPath(
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "DotClaw.SandboxMcp"));
    }
}
