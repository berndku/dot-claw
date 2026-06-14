namespace DotClaw.Agent;

using Microsoft.Agents.AI;

/// <summary>
/// Owns the agent's workspace files (SOUL.md, USER.md, MEMORY.md, …) and injects them as
/// read-only context into every invocation.
/// <para>
/// On construction it seeds the workspace from <c>WorkspaceTemplates/</c> (first run only)
/// and exposes <see cref="ReadAll"/> for the <see cref="ContextBuilder"/>. As an
/// <see cref="AIContextProvider"/>, <see cref="ProvideAIContextAsync"/> runs around each
/// invocation, so edits the agent makes to its own memory files mid-session (via the
/// <c>write_file</c> tool) are picked up on the very next turn — no agent rebuild needed.
/// </para>
/// <para>
/// Memory <em>writes</em> remain LLM-driven (the agent updates MEMORY.md itself), matching
/// dot-claw's file-based, user-inspectable memory model. Hence no <c>StoreAIContextAsync</c>
/// override. The provider holds only the workspace path, so a single instance is safe to
/// share across sessions.
/// </para>
/// <para>
/// This is the C# port of OpenClaw's memory/workspace bootstrap.
/// </para>
/// </summary>
public sealed class WorkspaceMemoryProvider : AIContextProvider
{
    private static readonly string TemplatesDir = Path.Combine(
        AppDomain.CurrentDomain.BaseDirectory, "WorkspaceTemplates");

    /// <summary>The workspace directory, identical across all instances/sessions.</summary>
    public static readonly string WorkspaceDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".dotclaw", "workspace");

    // Process-wide guard so concurrent turns (user + cron) never read a torn workspace or
    // lose a write. Reads share; writes are exclusive. See AgentTools.WriteFile.
    public static readonly ReaderWriterLockSlim WorkspaceLock = new(LockRecursionPolicy.NoRecursion);

    private static readonly object SeedLock = new();

    public string Workspace { get; }

    public WorkspaceMemoryProvider()
        : base(null, null, null)
    {
        Workspace = WorkspaceDir;
        Directory.CreateDirectory(Workspace);
        SeedTemplates();
    }

    private void SeedTemplates()
    {
        if (!Directory.Exists(TemplatesDir)) return;

        // Concurrent instances (e.g. a user turn and a cron turn constructing at once) must not
        // race on first-run seeding.
        lock (SeedLock)
        {
            var isFresh = !File.Exists(Path.Combine(Workspace, "SOUL.md"));

            foreach (var templatePath in Directory.GetFiles(TemplatesDir, "*.md"))
            {
                var filename = Path.GetFileName(templatePath);

                // Only seed BOOTSTRAP.md on first run
                if (filename == "BOOTSTRAP.md" && !isFresh)
                    continue;

                var dest = Path.Combine(Workspace, filename);
                try
                {
                    File.Copy(templatePath, dest, overwrite: false);
                }
                catch (IOException) when (File.Exists(dest))
                {
                    // Already seeded (possibly by a concurrent instance) — fine.
                }
            }
        }
    }

    /// <summary>
    /// Reads the workspace files for injection and returns them as tag → content pairs.
    /// On a fresh workspace (BOOTSTRAP.md still present) AGENTS.md and MEMORY.md are withheld so the
    /// first-run conversation isn't derailed into filesystem exploration.
    /// </summary>
    public Dictionary<string, string> ReadAll()
    {
        var result = new Dictionary<string, string>();

        // Snapshot all files under a single read lock so a concurrent writer can't give this turn
        // a half-updated, inconsistent view across files.
        WorkspaceLock.EnterReadLock();
        try
        {
            // Fresh workspace: BOOTSTRAP.md is still present (the agent deletes it once it knows who it
            // is). During bootstrap we withhold AGENTS.md and MEMORY.md — AGENTS.md's tool list and
            // filesystem framing nudge weaker models to "explore" (list the workspace, re-read files
            // they already have in context) instead of just having the first conversation. OpenClaw
            // does the same, injecting only [BOOTSTRAP, IDENTITY, USER, SOUL] on the first run.
            var isBootstrap = File.Exists(Path.Combine(Workspace, "BOOTSTRAP.md"));
            var files = isBootstrap
                ? new[] { "BOOTSTRAP.md", "IDENTITY.md", "USER.md", "SOUL.md" }
                : new[] { "AGENTS.md", "IDENTITY.md", "MEMORY.md", "SOUL.md", "USER.md" };

            foreach (var filename in files)
            {
                var path = Path.Combine(Workspace, filename);
                if (!File.Exists(path)) continue;

                var content = File.ReadAllText(path).Trim();
                if (!string.IsNullOrEmpty(content))
                {
                    var key = Path.GetFileNameWithoutExtension(filename).ToLowerInvariant();
                    result[key] = content;
                }
            }
        }
        finally { WorkspaceLock.ExitReadLock(); }

        return result;
    }

    /// <summary>
    /// Reads a single workspace file's raw contents (e.g. HEARTBEAT.md, which is excluded from the
    /// per-turn context). Returns <c>null</c> if the file is missing or empty.
    /// </summary>
    public string? TryReadRaw(string filename)
    {
        var path = Path.Combine(Workspace, filename);
        WorkspaceLock.EnterReadLock();
        try
        {
            if (!File.Exists(path)) return null;
            var content = File.ReadAllText(path);
            return string.IsNullOrWhiteSpace(content) ? null : content;
        }
        finally { WorkspaceLock.ExitReadLock(); }
    }

    /// <summary>True if <paramref name="fullPath"/> lives inside the shared workspace directory.</summary>
    public static bool IsWorkspacePath(string fullPath)
    {
        var normalized = Path.GetFullPath(fullPath);
        var root = Path.GetFullPath(WorkspaceDir);
        return normalized.StartsWith(root, StringComparison.OrdinalIgnoreCase);
    }

    protected override ValueTask<AIContext> ProvideAIContextAsync(
        InvokingContext context, CancellationToken cancellationToken = default)
    {
        // Read workspace files fresh on every invocation.
        var instructions = ContextBuilder.BuildWorkspaceContext(this);
        return new ValueTask<AIContext>(new AIContext { Instructions = instructions });
    }
}
