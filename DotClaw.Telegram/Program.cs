using System.Threading.Channels;
using DotClaw.Cron;
using DotClaw.Runtime;
using DotClaw.Telegram;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

// ── OpenTelemetry tracing (opt-in) ─────────────────────────────
// Exports agent + chat-completion GenAI spans over OTLP (e.g. to the Aspire dashboard). No-op when
// DotClaw:Otel:Enabled is false. Kept alive for the whole process; disposal flushes pending spans.
using var tracerProvider = Telemetry.TryInitialize();

// ── Resolve Telegram Bot Token ─────────────────────────────────
var botToken = AppConfiguration.Instance["Telegram:BotToken"];
if (string.IsNullOrWhiteSpace(botToken))
    throw new InvalidOperationException(
        "Telegram:BotToken not configured.\n" +
        "1. Open Telegram → talk to @BotFather → /newbot\n" +
        "2. Copy the token\n" +
        "3. Add it to appsettings.local.json under Telegram:BotToken");

var bot = new TelegramBotClient(botToken);
var me = await bot.GetMe();
Console.WriteLine($"🦞 DotClaw Telegram Gateway — connected as @{me.Username}");
Console.WriteLine("Listening for messages... (Ctrl+C to stop)\n");

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

// ── Runtime wiring ─────────────────────────────────────────────
// One inbound queue drained by a single consumer → user + heartbeat turns are serialized
// (they share the user's session). Cron jobs run concurrently in isolated sessions and
// self-deliver, mirroring real OpenClaw.
var sink = new TelegramMessageSink(bot);
var cron = new CronService();
var runner = new AgentRunner(sink, cron);
var speechTranscriber = AzureSpeechTranscriber.FromConfiguration();

var inbound = Channel.CreateUnbounded<InboundItem>(
    new UnboundedChannelOptions { SingleReader = true });
var voiceProcessor = new TelegramVoiceProcessor(
    bot,
    sink,
    inbound.Writer,
    speechTranscriber,
    DotClawConfig.VoiceTranscriptionConcurrency);
var voiceTasks = new List<Task>();

if (speechTranscriber is null)
    Console.WriteLine("[voice] disabled (set AzureSpeech:Endpoint to enable Telegram voice)");
else
    Console.WriteLine($"[voice] enabled via Azure Speech Fast Transcription ({speechTranscriber.LocaleSummary}, {speechTranscriber.AuthenticationSummary})");

// The most recently active chat — the heartbeat checks in on whoever spoke last.
Route? lastRoute = null;

cron.Start((job, ct) => runner.RunCronAsync(job, ct), cts.Token);

if (DotClawConfig.HeartbeatEnabled)
{
    var heartbeat = new HeartbeatRunner(
        inbound.Writer, () => lastRoute, DotClawConfig.HeartbeatInterval);
    heartbeat.Start(cts.Token);
}
else
{
    Console.WriteLine("[heartbeat] disabled (set DOTCLAW_HEARTBEAT=on to enable)");
}

// ── Single consumer: serializes user + heartbeat turns ─────────
var consumer = Task.Run(async () =>
{
    try
    {
        await foreach (var item in inbound.Reader.ReadAllAsync(cts.Token))
        {
            try
            {
                await runner.RunInboundAsync(item, cts.Token);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                Console.WriteLine($"⚠️ Error handling {item.Source} turn for {item.Route.ChatId}: {ex.Message}");
                if (item.Source is TurnSource.User or TurnSource.Approval)
                {
                    try { await sink.SendAsync(item.Route, "Sorry, something went wrong. Try again!", cts.Token); }
                    catch { }
                }
            }
        }
    }
    catch (OperationCanceledException) { }
});

// ── Long-polling loop: produces user items ─────────────────────
var offset = 0;
while (!cts.Token.IsCancellationRequested)
{
    try
    {
        var updates = await bot.GetUpdates(
            offset, timeout: 30,
            allowedUpdates: [UpdateType.Message, UpdateType.CallbackQuery],
            cancellationToken: cts.Token);

        foreach (var update in updates)
        {
            offset = update.Id + 1;

            // Button tap on an approval prompt → resolve the parked approval.
            if (update.CallbackQuery is { } callback)
            {
                _ = Task.Run(() => HandleCallback(callback));
                continue;
            }

            if (update.Message is not { } message) continue;
            if (message.Chat is not { } chat) continue;

            var route = new Route("telegram", chat.Id.ToString());

            if (message.Text is { } userText)
            {
                Console.WriteLine($"[{chat.Id}] {chat.FirstName}: {userText}");
                lastRoute = route;
                await inbound.Writer.WriteAsync(new InboundItem(route, userText, TurnSource.User), cts.Token);
                continue;
            }

            if (message.Voice is { } voice)
            {
                Console.WriteLine($"[{chat.Id}] {chat.FirstName}: [voice {voice.Duration}s]");
                lastRoute = route;
                voiceTasks.RemoveAll(task => task.IsCompleted);
                voiceTasks.Add(Task.Run(() => voiceProcessor.ProcessAsync(message, route, cts.Token)));
            }
        }
    }
    catch (OperationCanceledException) { break; }
    catch (Exception ex)
    {
        Console.WriteLine($"⚠️ Polling error: {ex.Message}");
        await Task.Delay(3000, cts.Token);
    }
}

try { await Task.WhenAll(voiceTasks); }
catch (OperationCanceledException) when (cts.Token.IsCancellationRequested) { }
catch (Exception ex) { Console.WriteLine($"[voice] shutdown wait failed: {ex.Message}"); }
inbound.Writer.TryComplete();
try { await consumer; } catch { }
Console.WriteLine("Goodbye.");

// ── Approval callback handler ──────────────────────────────────
// A button tap resolves a parked approval. State is in-memory (demo-grade), so a bot
// restart drops outstanding approvals — handled gracefully as "expired". The Telegram-specific
// UI lives here; the actual agent resume is delegated to the shared AgentRunner.
async Task HandleCallback(CallbackQuery callback)
{
    var data = callback.Data ?? "";
    var parts = data.Split('|', 2);
    var action = parts[0];
    var token = parts.Length > 1 ? parts[1] : "";

    // Only "a" (approve) or "d" (deny) are valid; atomically consume the pending entry so a
    // double-tap (or a stale/unknown callback) can't approve twice or resolve the wrong thing.
    var valid = action is "a" or "d";
    if (!valid || string.IsNullOrEmpty(token) || !ApprovalStore.Items.TryRemove(token, out var pending))
    {
        await bot.AnswerCallbackQuery(callback.Id, "This request expired — please ask again.",
            cancellationToken: cts.Token);
        if (callback.Message is { } expiredMsg)
        {
            try { await bot.EditMessageReplyMarkup(expiredMsg.Chat.Id, expiredMsg.MessageId, replyMarkup: null, cancellationToken: cts.Token); }
            catch { }
        }
        return;
    }

    var approved = action == "a";

    // Acknowledge immediately (clears the client's spinner) and freeze the buttons.
    await bot.AnswerCallbackQuery(callback.Id, approved ? "Approved" : "Denied", cancellationToken: cts.Token);
    if (callback.Message is { } promptMsg)
    {
        try
        {
            await bot.EditMessageText(promptMsg.Chat.Id, promptMsg.MessageId,
                (promptMsg.Text ?? "Approval") + (approved ? "\n\n✅ Approved" : "\n\n❌ Denied"),
                replyMarkup: null, cancellationToken: cts.Token);
        }
        catch { }
    }

    // Resume on the SINGLE consumer (not on this callback thread) so the agent run and its history
    // write are serialized with the chat's user/heartbeat turns — no concurrent writes to the
    // session file. The runner re-parks automatically if the resume itself needs another approval.
    try
    {
        await inbound.Writer.WriteAsync(
            new InboundItem(pending.Route, "", TurnSource.Approval) { Approval = pending, Approved = approved },
            cts.Token);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"⚠️ Could not enqueue approval resume for {pending.Route.ChatId}: {ex.Message}");
    }
}
