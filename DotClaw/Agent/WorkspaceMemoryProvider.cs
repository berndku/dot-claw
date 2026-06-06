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

    public string Workspace { get; }

    public WorkspaceMemoryProvider()
        : base(null, null, null)
    {
        Workspace = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".dotclaw", "workspace");
        Directory.CreateDirectory(Workspace);
        SeedTemplates();
    }

    private void SeedTemplates()
    {
        if (!Directory.Exists(TemplatesDir)) return;

        var isFresh = !File.Exists(Path.Combine(Workspace, "SOUL.md"));

        foreach (var templatePath in Directory.GetFiles(TemplatesDir, "*.md"))
        {
            var filename = Path.GetFileName(templatePath);

            // Only seed BOOTSTRAP.md on first run
            if (filename == "BOOTSTRAP.md" && !isFresh)
                continue;

            var dest = Path.Combine(Workspace, filename);
            if (!File.Exists(dest))
                File.Copy(templatePath, dest);
        }
    }

    /// <summary>
    /// Reads all workspace files and returns them as tag → content pairs.
    /// </summary>
    public Dictionary<string, string> ReadAll()
    {
        var files = new[] { "AGENTS.md", "BOOTSTRAP.md", "IDENTITY.md", "MEMORY.md", "SOUL.md", "USER.md" };
        var result = new Dictionary<string, string>();

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

        return result;
    }

    protected override ValueTask<AIContext> ProvideAIContextAsync(
        InvokingContext context, CancellationToken cancellationToken = default)
    {
        // Read workspace files fresh on every invocation.
        var instructions = ContextBuilder.BuildWorkspaceContext(this);
        return new ValueTask<AIContext>(new AIContext { Instructions = instructions });
    }
}
