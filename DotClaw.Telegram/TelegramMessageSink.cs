namespace DotClaw.Telegram;

using DotClaw.Runtime;
using global::Telegram.Bot;
using global::Telegram.Bot.Types.Enums;
using global::Telegram.Bot.Types.ReplyMarkups;

/// <summary>
/// <see cref="IMessageSink"/> over the Telegram Bot API. Lets <see cref="AgentRunner"/> deliver
/// user replies, heartbeat lines, and self-announcing cron reminders without knowing about Telegram.
/// </summary>
public sealed class TelegramMessageSink : IMessageSink
{
    private const int TelegramMaxChars = 4096;

    private readonly TelegramBotClient _bot;

    public TelegramMessageSink(TelegramBotClient bot) => _bot = bot;

    public async Task SendAsync(Route route, string text, CancellationToken ct)
    {
        if (!long.TryParse(route.ChatId, out var chatId)) return;

        foreach (var chunk in Chunk(text, TelegramMaxChars))
            await _bot.SendMessage(chatId, chunk, parseMode: ParseMode.None, cancellationToken: ct);
    }

    public async Task TypingAsync(Route route, CancellationToken ct)
    {
        if (!long.TryParse(route.ChatId, out var chatId)) return;
        try { await _bot.SendChatAction(chatId, ChatAction.Typing, cancellationToken: ct); }
        catch { /* typing is best-effort */ }
    }

    public async Task RequestApprovalAsync(Route route, ApprovalRequest request, CancellationToken ct)
    {
        if (!long.TryParse(route.ChatId, out var chatId)) return;

        var keyboard = new InlineKeyboardMarkup(new[]
        {
            InlineKeyboardButton.WithCallbackData("✅ Approve", $"a|{request.Token}"),
            InlineKeyboardButton.WithCallbackData("❌ Deny", $"d|{request.Token}"),
        });

        var argsBlock = string.IsNullOrEmpty(request.ArgumentsText) ? "" : "\n" + request.ArgumentsText;
        await _bot.SendMessage(chatId,
            $"🔐 Approval required\n\nTool: {request.ToolName}{argsBlock}",
            replyMarkup: keyboard, cancellationToken: ct);

        Console.WriteLine($"[{chatId}] 🔐 approval requested for {request.ToolName}");
    }

    private static IEnumerable<string> Chunk(string text, int max)
    {
        if (string.IsNullOrEmpty(text)) yield break;
        for (var i = 0; i < text.Length; i += max)
            yield return text.Substring(i, Math.Min(max, text.Length - i));
    }
}
