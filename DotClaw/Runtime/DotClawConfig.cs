namespace DotClaw.Runtime;

using OpenTelemetry.Exporter;

public enum ToolExecutionMode
{
    Cmd,
    SandboxMcp,
    CSharpSandbox,
}

/// <summary>
/// Lightweight runtime configuration read from the shared AppConfiguration.
/// The heartbeat is <b>off by default</b> — it is an opt-in ambient feature for the demo.
/// </summary>
public static class DotClawConfig
{
    /// <summary>Whether the ambient heartbeat runner is enabled. Off by default.</summary>
    public static bool HeartbeatEnabled =>
        ParseBool(AppConfiguration.Instance["DotClaw:Heartbeat"], defaultValue: false);

    /// <summary>
    /// Whether the free Parallel hosted Search MCP (<c>web_search</c>/<c>web_fetch</c>) is offered
    /// to the agent. On by default; reachability failures are handled fail-soft at startup.
    /// </summary>
    public static bool WebSearchEnabled =>
        ParseBool(ConfigValue("DotClaw:WebSearch", "DOTCLAW_WEB_SEARCH"), defaultValue: true);

    /// <summary>
    /// Whether OpenTelemetry tracing is exported over OTLP (e.g. to the Aspire dashboard).
    /// Off by default — opt-in, like the heartbeat. Enable with <c>DotClaw:Otel:Enabled</c> or the
    /// <c>DOTCLAW_OTEL</c> env var.
    /// </summary>
    public static bool OtelEnabled =>
        ParseBool(ConfigValue("DotClaw:Otel:Enabled", "DOTCLAW_OTEL"), defaultValue: false);

    /// <summary>
    /// OTLP collector endpoint. Defaults to the Aspire dashboard's gRPC ingest
    /// (<c>http://localhost:4317</c>). Honors the standard <c>OTEL_EXPORTER_OTLP_ENDPOINT</c> env var.
    /// </summary>
    public static string OtelEndpoint
    {
        get
        {
            var v = ConfigValue("DotClaw:Otel:Endpoint", "OTEL_EXPORTER_OTLP_ENDPOINT");
            return string.IsNullOrWhiteSpace(v) ? "http://localhost:4317" : v.Trim();
        }
    }

    /// <summary>
    /// OTLP wire protocol. <c>grpc</c> (default, port 4317) or <c>http/protobuf</c> (port 4318).
    /// Honors the standard <c>OTEL_EXPORTER_OTLP_PROTOCOL</c> env var.
    /// </summary>
    public static OtlpExportProtocol OtelProtocol
    {
        get
        {
            var v = ConfigValue("DotClaw:Otel:Protocol", "OTEL_EXPORTER_OTLP_PROTOCOL");
            return (v?.Trim().ToLowerInvariant().Replace("_", "/")) switch
            {
                "http/protobuf" or "httpprotobuf" or "http" => OtlpExportProtocol.HttpProtobuf,
                _ => OtlpExportProtocol.Grpc,
            };
        }
    }

    /// <summary>OpenTelemetry <c>service.name</c> resource attribute. Defaults to <c>DotClaw</c>.</summary>
    public static string OtelServiceName
    {
        get
        {
            var v = ConfigValue("DotClaw:Otel:ServiceName", "DOTCLAW_OTEL_SERVICE_NAME");
            return string.IsNullOrWhiteSpace(v) ? "DotClaw" : v.Trim();
        }
    }

    /// <summary>
    /// Whether to attach LLM request/response content (prompts, completions, tool arguments) to the
    /// emitted GenAI spans. This is the Agent Framework's <c>EnableSensitiveData</c> flag. On by
    /// default so the Aspire trace view shows full message content for local debugging; turn it off
    /// (<c>DotClaw:Otel:CaptureMessageContent: false</c>) to keep potentially sensitive data out of
    /// traces.
    /// </summary>
    public static bool OtelCaptureMessageContent =>
        ParseBool(ConfigValue("DotClaw:Otel:CaptureMessageContent", "DOTCLAW_OTEL_CAPTURE_CONTENT"),
            defaultValue: true);

    /// <summary>
    /// Selects how built-in file/command tools run.
    /// Prefer <c>DotClaw:ToolMode</c>; legacy <c>DotClaw:Sandbox</c> is retained as a fallback.
    /// </summary>
    public static ToolExecutionMode ToolMode
    {
        get
        {
            var configured = ConfigValue("DotClaw:ToolMode", "DOTCLAW_TOOL_MODE");
            if (!string.IsNullOrWhiteSpace(configured))
                return ParseToolMode(configured);

            var legacySandbox = ConfigValue("DotClaw:Sandbox", "DOTCLAW_SANDBOX");
            if (string.IsNullOrWhiteSpace(legacySandbox))
                return ToolExecutionMode.SandboxMcp;

            return ParseLegacySandbox(legacySandbox)
                ? ToolExecutionMode.SandboxMcp
                : ToolExecutionMode.Cmd;
        }
    }

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

    /// <summary>
    /// Tool names that require human approval before they run. Read from the
    /// <c>DotClaw:ApprovalTools</c> appsettings array. Returns <c>null</c> when nothing is
    /// configured, so callers can apply their own defaults.
    /// </summary>
    public static IReadOnlyList<string>? ApprovalTools
    {
        get
        {
            var configuredTools = AppConfiguration.Instance
                .GetSection("DotClaw:ApprovalTools")
                .GetChildren()
                .Select(section => section.Value)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Select(name => name!.Trim())
                .ToArray();
            return configuredTools.Length > 0 ? configuredTools : null;
        }
    }

    private static string? ConfigValue(string key, string legacyEnvironmentVariable)
    {
        var legacyValue = Environment.GetEnvironmentVariable(legacyEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(legacyValue))
            return legacyValue;

        var value = AppConfiguration.Instance[key];
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static ToolExecutionMode ParseToolMode(string value)
    {
        var normalized = value.Trim().ToLowerInvariant()
            .Replace('_', '-')
            .Replace(' ', '-');

        return normalized switch
        {
            "cmd" or "host" or "direct" or "no-sandbox" or "cmd-no-sandbox" or "cmd-w/o-sandbox"
                => ToolExecutionMode.Cmd,
            "sandboxmcp" or "sandbox-mcp" or "mcp"
                => ToolExecutionMode.SandboxMcp,
            "csharp-sandbox" or "c#-sandbox" or "cs-sandbox" or "c-sharp-sandbox"
                => ToolExecutionMode.CSharpSandbox,
            _ => throw new InvalidOperationException(
                $"Unsupported DotClaw:ToolMode '{value}'. Use one of: cmd, sandboxmcp, csharp-sandbox."),
        };
    }

    private static bool ParseBool(string? v, bool defaultValue)
    {
        if (string.IsNullOrWhiteSpace(v)) return defaultValue;
        return v.Trim().ToLowerInvariant() is "1" or "true" or "on" or "yes";
    }

    private static bool ParseLegacySandbox(string value) =>
        value.Trim().ToLowerInvariant() is not ("0" or "false" or "off" or "no");
}
