namespace DotClaw.Agent;

using System.Text.RegularExpressions;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

/// <summary>
/// Timer-driven heartbeat that periodically invokes the agent with HEARTBEAT.md.
/// Channels register a delivery callback to receive proactive messages.
/// Mirrors OpenClaw's heartbeat-runner: same agent pipeline, synthetic prompt, skip if empty.
/// </summary>
public class HeartbeatRunner : IDisposable
{
    private readonly TimeSpan _interval;
    private readonly Func<string, Task> _deliver;
    private readonly MemoryManager _memory;
    private Timer? _timer;
    private int _running; // guard against overlapping ticks

    private const string HeartbeatPrompt =
        "Read HEARTBEAT.md in your workspace. If any tasks need attention, handle them and report what you did. " +
        "If nothing needs doing, reply with exactly HEARTBEAT_OK — nothing else.";

    /// <param name="memory">Workspace manager (to locate HEARTBEAT.md).</param>
    /// <param name="interval">How often to fire. Use TimeSpan.Zero to disable.</param>
    /// <param name="deliver">Callback that receives the agent's response text. Won't be called for HEARTBEAT_OK or skipped runs.</param>
    public HeartbeatRunner(MemoryManager memory, TimeSpan interval, Func<string, Task> deliver)
    {
        _memory = memory;
        _interval = interval;
        _deliver = deliver;
    }

    public void Start()
    {
        if (_interval <= TimeSpan.Zero) return;
        // First tick after one interval, then repeating
        _timer = new Timer(_ => _ = RunOnceAsync(), null, _interval, _interval);
        Console.WriteLine($"💓 Heartbeat started — every {_interval.TotalMinutes:0.#} min");
    }

    public async Task RunOnceAsync()
    {
        // Skip if a previous tick is still running
        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0) return;

        try
        {
            // Read HEARTBEAT.md — skip if effectively empty
            var heartbeatPath = Path.Combine(_memory.Workspace, "HEARTBEAT.md");
            if (!File.Exists(heartbeatPath)) return;

            var content = await File.ReadAllTextAsync(heartbeatPath);
            if (IsEffectivelyEmpty(content))
            {
                Console.WriteLine("💓 Heartbeat skipped — HEARTBEAT.md is empty");
                return;
            }

            Console.WriteLine("💓 Heartbeat tick — running agent...");

            // Create a fresh agent + session for the heartbeat (isolated, like OpenClaw's isolatedSession)
            var (agent, _) = await DotClawAgentFactory.CreateAsync("heartbeat");
            var session = await agent.CreateSessionAsync();

            var messages = new List<ChatMessage>
            {
                new(ChatRole.User, HeartbeatPrompt)
            };

            var response = await agent.RunAsync(messages, session);
            var responseText = response.Text?.Trim() ?? "";

            // Filter: if agent says nothing needs doing, don't bother the user
            if (string.IsNullOrEmpty(responseText) ||
                responseText.Equals("HEARTBEAT_OK", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine("💓 Heartbeat OK — nothing to report");
                return;
            }

            Console.WriteLine($"💓 Heartbeat has a message — delivering...");
            await _deliver(responseText);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"⚠️ Heartbeat error: {ex.Message}");
        }
        finally
        {
            Interlocked.Exchange(ref _running, 0);
        }
    }

    /// <summary>
    /// Returns true if the content is only blank lines, markdown headers, and HTML comments.
    /// This matches OpenClaw's "effectively empty" check to save API calls.
    /// </summary>
    private static bool IsEffectivelyEmpty(string content)
    {
        var lines = content.Split('\n');
        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (string.IsNullOrEmpty(trimmed)) continue;
            if (trimmed.StartsWith('#')) continue;
            if (trimmed.StartsWith("<!--") && trimmed.EndsWith("-->")) continue;
            if (trimmed.StartsWith('_') && trimmed.EndsWith('_')) continue; // italic meta-instructions
            return false;
        }
        return true;
    }

    public void Dispose()
    {
        _timer?.Dispose();
        _timer = null;
    }
}
