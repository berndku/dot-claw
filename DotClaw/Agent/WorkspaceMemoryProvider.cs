namespace DotClaw.Agent;

using Microsoft.Agents.AI;

/// <summary>
/// A read-only <see cref="AIContextProvider"/> that injects the agent's workspace memory
/// (SOUL.md, USER.md, MEMORY.md, …) into every invocation.
/// <para>
/// This replaces baking the workspace files into the agent's immutable instructions at
/// construction time. Because <see cref="ProvideAIContextAsync"/> runs around each
/// invocation, edits the agent makes to its own memory files mid-session (via the
/// <c>write_file</c> tool) are picked up on the very next turn — no agent rebuild needed.
/// </para>
/// <para>
/// Memory <em>writes</em> remain LLM-driven (the agent updates MEMORY.md itself), matching
/// dot-claw's file-based, user-inspectable memory model. Hence no <c>StoreAIContextAsync</c>
/// override. The provider is stateless and holds only a <see cref="MemoryManager"/>
/// reference, so a single instance is safe to share across sessions.
/// </para>
/// </summary>
internal sealed class WorkspaceMemoryProvider : AIContextProvider
{
    private readonly MemoryManager _memory;

    public WorkspaceMemoryProvider(MemoryManager memory)
        : base(null, null, null)
    {
        _memory = memory;
    }

    protected override ValueTask<AIContext> ProvideAIContextAsync(
        InvokingContext context, CancellationToken cancellationToken = default)
    {
        // Read workspace files fresh on every invocation.
        var instructions = ContextBuilder.BuildWorkspaceContext(_memory);
        return new ValueTask<AIContext>(new AIContext { Instructions = instructions });
    }
}
