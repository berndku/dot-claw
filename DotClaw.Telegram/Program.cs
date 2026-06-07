using System.Threading.Channels;
using DotClaw.Cron;
using DotClaw.Runtime;
using DotClaw.Telegram;
using Telegram.Bot;

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

var inbound = Channel.CreateUnbounded<InboundItem>(
    new UnboundedChannelOptions { SingleReader = true });

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
                if (item.Source == TurnSource.User)
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
        var updates = await bot.GetUpdates(offset, timeout: 30, cancellationToken: cts.Token);

        foreach (var update in updates)
        {
            offset = update.Id + 1;

            if (update.Message?.Text is not { } userText) continue;
            if (update.Message.Chat is not { } chat) continue;

            Console.WriteLine($"[{chat.Id}] {chat.FirstName}: {userText}");

            var route = new Route("telegram", chat.Id.ToString());
            lastRoute = route;
            await inbound.Writer.WriteAsync(new InboundItem(route, userText, TurnSource.User), cts.Token);
        }
    }
    catch (OperationCanceledException) { break; }
    catch (Exception ex)
    {
        Console.WriteLine($"⚠️ Polling error: {ex.Message}");
        await Task.Delay(3000, cts.Token);
    }
}

inbound.Writer.TryComplete();
try { await consumer; } catch { }
Console.WriteLine("Goodbye.");
