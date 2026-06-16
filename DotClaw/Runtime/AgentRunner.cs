namespace DotClaw.Runtime;

using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using DotClaw.Agent;
using DotClaw.Cron;
using DotClaw.Session;
using DotClaw.Tools;
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
    private const int MaxHistoryTokens = 100_000;
    private static readonly Regex HtmlCommentRegex = new(
        "<!--.*?-->",
        RegexOptions.Singleline | RegexOptions.CultureInvariant);

    private readonly IMessageSink _sink;
    private readonly CronService _cron;

    public AgentRunner(IMessageSink sink, CronService cron)
    {
        _sink = sink;
        _cron = cron;
    }

    /// <summary>Runs a User, Heartbeat, or Approval-resume turn (all live in the route's main session).</summary>
    public Task RunInboundAsync(InboundItem item, CancellationToken ct) => item.Source switch
    {
        TurnSource.Heartbeat => RunHeartbeatAsync(item.Route, ct),
        TurnSource.Approval => ResolveApprovalAsync(item.Approval!, item.Approved, ct),
        _ => RunUserAsync(item.Route, item.Text, ct),
    };

    private async Task RunUserAsync(Route route, string userText, CancellationToken ct)
    {
        await _sink.TypingAsync(route, ct);

        var (agent, _) = await DotClawAgentFactory.CreateAsync(
            route.Channel, route.ChatId, _cron, TurnSource.User, BuildApprovalPolicy());
        var session = await agent.CreateSessionAsync();

        var store = new SessionManager(SessionKey(route));
        var history = LoadHistory(store.Load());
        history.Add(new ChatMessage(ChatRole.User, userText));

        var response = await agent.RunAsync(history, session);
        await DeliverOrRequestApprovalAsync(route, SessionKey(route), userText, history, response, ct);
    }

    /// <summary>
    /// Resumes a parked approval after the human tapped Approve/Deny. The pending entry is removed
    /// by the caller (so the button can't be double-handled); we rebuild the agent on a fresh
    /// session and replay the stored messages plus the approval response — correlation is by CallId,
    /// so a fresh session is fine.
    /// </summary>
    public async Task ResolveApprovalAsync(PendingApproval pending, bool approved, CancellationToken ct)
    {
        var (agent, _) = await DotClawAgentFactory.CreateAsync(
            pending.Route.Channel, pending.Route.ChatId, _cron, TurnSource.User, BuildApprovalPolicy());
        var session = await agent.CreateSessionAsync();

        var messages = new List<ChatMessage>(pending.Messages)
        {
            new(ChatRole.User, [pending.Request.CreateResponse(approved)]),
        };

        var response = await agent.RunAsync(messages, session);
        await DeliverOrRequestApprovalAsync(
            pending.Route, pending.SessionKey, pending.UserText, messages, response, ct);
    }

    /// <summary>
    /// Shared tail for user turns and approval resumes: if the run produced a tool-approval request,
    /// park it and ask the human (no persistence yet); otherwise persist the exchange and deliver.
    /// </summary>
    private async Task DeliverOrRequestApprovalAsync(
        Route route, string sessionKey, string userText,
        List<ChatMessage> sentMessages, AgentResponse response, CancellationToken ct)
    {
        var request = response.Messages
            .SelectMany(m => m.Contents)
            .OfType<ToolApprovalRequestContent>()
            .FirstOrDefault();

        if (request is null)
        {
            var text = FinalAssistantText(response) ?? "(no response)";
            new SessionManager(sessionKey).Append([
                new { role = "user", content = userText },
                new { role = "assistant", content = text },
            ]);
            await _sink.SendAsync(route, text, ct);
            return;
        }

        // Park the live message list (including the assistant request) so the button tap can resume.
        var working = new List<ChatMessage>(sentMessages);
        working.AddRange(response.Messages);

        var token = Guid.NewGuid().ToString("N")[..16];
        ApprovalStore.Items[token] =
            new PendingApproval(token, working, request, route, sessionKey, userText);

        var call = request.ToolCall as FunctionCallContent;
        var toolName = call?.Name ?? "tool";
        var argsText = call?.Arguments is { Count: > 0 } args
            ? string.Join("\n", args.Select(kv => $"• {kv.Key}: {kv.Value}"))
            : "";

        await _sink.RequestApprovalAsync(route, new ApprovalRequest(token, toolName, argsText), ct);
    }

    /// <summary>
    /// Approval policy for interactive user turns, sourced from the <c>DotClaw:ApprovalTools</c>
    /// appsettings array (defaulting to <c>send_message</c>). Heartbeat and cron turns deliberately
    /// use no policy: there is no human present to tap a button, so their tool calls run ungated.
    /// </summary>
    private static ApprovalPolicy BuildApprovalPolicy() =>
        ApprovalPolicy.FromConfiguration(defaults: [MessagingTools.ToolName]);

    private async Task RunHeartbeatAsync(Route route, CancellationToken ct)
    {
        var (agent, memory) = await DotClawAgentFactory.CreateAsync(
            route.Channel, route.ChatId, _cron, TurnSource.Heartbeat);
        var session = await agent.CreateSessionAsync();

        var store = new SessionManager(SessionKey(route));
        var entries = store.Load();
        var history = LoadHistory(entries);
        var prompt = BuildHeartbeatPrompt(memory, TimeSinceLastUser(entries));
        if (prompt is null)
        {
            Console.WriteLine($"[heartbeat] {route.ChatId}: no heartbeat tasks configured (silent)");
            return;
        }

        // Inject as a User turn, not System: chat models are trained to *respond* to the user turn,
        // so a wake-up in the user slot reliably prompts a decision to speak or stay silent. A trailing
        // system message reads as configuration and biases the model toward the compliant minimal
        // answer (HEARTBEAT_OK) — the reason the heartbeat never actually checked in.
        history.Add(new ChatMessage(ChatRole.User, prompt));

        // [HEARTBEAT-DIAG] Temporary troubleshooting — remove once confirmed working.
        Console.WriteLine($"[heartbeat-diag] history={history.Count} msgs (excluding the tick prompt):");
        for (var i = 0; i < history.Count - 1; i++)
            Console.WriteLine($"[heartbeat-diag]   {history[i].Role}: {Truncate((history[i].Text ?? "").Replace('\n', ' '), 80)}");
        Console.WriteLine($"[heartbeat-diag] FULL PROMPT >>>\n{prompt}\n<<< END PROMPT");

        var response = await agent.RunAsync(history, session);
        var text = (FinalAssistantText(response) ?? "").Trim();

        // [HEARTBEAT-DIAG] Temporary — show the raw model reply so we can see WHY it's silent.
        Console.WriteLine($"[heartbeat-diag] rawReply=\"{Truncate(text.Replace('\n', ' '), 120)}\"");

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
        var text = FinalAssistantText(response) ?? job.Prompt;

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

    // Returns only the final assistant message's text, or null if none carries text. AgentResponse.Text
    // concatenates the text of every message in a turn — including intermediate narration emitted
    // alongside tool calls — so tool-using turns would otherwise deliver the reply twice.
    private static string? FinalAssistantText(AgentResponse response) =>
        response.Messages
            .Where(m => m.Role == ChatRole.Assistant)
            .Select(m => m.Text)
            .LastOrDefault(t => !string.IsNullOrWhiteSpace(t));

    private static List<ChatMessage> LoadHistory(IReadOnlyList<JsonElement> entries)
    {
        var history = new List<ChatMessage>();
        foreach (var entry in entries)
        {
            if (!entry.TryGetProperty("role", out var roleProp)) continue;
            var role = roleProp.GetString();
            var content = entry.TryGetProperty("content", out var c) ? c.GetString() ?? "" : "";
            var timestamp = entry.TryGetProperty("timestamp", out var t) ? t.GetString() : null;
            var stamped = StampContent(timestamp, content);
            if (role is "user") history.Add(new ChatMessage(ChatRole.User, stamped));
            else if (role is "assistant") history.Add(new ChatMessage(ChatRole.Assistant, stamped));
        }
        return HistoryTrimmer.Trim(history, MaxHistoryTokens);
    }

    // Prefixes a persisted message's wall-clock time to its content so the model can see *when* each
    // turn happened. The chat-completion wire format carries no per-message timestamp field, so the
    // only way the model can reason about staleness — e.g. the heartbeat deciding whether to check in
    // on a quiet user — is to read the time from inside the content. Paired with the "Current
    // date/time" line ContextBuilder injects every turn (same UTC-minute format), this lets the model
    // compute how long ago the last user message was, with no special-casing in the prompt.
    private static string StampContent(string? isoTimestamp, string content)
    {
        if (string.IsNullOrWhiteSpace(isoTimestamp))
            return content;

        var formatted = DateTimeOffset.TryParse(
            isoTimestamp, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var dto)
            ? dto.UtcDateTime.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture)
            : isoTimestamp;

        return $"[{formatted}] {content}";
    }

    /// <summary>
    /// Builds the per-tick heartbeat system prompt from the workspace's <c>HEARTBEAT.md</c>, or
    /// returns <c>null</c> when that file has no actionable (non-comment) content. Public so the CLI
    /// frontend — which runs its own lightweight heartbeat loop rather than the channel/consumer the
    /// Telegram gateway uses — can share the exact same gating and framing.
    /// </summary>
    public static string? BuildHeartbeatPrompt(WorkspaceMemoryProvider memory, TimeSpan? sinceLastUser = null)
    {
        var custom = memory.TryReadRaw("HEARTBEAT.md");
        if (!HasHeartbeatInstructions(custom))
            return null;

        var body = custom!.Trim();
        var now = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture);

        // Do the arithmetic the model is unreliable at: state the exact elapsed time since the user's
        // last message so HEARTBEAT.md's rules can act on a fact, instead of re-deriving it from the
        // minute-precision stamps in the replayed transcript.
        var gapLine = sinceLastUser is { } gap
            ? $"Elapsed time since the user's last message: {DescribeGap(gap)}.\n\n"
            : "";

        return
            $"[HEARTBEAT @ {now}] Automatic background tick — the user has not sent a new message. Follow the rules below and decide what to do.\n\n" +
            gapLine +
            body + "\n\n" +
            $"Output only your decision: either the exact message to send the user now, or the literal token {HeartbeatOk} to stay silent — exactly as the rules above direct.";
    }

    private static bool HasHeartbeatInstructions(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return false;

        var withoutHtmlComments = HtmlCommentRegex.Replace(content, "");
        foreach (var line in withoutHtmlComments.Replace("\r\n", "\n").Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length > 0 && !trimmed.StartsWith("#", StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Wall-clock time since the most recent user message in <paramref name="entries"/>, or
    /// <c>null</c> when no user message is on record. Uses the persisted ISO timestamps (second
    /// precision) so the heartbeat can state the gap exactly rather than make the model derive it
    /// from the minute-truncated stamps in the replayed transcript.
    /// </summary>
    private static TimeSpan? TimeSinceLastUser(IEnumerable<JsonElement> entries)
    {
        DateTimeOffset? lastUser = null;
        foreach (var entry in entries)
        {
            if (!entry.TryGetProperty("role", out var role) || role.GetString() != "user")
                continue;
            if (entry.TryGetProperty("timestamp", out var t) &&
                DateTimeOffset.TryParse(
                    t.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var dto))
                lastUser = dto;
        }

        return lastUser is { } u ? DateTime.UtcNow - u.UtcDateTime : null;
    }

    /// <summary>Renders an elapsed <see cref="TimeSpan"/> as a short human phrase (e.g. "3 minutes").</summary>
    private static string DescribeGap(TimeSpan gap)
    {
        if (gap < TimeSpan.FromMinutes(1)) return "less than a minute";
        var minutes = (int)gap.TotalMinutes;
        if (minutes < 60) return minutes == 1 ? "1 minute" : $"{minutes} minutes";
        var hours = (int)gap.TotalHours;
        if (hours < 24) return hours == 1 ? "1 hour" : $"{hours} hours";
        var days = (int)gap.TotalDays;
        return days == 1 ? "1 day" : $"{days} days";
    }

    private static string Truncate(string s, int max) => s.Length > max ? s[..max] + "..." : s;
}
