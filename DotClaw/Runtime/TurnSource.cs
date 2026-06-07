namespace DotClaw.Runtime;

/// <summary>
/// Where an agent turn originated. Drives session key, tool set, delivery framing and
/// whether user history is read.
/// </summary>
public enum TurnSource
{
    /// <summary>A real message typed by the user.</summary>
    User,

    /// <summary>An ambient background check (the heartbeat), run in the user's main session.</summary>
    Heartbeat,

    /// <summary>A scheduled reminder firing in its own isolated, throwaway session.</summary>
    Cron,
}
