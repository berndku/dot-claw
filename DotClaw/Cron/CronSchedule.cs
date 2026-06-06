namespace DotClaw.Cron;

using System.Globalization;

/// <summary>Whether a cron job fires once or repeats.</summary>
public enum CronKind
{
    /// <summary>Fires a single time, then is removed.</summary>
    Once,

    /// <summary>Fires repeatedly on a fixed interval.</summary>
    Every,
}

/// <summary>
/// A parsed schedule expression. Tier-1 grammar (no cron expressions):
/// <list type="bullet">
///   <item><c>in:&lt;dur&gt;</c> — one-shot after a delay (e.g. <c>in:1m</c>, <c>in:30s</c>, <c>in:2h</c>).</item>
///   <item><c>every:&lt;dur&gt;</c> — recurring on an interval (e.g. <c>every:30m</c>).</item>
/// </list>
/// Duration units: <c>s</c>, <c>m</c>, <c>h</c>, <c>d</c>.
/// </summary>
public readonly record struct CronSchedule(CronKind Kind, TimeSpan Interval, string Raw)
{
    public static bool TryParse(string? expr, out CronSchedule schedule, out string? error)
    {
        schedule = default;
        error = null;

        if (string.IsNullOrWhiteSpace(expr))
        {
            error = "schedule is empty";
            return false;
        }

        var s = expr.Trim();
        var idx = s.IndexOf(':');
        if (idx <= 0)
        {
            error = $"expected 'in:<duration>' or 'every:<duration>', got '{expr}'";
            return false;
        }

        var kindStr = s[..idx].Trim().ToLowerInvariant();
        var durStr = s[(idx + 1)..].Trim();

        if (!TryParseDuration(durStr, out var dur, out error))
            return false;

        if (dur <= TimeSpan.Zero)
        {
            error = "duration must be positive";
            return false;
        }

        switch (kindStr)
        {
            case "in":
            case "at":
                schedule = new CronSchedule(CronKind.Once, dur, s);
                return true;
            case "every":
                schedule = new CronSchedule(CronKind.Every, dur, s);
                return true;
            default:
                error = $"unknown schedule kind '{kindStr}' (use 'in' or 'every')";
                return false;
        }
    }

    private static bool TryParseDuration(string d, out TimeSpan span, out string? error)
    {
        span = default;
        error = null;

        if (string.IsNullOrWhiteSpace(d))
        {
            error = "duration is empty";
            return false;
        }

        d = d.Trim().ToLowerInvariant();
        var unit = d[^1];
        var numPart = d[..^1];

        double secondsPerUnit;
        switch (unit)
        {
            case 's': secondsPerUnit = 1; break;
            case 'm': secondsPerUnit = 60; break;
            case 'h': secondsPerUnit = 3600; break;
            case 'd': secondsPerUnit = 86400; break;
            default:
                error = $"unknown duration unit in '{d}' (use s/m/h/d)";
                return false;
        }

        if (!double.TryParse(numPart, NumberStyles.Float, CultureInfo.InvariantCulture, out var n))
        {
            error = $"invalid duration number in '{d}'";
            return false;
        }

        span = TimeSpan.FromSeconds(n * secondsPerUnit);
        return true;
    }
}
