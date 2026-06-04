namespace DotClaw.Session;

using Microsoft.Extensions.AI;

/// <summary>
/// Trims conversation history to stay within token budget.
/// Uses a simple character-based estimate (1 token ≈ 4 chars)
/// and keeps the most recent messages that fit.
/// </summary>
public static class HistoryTrimmer
{
    private const int CharsPerToken = 4;

    /// <summary>
    /// Returns a trimmed copy of the message list that fits within the given token budget.
    /// Always keeps the most recent messages. If even a single message exceeds the budget,
    /// it is truncated.
    /// </summary>
    /// <param name="messages">Full conversation history.</param>
    /// <param name="maxTokens">Maximum token budget for history (excludes system prompt and tools).</param>
    public static List<ChatMessage> Trim(IReadOnlyList<ChatMessage> messages, int maxTokens = 6000)
    {
        var maxChars = maxTokens * CharsPerToken;
        var result = new List<ChatMessage>();
        var totalChars = 0;

        // Walk backwards from the most recent message
        for (int i = messages.Count - 1; i >= 0; i--)
        {
            var content = messages[i].Text ?? "";
            var charCount = content.Length + 20; // overhead for role/metadata

            if (totalChars + charCount > maxChars)
                break;

            totalChars += charCount;
            result.Insert(0, messages[i]);
        }

        // Always include at least the last message (the user's current input)
        if (result.Count == 0 && messages.Count > 0)
            result.Add(messages[^1]);

        return result;
    }
}
