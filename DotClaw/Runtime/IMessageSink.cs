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
}
