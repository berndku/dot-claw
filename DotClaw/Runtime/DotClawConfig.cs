namespace DotClaw.Runtime;

/// <summary>
/// Lightweight runtime configuration read from environment variables.
/// The heartbeat is <b>off by default</b> — it is an opt-in ambient feature for the demo.
/// </summary>
public static class DotClawConfig
{
    /// <summary>Whether the ambient heartbeat runner is enabled. Off unless DOTCLAW_HEARTBEAT is truthy.</summary>
    public static bool HeartbeatEnabled =>
        ParseBool(Environment.GetEnvironmentVariable("DOTCLAW_HEARTBEAT"), defaultValue: false);

    /// <summary>How often the heartbeat ticks. DOTCLAW_HEARTBEAT_INTERVAL (seconds), default 45s.</summary>
    public static TimeSpan HeartbeatInterval
    {
        get
        {
            var v = Environment.GetEnvironmentVariable("DOTCLAW_HEARTBEAT_INTERVAL");
            if (int.TryParse(v, out var secs) && secs > 0)
                return TimeSpan.FromSeconds(secs);
            return TimeSpan.FromSeconds(45);
        }
    }

    private static bool ParseBool(string? v, bool defaultValue)
    {
        if (string.IsNullOrWhiteSpace(v)) return defaultValue;
        return v.Trim().ToLowerInvariant() is "1" or "true" or "on" or "yes";
    }
}
