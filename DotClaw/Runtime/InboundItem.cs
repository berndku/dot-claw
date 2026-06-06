namespace DotClaw.Runtime;

/// <summary>
/// One unit of work for the gateway's single consumer: a turn to run for a given route.
/// User and Heartbeat items flow through the inbound channel; Cron runs concurrently
/// outside the channel (see <see cref="DotClaw.Cron.CronService"/>).
/// </summary>
public sealed record InboundItem(Route Route, string Text, TurnSource Source);
