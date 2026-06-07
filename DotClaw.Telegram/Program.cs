using System.Collections.Concurrent;
using DotClaw.Agent;
using DotClaw.Session;
using DotClaw.Tools;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;

// ── Resolve Telegram Bot Token ─────────────────────────────────
var botToken = Environment.GetEnvironmentVariable("TELEGRAM_BOT_TOKEN")
    ?? throw new InvalidOperationException(
        "TELEGRAM_BOT_TOKEN not set.\n" +
        "1. Open Telegram → talk to @BotFather → /newbot\n" +
        "2. Copy the token\n" +
        "3. Set it:  $env:TELEGRAM_BOT_TOKEN = \"your-token\"");

var bot = new TelegramBotClient(botToken);
var me = await bot.GetMe();
Console.WriteLine($"🦞 DotClaw Telegram Gateway — connected as @{me.Username}");
Console.WriteLine("Listening for messages... (Ctrl+C to stop)\n");

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

// ── Long-polling loop ──────────────────────────────────────────
var offset = 0;
while (!cts.Token.IsCancellationRequested)
{
    try
    {
        var updates = await bot.GetUpdates(
            offset, timeout: 30,
            allowedUpdates: [UpdateType.Message, UpdateType.CallbackQuery],
            cancellationToken: cts.Token);

        foreach (var update in updates)
        {
            offset = update.Id + 1;

            // Button tap on an approval prompt → resolve the parked approval.
            if (update.CallbackQuery is { } callback)
            {
                _ = Task.Run(() => HandleCallback(bot, callback, cts.Token));
                continue;
            }

            if (update.Message?.Text is not { } userText) continue;
            if (update.Message.Chat is not { } chat) continue;

            Console.WriteLine($"[{chat.Id}] {chat.FirstName}: {userText}");

            _ = Task.Run(() => HandleMessage(bot, chat, userText, cts.Token));
        }
    }
    catch (OperationCanceledException) { break; }
    catch (Exception ex)
    {
        Console.WriteLine($"⚠️ Polling error: {ex.Message}");
        await Task.Delay(3000, cts.Token);
    }
}

Console.WriteLine("Goodbye.");

// ── Message handler ────────────────────────────────────────────
static async Task HandleMessage(TelegramBotClient bot, Chat chat, string userText, CancellationToken ct)
{
    try
    {
        // Typing indicator
        await bot.SendChatAction(chat.Id, ChatAction.Typing, cancellationToken: ct);

        // Create agent (shared factory from DotClaw core), gating tools per the approval policy.
        var (agent, _) = await DotClawAgentFactory.CreateAsync(
            "telegram", chat.Id.ToString(),
            ApprovalPolicy.FromEnvironment(defaults: [MessagingTools.ToolName]));
        var agentSession = await agent.CreateSessionAsync();

        // Load conversation history
        var sessionKey = $"telegram-{chat.Id}";
        var sessionStore = new SessionManager(sessionKey);
        var history = new List<ChatMessage>();

        foreach (var entry in sessionStore.Load())
        {
            if (!entry.TryGetProperty("role", out var roleProp)) continue;
            var role = roleProp.GetString();
            var content = entry.TryGetProperty("content", out var contentProp)
                ? contentProp.GetString() ?? "" : "";
            if (role is "user") history.Add(new ChatMessage(ChatRole.User, content));
            else if (role is "assistant") history.Add(new ChatMessage(ChatRole.Assistant, content));
        }

        history.Add(new ChatMessage(ChatRole.User, userText));

        // Run agent (MAF handles tool loop). If a tool needs approval, the response carries
        // a ToolApprovalRequestContent instead of final text.
        var response = await agent.RunAsync(history, agentSession, cancellationToken: ct);

        await DeliverOrRequestApprovalAsync(
            bot, chat.Id, "telegram", sessionKey, userText, history, response, ct);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"⚠️ Error handling message from {chat.Id}: {ex.Message}");
        try { await bot.SendMessage(chat.Id, "Sorry, something went wrong. Try again!", cancellationToken: ct); }
        catch { }
    }
}

// ── Approval callback handler ──────────────────────────────────
// A button tap resolves a parked approval. State is in-memory (demo-grade), so a bot
// restart drops outstanding approvals — handled gracefully as "expired".
static async Task HandleCallback(TelegramBotClient bot, CallbackQuery callback, CancellationToken ct)
{
    var data = callback.Data ?? "";
    var parts = data.Split('|', 2);
    var approved = parts[0] == "a";
    var token = parts.Length > 1 ? parts[1] : "";

    // Atomically consume the pending entry so a double-tap can't approve twice.
    if (string.IsNullOrEmpty(token) || !ApprovalStore.Items.TryRemove(token, out var pending))
    {
        await bot.AnswerCallbackQuery(callback.Id, "This request expired — please ask again.",
            cancellationToken: ct);
        if (callback.Message is { } expiredMsg)
        {
            try { await bot.EditMessageReplyMarkup(expiredMsg.Chat.Id, expiredMsg.MessageId, replyMarkup: null, cancellationToken: ct); }
            catch { }
        }
        return;
    }

    // Acknowledge immediately (clears the client's spinner) and freeze the buttons.
    await bot.AnswerCallbackQuery(callback.Id, approved ? "Approved" : "Denied", cancellationToken: ct);
    if (callback.Message is { } promptMsg)
    {
        try
        {
            await bot.EditMessageText(promptMsg.Chat.Id, promptMsg.MessageId,
                (promptMsg.Text ?? "Approval") + (approved ? "\n\n✅ Approved" : "\n\n❌ Denied"),
                replyMarkup: null, cancellationToken: ct);
        }
        catch { }
    }

    try
    {
        // Rebuild the agent + a fresh session. Correlation is by CallId carried inside the
        // stored request + CreateResponse, so the stored message list (incl. the assistant
        // approval-request message) is what makes the resume work.
        var (agent, _) = await DotClawAgentFactory.CreateAsync(
            pending.Channel, pending.ChatId.ToString(),
            ApprovalPolicy.FromEnvironment(defaults: [MessagingTools.ToolName]));
        var session = await agent.CreateSessionAsync();

        var messages = new List<ChatMessage>(pending.Messages)
        {
            new(ChatRole.User, [pending.Request.CreateResponse(approved)]),
        };

        var response = await agent.RunAsync(messages, session, cancellationToken: ct);

        // The continuation may itself request another approval → re-park; otherwise deliver.
        await DeliverOrRequestApprovalAsync(
            bot, pending.ChatId, pending.Channel, pending.SessionKey, pending.UserText, messages, response, ct);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"⚠️ Error resolving approval for {pending.ChatId}: {ex.Message}");
        try { await bot.SendMessage(pending.ChatId, "Sorry, something went wrong resolving that.", cancellationToken: ct); }
        catch { }
    }
}

// Sends the final reply, OR — if the run produced a tool-approval request — sends an inline
// Approve/Deny keyboard and parks the pending state for the callback to resume.
static async Task DeliverOrRequestApprovalAsync(
    TelegramBotClient bot, long chatId, string channel, string sessionKey, string userText,
    List<ChatMessage> sentMessages, AgentResponse response, CancellationToken ct)
{
    var request = response.Messages
        .SelectMany(m => m.Contents)
        .OfType<ToolApprovalRequestContent>()
        .FirstOrDefault();

    if (request is null)
    {
        var responseText = response.Text ?? "(no response)";

        new SessionManager(sessionKey).Append([
            new { role = "user", content = userText },
            new { role = "assistant", content = responseText },
        ]);

        foreach (var chunk in ChunkText(responseText, 4096))
            await bot.SendMessage(chatId, chunk, parseMode: ParseMode.None, cancellationToken: ct);

        Console.WriteLine($"[{chatId}] 🦞: {Truncate(responseText, 80)}");
        return;
    }

    // Carry forward everything we sent plus the assistant message holding the request, so the
    // callback can resume with full context against a fresh session.
    var working = new List<ChatMessage>(sentMessages);
    working.AddRange(response.Messages);

    var token = Guid.NewGuid().ToString("N")[..16];
    ApprovalStore.Items[token] = new Pending(working, request, chatId, channel, sessionKey, userText);

    var call = request.ToolCall as FunctionCallContent;
    var toolName = call?.Name ?? "tool";
    var argsText = call?.Arguments is { } args && args.Count > 0
        ? "\n" + string.Join("\n", args.Select(kv => $"• {kv.Key}: {kv.Value}"))
        : "";

    var keyboard = new InlineKeyboardMarkup(new[]
    {
        InlineKeyboardButton.WithCallbackData("✅ Approve", $"a|{token}"),
        InlineKeyboardButton.WithCallbackData("❌ Deny", $"d|{token}"),
    });

    await bot.SendMessage(chatId,
        $"🔐 Approval required\n\nTool: {toolName}{argsText}",
        replyMarkup: keyboard, cancellationToken: ct);

    Console.WriteLine($"[{chatId}] 🔐 approval requested for {toolName}");
}

static IEnumerable<string> ChunkText(string text, int maxLength)
{
    for (var i = 0; i < text.Length; i += maxLength)
        yield return text.Substring(i, Math.Min(maxLength, text.Length - i));
}

static string Truncate(string s, int max) =>
    s.Length > max ? s[..max] + "..." : s;

// In-memory store of approvals awaiting a button tap (demo-grade; lost on restart).
static class ApprovalStore
{
    public static readonly ConcurrentDictionary<string, Pending> Items = new();
}

// Parked state for a pending approval: the full message list to resume with (including the
// assistant message carrying the request), the request itself, and routing/persistence info.
record Pending(
    List<ChatMessage> Messages,
    ToolApprovalRequestContent Request,
    long ChatId,
    string Channel,
    string SessionKey,
    string UserText);
