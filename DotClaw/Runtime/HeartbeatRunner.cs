namespace DotClaw.Runtime;

using System.Threading.Channels;

/// <summary>
/// The ambient heartbeat: a separate <see cref="PeriodicTimer"/> that, on each tick, enqueues a
/// <see cref="TurnSource.Heartbeat"/> item for the current active route. It is just another producer
/// into the inbound channel, so heartbeat turns run on the single consumer (serialized with user
/// turns) — independent of cron, which self-delivers from isolated sessions.
/// </summary>
public sealed class HeartbeatRunner
{
    private readonly ChannelWriter<InboundItem> _inbound;
    private readonly Func<Route?> _currentRoute;
    private readonly TimeSpan _interval;

    public HeartbeatRunner(ChannelWriter<InboundItem> inbound, Func<Route?> currentRoute, TimeSpan interval)
    {
        _inbound = inbound;
        _currentRoute = currentRoute;
        _interval = interval;
    }

    public void Start(CancellationToken ct) => _ = Task.Run(() => LoopAsync(ct), ct);

    private async Task LoopAsync(CancellationToken ct)
    {
        Console.WriteLine($"[heartbeat] enabled — every {_interval.TotalSeconds:0}s");
        using var timer = new PeriodicTimer(_interval);
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                var route = _currentRoute();
                if (route is null)
                {
                    Console.WriteLine("[heartbeat] tick — no active route yet, skipping");
                    continue;
                }

                Console.WriteLine($"[heartbeat] tick → queueing check for {route.ChatId}");
                _inbound.TryWrite(new InboundItem(route, "", TurnSource.Heartbeat));
            }
        }
        catch (OperationCanceledException) { }
    }
}
