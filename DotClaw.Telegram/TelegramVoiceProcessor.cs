namespace DotClaw.Telegram;

using System.Threading.Channels;
using DotClaw.Runtime;
using global::Telegram.Bot;
using global::Telegram.Bot.Types;

internal sealed class TelegramVoiceProcessor
{
    private const int TelegramDownloadLimitBytes = 20 * 1024 * 1024;

    private readonly TelegramBotClient _bot;
    private readonly TelegramMessageSink _sink;
    private readonly ChannelWriter<InboundItem> _inbound;
    private readonly AzureSpeechTranscriber? _transcriber;
    private readonly SemaphoreSlim _gate;

    public TelegramVoiceProcessor(
        TelegramBotClient bot,
        TelegramMessageSink sink,
        ChannelWriter<InboundItem> inbound,
        AzureSpeechTranscriber? transcriber,
        int maxConcurrency)
    {
        _bot = bot;
        _sink = sink;
        _inbound = inbound;
        _transcriber = transcriber;
        _gate = new SemaphoreSlim(maxConcurrency);
    }

    public async Task ProcessAsync(Message message, Route route, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            await ProcessCoreAsync(message, route, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (AzureSpeechTranscriptionException ex)
        {
            Console.WriteLine($"[voice] transcription failed for {route.ChatId}: {ex.Message}");
            await _sink.SendAsync(route, "Sorry, I couldn't transcribe that voice message. Please try again or send text.", ct);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[voice] failed for {route.ChatId}: {ex.Message}");
            await _sink.SendAsync(route, "Sorry, I couldn't process that voice message. Please try again or send text.", ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task ProcessCoreAsync(Message message, Route route, CancellationToken ct)
    {
        if (message.Voice is not { } voice)
            return;

        if (_transcriber is null)
        {
            await _sink.SendAsync(
                route,
                "Voice messages need Azure Speech configuration. Set AzureSpeech:Endpoint and AzureSpeech:Key in appsettings.local.json, then restart the gateway.",
                ct);
            return;
        }

        if (voice.FileSize is > TelegramDownloadLimitBytes)
        {
            await _sink.SendAsync(route, "That voice message is too large for Telegram Bot API download. Please send a shorter clip.", ct);
            return;
        }

        await _sink.TypingAsync(route, ct);

        var file = await _bot.GetFile(voice.FileId, ct);
        if (file.FileSize is > TelegramDownloadLimitBytes)
        {
            await _sink.SendAsync(route, "That voice message is too large for Telegram Bot API download. Please send a shorter clip.", ct);
            return;
        }

        using var audio = new MemoryStream();
        await _bot.DownloadFile(file, audio, ct);

        var contentType = NormalizeContentType(voice.MimeType);
        var transcript = await _transcriber.TranscribeAsync(audio, FileNameFor(contentType), contentType, ct);

        if (string.IsNullOrWhiteSpace(transcript.Text))
        {
            await _sink.SendAsync(route, "I couldn't hear any speech in that voice message. Please try again or send text.", ct);
            return;
        }

        await _inbound.WriteAsync(new InboundItem(route, FormatTranscript(transcript.Text, voice), TurnSource.User), ct);
    }

    private static string NormalizeContentType(string? mimeType) =>
        string.IsNullOrWhiteSpace(mimeType) ? "audio/ogg" : mimeType.Trim();

    private static string FileNameFor(string contentType) =>
        contentType.Contains("webm", StringComparison.OrdinalIgnoreCase) ? "voice.webm" : "voice.ogg";

    private static string FormatTranscript(string text, Voice voice)
    {
        var duration = voice.Duration > 0 ? $", duration {voice.Duration}s" : "";
        return $"[Voice message transcript{duration}]\n{text}";
    }
}
