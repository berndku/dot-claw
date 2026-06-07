namespace DotClaw.Agent;

/// <summary>
/// Runtime-configurable policy that decides which tools require human approval before they
/// run. In MAF, approval is a <i>registration-time</i> decision: a tool is gated by wrapping
/// it in an <c>ApprovalRequiredAIFunction</c>. This policy simply supplies the set of tool
/// names to gate, sourced from configuration (an env var) rather than being hardcoded or
/// attribute-based. Because both in-process tools and MCP/sandbox tools are <c>AIFunction</c>s
/// with a <c>.Name</c>, the same policy covers everything uniformly.
/// </summary>
public sealed class ApprovalPolicy
{
    private readonly HashSet<string> _names;

    public ApprovalPolicy(IEnumerable<string> toolNames)
        => _names = new HashSet<string>(toolNames, StringComparer.OrdinalIgnoreCase);

    /// <summary>True when no tool requires approval (gating is effectively disabled).</summary>
    public bool IsEmpty => _names.Count == 0;

    /// <summary>The set of tool names that require approval (for logging/display).</summary>
    public IReadOnlyCollection<string> GatedNames => _names;

    /// <summary>Whether the named tool must be approved before it runs.</summary>
    public bool RequiresApproval(string toolName) => _names.Contains(toolName);

    /// <summary>
    /// Reads <c>DOTCLAW_APPROVAL_TOOLS</c> (comma-separated, case-insensitive tool names),
    /// falling back to <paramref name="defaults"/> when the variable is unset or blank.
    /// </summary>
    public static ApprovalPolicy FromEnvironment(IEnumerable<string>? defaults = null)
    {
        var env = Environment.GetEnvironmentVariable("DOTCLAW_APPROVAL_TOOLS");
        var names = !string.IsNullOrWhiteSpace(env)
            ? env.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : (defaults ?? Array.Empty<string>()).ToArray();
        return new ApprovalPolicy(names);
    }

    /// <summary>A policy that gates nothing — preserves default (no-approval) behavior.</summary>
    public static readonly ApprovalPolicy None = new(Array.Empty<string>());
}
