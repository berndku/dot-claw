namespace DotClaw.Runtime;

/// <summary>
/// Lightweight runtime configuration read from the shared AppConfiguration.
/// The heartbeat is <b>off by default</b> — it is an opt-in ambient feature for the demo.
/// </summary>
public static class DotClawConfig
{
    /// <summary>Whether the ambient heartbeat runner is enabled. Off by default.</summary>
    public static bool HeartbeatEnabled =>
        ParseBool(AppConfiguration.Instance["DotClaw:Heartbeat"], defaultValue: false);

    /// <summary>How often the heartbeat ticks (seconds), default 45s.</summary>
    public static TimeSpan HeartbeatInterval
    {
        get
        {
            var v = AppConfiguration.Instance["DotClaw:HeartbeatIntervalSeconds"];
            if (int.TryParse(v, out var secs) && secs > 0)
                return TimeSpan.FromSeconds(secs);
            return TimeSpan.FromSeconds(45);
        }
    }

    /// <summary>Locales sent to Azure Speech Fast Transcription. Defaults to German + English.</summary>
    public static string[] SpeechLocales
    {
        get
        {
            var configuredLocales = AppConfiguration.Instance
                .GetSection("AzureSpeech:Locales")
                .GetChildren()
                .Select(section => section.Value)
                .Where(locale => !string.IsNullOrWhiteSpace(locale))
                .Select(locale => locale!.Trim())
                .ToArray();
            if (configuredLocales.Length > 0)
                return configuredLocales;

            var v = ConfigValue("AzureSpeech:Locales", "DOTCLAW_SPEECH_LOCALES");
            if (string.IsNullOrWhiteSpace(v))
                return ["de-DE", "en-US"];

            var locales = v.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            return locales.Length > 0 ? locales : ["de-DE", "en-US"];
        }
    }

    /// <summary>Azure Speech REST API version for Fast Transcription.</summary>
    public static string SpeechApiVersion
    {
        get
        {
            var v = ConfigValue("AzureSpeech:ApiVersion", "DOTCLAW_SPEECH_API_VERSION");
            return string.IsNullOrWhiteSpace(v) ? "2025-10-15" : v.Trim();
        }
    }

    /// <summary>Maximum concurrent Telegram voice downloads/transcriptions. Default 2.</summary>
    public static int VoiceTranscriptionConcurrency
    {
        get
        {
            var v = ConfigValue("Telegram:Voice:TranscriptionConcurrency", "DOTCLAW_VOICE_TRANSCRIPTION_CONCURRENCY");
            if (string.IsNullOrWhiteSpace(v))
                v = AppConfiguration.Instance["DotClaw:VoiceTranscriptionConcurrency"];
            if (int.TryParse(v, out var concurrency) && concurrency > 0)
                return Math.Min(concurrency, 16);
            return 2;
        }
    }

    private static string? ConfigValue(string key, string legacyEnvironmentVariable)
    {
        var value = AppConfiguration.Instance[key];
        return string.IsNullOrWhiteSpace(value) ? Environment.GetEnvironmentVariable(legacyEnvironmentVariable) : value;
    }

    private static bool ParseBool(string? v, bool defaultValue)
    {
        if (string.IsNullOrWhiteSpace(v)) return defaultValue;
        return v.Trim().ToLowerInvariant() is "1" or "true" or "on" or "yes";
    }
}
