namespace DotClaw.Runtime;

/// <summary>
/// A delivery address for an agent turn: which channel (telegram/console/…) and which chat.
/// Baked into cron jobs so a scheduled reminder knows where to deliver itself.
/// </summary>
public sealed record Route(string Channel, string ChatId);
