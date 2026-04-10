namespace DotClaw.Agent;

using System.Text;

/// <summary>
/// Builds the system prompt from workspace files and runtime context.
/// Wraps each workspace file in XML-style tags, just like OpenClaw does.
/// </summary>
public static class ContextBuilder
{
    public static string BuildSystemPrompt(MemoryManager memory, string? channel = null, string? chatId = null)
    {
        var now = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm 'UTC'");
        var sb = new StringBuilder();

        sb.AppendLine("You are a personal AI assistant.");
        sb.AppendLine($"Current date/time: {now}");
        sb.AppendLine($"Workspace: {memory.Workspace}");
        sb.AppendLine($"Always use absolute paths when reading or writing files. Your workspace is {memory.Workspace}.");

        if (!string.IsNullOrEmpty(channel))
            sb.AppendLine($"Channel: {channel}");
        if (!string.IsNullOrEmpty(chatId))
            sb.AppendLine($"Chat ID: {chatId}");

        // Append workspace files as tagged sections
        var workspaceFiles = memory.ReadAll();

        if (workspaceFiles.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("# Project Context");
            sb.AppendLine();
            sb.AppendLine("The following workspace files have been loaded:");

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
