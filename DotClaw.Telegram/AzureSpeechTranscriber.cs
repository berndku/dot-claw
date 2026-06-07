namespace DotClaw.Telegram;

using Azure.Core;
using Azure.Identity;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DotClaw.Runtime;

internal sealed class AzureSpeechTranscriber
{
    private const string CognitiveServicesScope = "https://cognitiveservices.azure.com/.default";

    private static readonly HttpClient Http = new();
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly Uri _endpoint;
    private readonly string? _subscriptionKey;
    private readonly TokenCredential? _credential;
    private readonly string[] _locales;
    private readonly string _apiVersion;

    private AzureSpeechTranscriber(
        Uri endpoint,
        string? subscriptionKey,
        TokenCredential? credential,
        string[] locales,
        string apiVersion)
    {
        _endpoint = NormalizeEndpoint(endpoint);
        _subscriptionKey = subscriptionKey;
        _credential = credential;
        _locales = locales
            .Where(locale => !string.IsNullOrWhiteSpace(locale))
            .Select(locale => locale.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        _apiVersion = apiVersion;

        if (_locales.Length == 0)
            throw new InvalidOperationException("AzureSpeech:Locales must contain at least one locale.");
    }

    public string LocaleSummary => string.Join(",", _locales);

    public string AuthenticationSummary => string.IsNullOrWhiteSpace(_subscriptionKey)
        ? "Microsoft Entra ID"
        : "key";

    public static AzureSpeechTranscriber? FromConfiguration()
    {
        var endpoint = ConfigValue("AzureSpeech:Endpoint", "AZURE_SPEECH_ENDPOINT");
        var key = ConfigValue("AzureSpeech:Key", "AZURE_SPEECH_API_KEY", "AZURE_SPEECH_KEY");

        if (string.IsNullOrWhiteSpace(endpoint))
        {
            if (!string.IsNullOrWhiteSpace(key))
                throw new InvalidOperationException("Voice transcription requires AzureSpeech:Endpoint when AzureSpeech:Key is configured.");
            return null;
        }

        return new AzureSpeechTranscriber(
            new Uri(endpoint.Trim(), UriKind.Absolute),
            string.IsNullOrWhiteSpace(key) ? null : key.Trim(),
            string.IsNullOrWhiteSpace(key) ? new DefaultAzureCredential() : null,
            DotClawConfig.SpeechLocales,
            DotClawConfig.SpeechApiVersion);
    }

    public async Task<VoiceTranscript> TranscribeAsync(
        Stream audio,
        string fileName,
        string contentType,
        CancellationToken ct)
    {
        if (audio.CanSeek)
            audio.Position = 0;

        using var form = new MultipartFormDataContent();
        using var audioContent = new StreamContent(audio);
        audioContent.Headers.ContentType =
            MediaTypeHeaderValue.TryParse(contentType, out var mediaType)
                ? mediaType
                : new MediaTypeHeaderValue("audio/ogg");
        form.Add(audioContent, "audio", fileName);

        var definition = JsonSerializer.Serialize(new FastTranscriptionDefinition(_locales), JsonOptions);
        using var definitionContent = new StringContent(definition, Encoding.UTF8, "application/json");
        form.Add(definitionContent, "definition");

        using var request = new HttpRequestMessage(HttpMethod.Post, TranscriptionUri)
        {
            Content = form
        };
        await ApplyAuthenticationAsync(request, ct);

        using var response = await Http.SendAsync(request, ct);
        var responseBody = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
        {
            throw new AzureSpeechTranscriptionException(
                $"Azure Speech transcription failed ({(int)response.StatusCode} {response.ReasonPhrase}): {Truncate(responseBody, 500)}");
        }

        var parsed = JsonSerializer.Deserialize<FastTranscriptionResponse>(responseBody, JsonOptions)
            ?? throw new AzureSpeechTranscriptionException("Azure Speech returned an empty transcription response.");

        var text = string.Join(
            "\n",
            parsed.CombinedPhrases?
                .Select(phrase => phrase.Text)
                .Where(phrase => !string.IsNullOrWhiteSpace(phrase))
            ?? []);

        return new VoiceTranscript(text.Trim());
    }

    private Uri TranscriptionUri =>
        new(_endpoint, $"speechtotext/transcriptions:transcribe?api-version={Uri.EscapeDataString(_apiVersion)}");

    private static Uri NormalizeEndpoint(Uri endpoint) =>
        new(endpoint.ToString().TrimEnd('/') + "/");

    private async Task ApplyAuthenticationAsync(HttpRequestMessage request, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(_subscriptionKey))
        {
            request.Headers.Add("Ocp-Apim-Subscription-Key", _subscriptionKey);
            return;
        }

        if (_credential is null)
            throw new AzureSpeechTranscriptionException("Azure Speech authentication is not configured.");

        var token = await _credential.GetTokenAsync(new TokenRequestContext([CognitiveServicesScope]), ct);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
    }

    private static string? ConfigValue(string key, params string[] legacyEnvironmentVariables)
    {
        var value = AppConfiguration.Instance[key];
        if (!string.IsNullOrWhiteSpace(value))
            return value;

        foreach (var variable in legacyEnvironmentVariables)
        {
            value = Environment.GetEnvironmentVariable(variable);
            if (!string.IsNullOrWhiteSpace(value))
                return value;
        }

        return null;
    }

    private static string Truncate(string value, int maxChars)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "<empty response>";
        return value.Length <= maxChars ? value : value[..maxChars] + "...";
    }

    private sealed record FastTranscriptionDefinition(
        [property: JsonPropertyName("locales")] string[] Locales);

    private sealed record FastTranscriptionResponse(
        [property: JsonPropertyName("combinedPhrases")] IReadOnlyList<CombinedPhrase>? CombinedPhrases);

    private sealed record CombinedPhrase(
        [property: JsonPropertyName("text")] string? Text);
}

internal sealed record VoiceTranscript(string Text);

internal sealed class AzureSpeechTranscriptionException(string message) : Exception(message);
