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

    /// <summary>
    /// A human's Approve/Deny decision resuming a parked tool-approval. Runs in the user's main
    /// session, so it flows through the same single consumer as User/Heartbeat turns to avoid
    /// concurrent writes to that session's history.
    /// </summary>
    Approval,
}
