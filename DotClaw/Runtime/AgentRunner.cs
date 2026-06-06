namespace DotClaw.Runtime;

using DotClaw.Agent;
using DotClaw.Cron;
using DotClaw.Session;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

/// <summary>
/// The single place that turns an intent (user message, heartbeat tick, or due cron job) into an
/// agent run + delivery. Extracted from the old Telegram <c>HandleMessage</c> so both the gateway
/// consumer and the cron service share identical agent behavior.
/// <para>
/// The <see cref="TurnSource"/> drives everything: which session key is used, which tools the agent
/// gets (cron-triggered turns get no cron tools), how the turn is framed, and whether output is
/// suppressed (the heartbeat's <c>HEARTBEAT_OK</c> silence).
/// </para>
/// </summary>
public sealed class AgentRunner
{
    private const string HeartbeatOk = "HEARTBEAT_OK";

    private readonly IMessageSink _sink;
    private readonly CronService _cron;

    public AgentRunner(IMessageSink sink, CronService cron)
    {
        _sink = sink;
        _cron = cron;
    }

    /// <summary>Runs a User or Heartbeat turn (both live in the route's main session).</summary>
    public Task RunInboundAsync(InboundItem item, CancellationToken ct) =>
        item.Source == TurnSource.Heartbeat
            ? RunHeartbeatAsync(item.Route, ct)
            : RunUserAsync(item.Route, item.Text, ct);

    private async Task RunUserAsync(Route route, string userText, CancellationToken ct)
    {
        await _sink.TypingAsync(route, ct);

        var (agent, _) = await DotClawAgentFactory.CreateAsync(
            route.Channel, route.ChatId, _cron, TurnSource.User);
        var session = await agent.CreateSessionAsync();

        var store = new SessionManager(SessionKey(route));
        var history = LoadHistory(store);
        history.Add(new ChatMessage(ChatRole.User, userText));

        var response = await agent.RunAsync(history, session);
        var text = response.Text ?? "(no response)";

        store.Append([
            new { role = "user", content = userText },
            new { role = "assistant", content = text },
        ]);
        await _sink.SendAsync(route, text, ct);
    }

    private async Task RunHeartbeatAsync(Route route, CancellationToken ct)
    {
        var (agent, memory) = await DotClawAgentFactory.CreateAsync(
            route.Channel, route.ChatId, _cron, TurnSource.Heartbeat);
        var session = await agent.CreateSessionAsync();

        var store = new SessionManager(SessionKey(route));
        var history = LoadHistory(store);
        history.Add(new ChatMessage(ChatRole.System, BuildHeartbeatPrompt(memory)));

        var response = await agent.RunAsync(history, session);
        var text = (response.Text ?? "").Trim();

        // Restraint: an empty or HEARTBEAT_OK reply means "nothing to say" — stay silent.
        if (text.Length == 0 || text.StartsWith(HeartbeatOk, StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine($"[heartbeat] {route.ChatId}: {HeartbeatOk} (silent)");
            return;
        }

        Console.WriteLine($"[heartbeat] {route.ChatId}: speaking → {Truncate(text, 60)}");
        store.Append([new { role = "assistant", content = text }]);
        await _sink.SendAsync(route, text, ct);
    }

    /// <summary>
    /// Runs an isolated cron job in its own throwaway session and self-delivers ("announce").
    /// Runs concurrently with live chat — see <see cref="CronService"/>.
    /// </summary>
    public async Task RunCronAsync(CronJob job, CancellationToken ct)
    {
        var (agent, _) = await DotClawAgentFactory.CreateAsync(
            job.Route.Channel, job.Route.ChatId, _cron, TurnSource.Cron);
        var session = await agent.CreateSessionAsync();

        var store = new SessionManager($"cron-{job.Id}");
        var prompt =
            "A scheduled reminder you set earlier is now due.\n" +
            $"Topic: {job.Prompt}\n" +
            "Deliver the reminder to the user now, in your own voice. Keep it to one or two sentences.";
        var history = new List<ChatMessage> { new(ChatRole.System, prompt) };

        var response = await agent.RunAsync(history, session);
        var text = response.Text ?? job.Prompt;

        // Final check: if the job was removed while this run was in flight, don't deliver.
        if (_cron.IsCancelled(job.Id))
        {
            Console.WriteLine($"[cron] job {job.Id} cancelled before delivery — suppressed.");
            return;
        }

        store.Append([new { role = "assistant", content = text }]);
        await _sink.SendAsync(job.Route, text, ct);
        Console.WriteLine($"[cron] delivered job {job.Id} → {job.Route.ChatId}");
    }

    private static string SessionKey(Route route) => $"{route.Channel}-{route.ChatId}";

    private static List<ChatMessage> LoadHistory(SessionManager store)
    {
        var history = new List<ChatMessage>();
        foreach (var entry in store.Load())
        {
            if (!entry.TryGetProperty("role", out var roleProp)) continue;
            var role = roleProp.GetString();
            var content = entry.TryGetProperty("content", out var c) ? c.GetString() ?? "" : "";
            if (role is "user") history.Add(new ChatMessage(ChatRole.User, content));
            else if (role is "assistant") history.Add(new ChatMessage(ChatRole.Assistant, content));
        }
        return history;
    }

    private static string BuildHeartbeatPrompt(WorkspaceMemoryProvider memory)
    {
        var custom = memory.TryReadRaw("HEARTBEAT.md");
        var body = string.IsNullOrWhiteSpace(custom)
            ? "Look at the current time and the user's context. If there is something genuinely worth " +
              "proactively saying right now, say it in one short line. Otherwise stay silent."
            : custom!.Trim();

        return
            "[HEARTBEAT] This is an automatic background check, not a message from the user.\n" +
            body + "\n\n" +
            $"If there is nothing worth sending right now, reply with exactly: {HeartbeatOk}";
    }

    private static string Truncate(string s, int max) => s.Length > max ? s[..max] + "..." : s;
}
