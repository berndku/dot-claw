namespace DotClaw.Cron;

using DotClaw.Runtime;

/// <summary>
/// A persisted scheduled job. Stored as JSON in <c>~/.dotclaw/cron.json</c>.
/// The <see cref="Route"/> is baked in so the job can deliver itself when it fires.
/// </summary>
public sealed class CronJob
{
    /// <summary>Short, human-friendly id (used in cron_list / cron_remove).</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public CronKind Kind { get; set; }

    /// <summary>Interval in seconds for <see cref="CronKind.Every"/>; the one-shot delay for <see cref="CronKind.Once"/>.</summary>
    public long IntervalSeconds { get; set; }

    /// <summary>Next UTC time the job should fire.</summary>
    public DateTime NextRunAt { get; set; }

    /// <summary>What to remind the user about. The agent phrases the actual message when it fires.</summary>
    public string Prompt { get; set; } = "";

    /// <summary>Where to deliver the reminder.</summary>
    public Route Route { get; set; } = new("console", "");

    public bool Enabled { get; set; } = true;

    public DateTime? LastRunAt { get; set; }

    /// <summary>The original schedule expression, kept for display in cron_list.</summary>
    public string ScheduleRaw { get; set; } = "";

    public TimeSpan Interval => TimeSpan.FromSeconds(IntervalSeconds);
}
