using DotClaw.Agent;
using DotClaw.Session;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

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
        var updates = await bot.GetUpdates(offset, timeout: 30, cancellationToken: cts.Token);

        foreach (var update in updates)
        {
            offset = update.Id + 1;

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

        // Create agent (shared factory from DotClaw core)
        var (agent, _) = await DotClawAgentFactory.CreateAsync("telegram", chat.Id.ToString());
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

        // Run agent (MAF handles tool loop)
        var response = await agent.RunAsync(history, agentSession);
        var responseText = response.Text ?? "(no response)";

        // Persist
        sessionStore.Append([
            new { role = "user", content = userText },
            new { role = "assistant", content = responseText }
        ]);

        // Send response (split if > 4096 chars — Telegram limit)
        foreach (var chunk in ChunkText(responseText, 4096))
        {
            await bot.SendMessage(chat.Id, chunk, parseMode: ParseMode.None, cancellationToken: ct);
        }

        Console.WriteLine($"[{chat.Id}] 🦞: {Truncate(responseText, 80)}");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"⚠️ Error handling message from {chat.Id}: {ex.Message}");
        try { await bot.SendMessage(chat.Id, "Sorry, something went wrong. Try again!", cancellationToken: ct); }
        catch { }
    }
}

static IEnumerable<string> ChunkText(string text, int maxLength)
{
    for (var i = 0; i < text.Length; i += maxLength)
        yield return text.Substring(i, Math.Min(maxLength, text.Length - i));
}

static string Truncate(string s, int max) =>
    s.Length > max ? s[..max] + "..." : s;
