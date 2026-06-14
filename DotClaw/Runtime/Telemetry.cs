namespace DotClaw.Runtime;

using OpenTelemetry;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

/// <summary>
/// OpenTelemetry bootstrap for DotClaw.
/// <para>
/// Exports the Microsoft Agent Framework / Microsoft.Extensions.AI <b>GenAI traces</b> — the
/// <c>invoke_agent</c> spans plus the nested chat-completion spans (one per model round-trip,
/// including tool calls) — over OTLP so they can be visualized in the
/// <see href="https://aspiredashboard.com/">Aspire dashboard</see>.
/// </para>
/// <para>
/// Tracing is opt-in (off by default, see <see cref="DotClawConfig.OtelEnabled"/>). The spans are
/// emitted by the chat-client and agent OpenTelemetry wrappers in <c>DotClawAgentFactory</c>, which
/// use <see cref="ActivitySourceName"/> as their source — the same name registered here via
/// <c>AddSource</c> so the exporter picks them up. Whether each span also carries the LLM
/// request/response content is controlled by <see cref="DotClawConfig.OtelCaptureMessageContent"/>
/// (the Agent Framework's <c>EnableSensitiveData</c> flag).
/// </para>
/// </summary>
public static class Telemetry
{
    /// <summary>
    /// ActivitySource name shared by the chat-client and agent OpenTelemetry wrappers. It must match
    /// the <c>sourceName</c> passed to <c>UseOpenTelemetry</c> and be registered with the
    /// TracerProvider (<c>AddSource</c>) for spans to be exported.
    /// </summary>
    public const string ActivitySourceName = "DotClaw";

    /// <summary>
    /// Builds and starts the TracerProvider when OTEL is enabled, returning it so the caller can keep
    /// it alive for the process lifetime and dispose it on shutdown (disposal flushes any pending
    /// spans). Returns <c>null</c> when tracing is disabled — callers can <c>using var</c> the result
    /// either way.
    /// </summary>
    public static TracerProvider? TryInitialize()
    {
        if (!DotClawConfig.OtelEnabled)
        {
            Console.WriteLine("[DotClaw] OTEL tracing: disabled (set DotClaw:Otel:Enabled=true or DOTCLAW_OTEL=on)");
            return null;
        }

        var endpoint = DotClawConfig.OtelEndpoint;
        var protocol = DotClawConfig.OtelProtocol;
        var serviceName = DotClawConfig.OtelServiceName;

        var provider = Sdk.CreateTracerProviderBuilder()
            .SetResourceBuilder(ResourceBuilder.CreateDefault()
                .AddService(serviceName, serviceVersion: "1.0.0")
                .AddAttributes(new Dictionary<string, object>
                {
                    ["service.instance.id"] = Environment.MachineName,
                    ["deployment.environment"] = "development",
                }))
            // DotClaw chat-client + agent GenAI spans (see DotClawAgentFactory).
            .AddSource(ActivitySourceName)
            // Safety net: spans emitted under the framework's default experimental source name.
            .AddSource("Experimental.Microsoft.Extensions.AI")
            // HTTP calls to Azure OpenAI, so model round-trips show up in the trace too.
            .AddHttpClientInstrumentation()
            .AddOtlpExporter(options =>
            {
                options.Endpoint = new Uri(endpoint);
                options.Protocol = protocol;
            })
            .Build();

        Console.WriteLine(
            $"[DotClaw] OTEL tracing: on → {endpoint} " +
            $"({(protocol == global::OpenTelemetry.Exporter.OtlpExportProtocol.Grpc ? "grpc" : "http/protobuf")}), " +
            $"service '{serviceName}', " +
            $"LLM request/response content: {(DotClawConfig.OtelCaptureMessageContent ? "on" : "off")}");

        return provider;
    }
}
