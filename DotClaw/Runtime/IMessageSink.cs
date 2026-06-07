namespace DotClaw.Runtime;

/// <summary>
/// Channel-agnostic outbound delivery. The core <see cref="AgentRunner"/> produces text and
/// delivers it through a sink; the concrete sink (Telegram, console, …) knows how to reach a route.
/// </summary>
public interface IMessageSink
{
    /// <summary>Deliver a message to the given route.</summary>
    Task SendAsync(Route route, string text, CancellationToken ct);

    /// <summary>Optional "typing…" indicator before a user turn. No-op for channels without one.</summary>
    Task TypingAsync(Route route, CancellationToken ct);

    /// <summary>
    /// Ask the human to approve or deny a pending tool call. Interactive channels render this with
    /// affordances (e.g. Telegram inline Approve/Deny buttons) whose response later resolves the
    /// parked approval; non-interactive channels may fall back to plain text.
    /// </summary>
    Task RequestApprovalAsync(Route route, ApprovalRequest request, CancellationToken ct);
}
