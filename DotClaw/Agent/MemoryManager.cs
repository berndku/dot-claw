namespace DotClaw.Agent;

/// <summary>
/// Manages the agent's workspace files (SOUL.md, USER.md, etc.).
/// Seeds templates on first run and reads them for the system prompt.
/// This is the C# port of OpenClaw's memory/workspace bootstrap.
/// </summary>
public class MemoryManager
{
    private static readonly string TemplatesDir = Path.Combine(
        AppDomain.CurrentDomain.BaseDirectory, "WorkspaceTemplates");

    public string Workspace { get; }

    public MemoryManager()
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
        var files = new[] { "AGENTS.md", "BOOTSTRAP.md", "HEARTBEAT.md", "IDENTITY.md", "MEMORY.md", "SOUL.md", "USER.md" };
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
}
