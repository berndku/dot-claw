namespace DotClaw.Runtime;

/// <summary>
/// One unit of work for the gateway's single consumer: a turn to run for a given route.
/// User, Heartbeat, and Approval-resume items flow through the inbound channel (so they share
/// the route's session safely); Cron runs concurrently outside the channel (see
/// <see cref="DotClaw.Cron.CronService"/>).
/// </summary>
public sealed record InboundItem(Route Route, string Text, TurnSource Source)
{
    /// <summary>Set only for <see cref="TurnSource.Approval"/> items: the parked approval to resume.</summary>
    public PendingApproval? Approval { get; init; }

    /// <summary>The human's decision for an <see cref="TurnSource.Approval"/> item.</summary>
    public bool Approved { get; init; }
}
