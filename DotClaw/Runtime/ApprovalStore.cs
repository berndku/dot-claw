namespace DotClaw.Runtime;

using System.Collections.Concurrent;
using Microsoft.Extensions.AI;

/// <summary>
/// Channel-agnostic details a sink needs to render an approval prompt to the human.
/// </summary>
public sealed record ApprovalRequest(string Token, string ToolName, string ArgumentsText);

/// <summary>
/// Parked state for a pending approval awaiting a human decision: the full message list to
/// resume with (including the assistant message carrying the request), the request itself,
/// and the routing/persistence info needed to deliver the eventual reply.
/// </summary>
public sealed record PendingApproval(
    string Token,
    List<ChatMessage> Messages,
    ToolApprovalRequestContent Request,
    Route Route,
    string SessionKey,
    string UserText);

/// <summary>
/// In-memory store of approvals awaiting a button tap (demo-grade; lost on restart, in which
/// case outstanding approvals are reported to the user as "expired").
/// </summary>
public static class ApprovalStore
{
    public static readonly ConcurrentDictionary<string, PendingApproval> Items = new();
}
