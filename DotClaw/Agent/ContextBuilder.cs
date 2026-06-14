namespace DotClaw.Agent;

using System.Text;

/// <summary>
/// Builds the system prompt from workspace files and runtime context.
/// Wraps each workspace file in XML-style tags, just like OpenClaw does.
/// <para>
/// Split into two parts so that the static preamble can be passed as the agent's
/// immutable <c>instructions</c>, while the workspace-file section is produced fresh
/// on every invocation by <see cref="WorkspaceMemoryProvider"/>.
/// </para>
/// </summary>
public static class ContextBuilder
{
    /// <summary>
    /// The static system-prompt preamble. Safe to bake into the agent's immutable
    /// instructions because none of it changes during a session.
    /// </summary>
    public static string BuildBaseInstructions(WorkspaceMemoryProvider memory, string? channel = null, string? chatId = null)
    {
        var sb = new StringBuilder();

        sb.AppendLine("You are a personal AI assistant.");
        sb.AppendLine($"Working directory: {memory.Workspace}");
        sb.AppendLine("Use the tools available to you. If an operation fails, adapt and try a different approach — don't speculate about system limitations you haven't encountered.");

        // Memory is the assistant's single biggest failure mode: it acknowledges a durable fact in
        // prose ("noted!") and then never persists it, so the fact is gone when the session ends.
        // This lives in the immutable base instructions (not just AGENTS.md) so the directive is
        // high-priority on every turn, including turns whose main job is some other task.
        sb.AppendLine();
        sb.AppendLine(
            "Remember your human. The moment the user reveals a durable fact about themselves — their name, "
            + "where they live, nationality, timezone, pronouns, preferences, important people or projects, or "
            + "anything they ask you to remember — persist it in the same turn with write_file: profile facts go "
            + "in USER.md, other lasting notes in MEMORY.md. Read the file first and merge; never overwrite "
            + "existing content. Save before or alongside your reply, not \"later\" — a verbal acknowledgement is "
            + "not memory; only a file write survives the session ending. Persist silently; don't announce it.");

        if (!string.IsNullOrEmpty(channel))
            sb.AppendLine($"Channel: {channel}");
        if (!string.IsNullOrEmpty(chatId))
            sb.AppendLine($"Chat ID: {chatId}");

        return sb.ToString();
    }

    /// <summary>
    /// The dynamic context section: current date/time plus the tagged workspace files.
    /// Read fresh on every agent invocation so that mid-session edits to MEMORY.md (etc.)
    /// are picked up without rebuilding the agent.
    /// </summary>
    public static string BuildWorkspaceContext(WorkspaceMemoryProvider memory)
    {
        var now = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm 'UTC'");
        var sb = new StringBuilder();

        sb.AppendLine($"Current date/time: {now}");

        var workspaceFiles = memory.ReadAll();

        // OpenClaw parity: workspace files (including BOOTSTRAP.md on a fresh workspace) are injected
        // plainly into Project Context with no special "bootstrap" steering. BOOTSTRAP.md already tells
        // the agent how to open the first conversation; layering extra "greet exactly once / don't quote
        // the example" directives on top contradicts the file and makes the model emit the greeting
        // twice. Let the file speak for itself — OpenClaw does exactly this and gets a single greeting.
        //
        // One guard we *do* add: a note that the contents below are the latest version, refreshed every
        // turn. Without it the model treats the `## FILE.md` headings as references and redundantly calls
        // read_file on a file it already has in full — re-injecting BOOTSTRAP's "greet now" text right
        // before generation, which is itself a path to the duplicate greeting.
        if (workspaceFiles.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("# Project Context");
            sb.AppendLine();
            sb.AppendLine("The following workspace files define your identity, behavior, and context.");
            sb.AppendLine("Their full contents below are always the latest version, refreshed every turn — treat them as authoritative and current. Don't list the workspace or call read_file on them to \"check\"; you already have everything here. Use your tools for the user's tasks, not to re-read this context.");

            if (workspaceFiles.ContainsKey("agents"))
                sb.AppendLine("If AGENTS.md is present, follow its operational guidance (including startup routines and red-line constraints) unless higher-priority instructions override it.");

            if (workspaceFiles.ContainsKey("soul"))
                sb.AppendLine("If SOUL.md is present, embody its persona and tone. Avoid stiff, generic replies; follow its guidance unless higher-priority instructions override it.");

            sb.AppendLine();

            foreach (var (tag, content) in workspaceFiles)
            {
                sb.AppendLine($"## {tag.ToUpperInvariant()}.md");
                sb.AppendLine();
                sb.AppendLine(content);
                sb.AppendLine();
            }
        }

        return sb.ToString();
    }
}
