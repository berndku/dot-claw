namespace DotClaw.Tools;

using System.ComponentModel;
using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;
using Spectre.Console;

/// <summary>
/// Agent tools as plain C# methods, registered via AIFunctionFactory for MAF.
/// Each method is automatically discovered and callable by the LLM.
/// </summary>
public static class AgentTools
{
    private const int MaxToolOutputChars = 8000;
    private const string ReadFileToolName = "read_file";
    private const string WriteFileToolName = "write_file";
    private const string ExecToolName = "exec";

    private static readonly string[] DangerousPatterns =
    [
        @"rm\s+-rf\s+/",
        @"mkfs",
        @"dd\s+if=",
        @":\(\)\s*\{.*\}",
        @">\s*/dev/sd",
        @"format\s+[a-zA-Z]:",
    ];

    [Description("Read the contents of a file at the given path.")]
    public static async Task<string> ReadFile(
        [Description("Absolute or relative path to the file to read.")] string path)
    {
        path = ExpandPath(path);
        AnsiConsole.MarkupLine($"  [bold magenta]⚡ read_file[/]  [dim]path=[/][white]{Markup.Escape(Truncate(path, 60))}[/]");

        try
        {
            string content;
            if (Agent.WorkspaceMemoryProvider.IsWorkspacePath(path))
            {
                // ReaderWriterLockSlim is thread-affine: the lock must be released on the same
                // thread that took it. Do the read synchronously so no await continuation can
                // resume on a different thread and trip "lock released without being held".
                Agent.WorkspaceMemoryProvider.WorkspaceLock.EnterReadLock();
                try { content = File.ReadAllText(path); }
                finally { Agent.WorkspaceMemoryProvider.WorkspaceLock.ExitReadLock(); }
            }
            else
            {
                content = await File.ReadAllTextAsync(path);
            }

            AnsiConsole.MarkupLine($"  [green]✓[/] [dim]{Markup.Escape(Truncate(content, 50))}[/]\n");
            return TruncateToolOutput(content, path);
        }
        catch (Exception ex)
        {
            var error = $"Error reading file: {ex.Message}";
            AnsiConsole.MarkupLine($"  [red]✗[/] [dim]{Markup.Escape(error)}[/]\n");
            return error;
        }
    }

    [Description("Write content to a file at the given path, creating directories as needed.")]
    public static async Task<string> WriteFile(
        [Description("Absolute or relative path to the file to write.")] string path,
        [Description("The content to write to the file.")] string content)
    {
        path = ExpandPath(path);
        AnsiConsole.MarkupLine($"  [bold magenta]⚡ write_file[/]  [dim]path=[/][white]{Markup.Escape(Truncate(path, 60))}[/]");

        try
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            // Writes to the shared workspace must be atomic and exclusive vs. concurrent readers
            // (a user turn and a cron turn can run at the same time — see WorkspaceMemoryProvider).
            if (Agent.WorkspaceMemoryProvider.IsWorkspacePath(path))
            {
                // ReaderWriterLockSlim is thread-affine: enter and exit must run on the same
                // thread, so the write must be synchronous (no await between Enter and Exit).
                Agent.WorkspaceMemoryProvider.WorkspaceLock.EnterWriteLock();
                try { AtomicWrite(path, content); }
                finally { Agent.WorkspaceMemoryProvider.WorkspaceLock.ExitWriteLock(); }
            }
            else
            {
                await File.WriteAllTextAsync(path, content);
            }

            var result = $"Wrote {content.Length} bytes to {path}";
            AnsiConsole.MarkupLine($"  [green]✓[/] [dim]{Markup.Escape(result)}[/]\n");
            return result;
        }
        catch (Exception ex)
        {
            var error = $"Error writing file: {ex.Message}";
            AnsiConsole.MarkupLine($"  [red]✗[/] [dim]{Markup.Escape(error)}[/]\n");
            return error;
        }
    }

    [Description("Run a shell command and return the output. Use for listing files, checking system state, running scripts, etc.")]
    public static async Task<string> Exec(
        [Description("The shell command to execute.")] string command)
    {
        AnsiConsole.MarkupLine($"  [bold magenta]⚡ exec[/]  [dim]command=[/][white]{Markup.Escape(Truncate(command, 60))}[/]");

        foreach (var pattern in DangerousPatterns)
        {
            if (Regex.IsMatch(command, pattern))
            {
                var blocked = $"Blocked: command matches dangerous pattern '{pattern}'";
                AnsiConsole.MarkupLine($"  [red]✗[/] [dim]{Markup.Escape(blocked)}[/]\n");
                return blocked;
            }
        }

        try
        {
            var isWindows = OperatingSystem.IsWindows();
            var psi = new ProcessStartInfo
            {
                FileName = isWindows ? "cmd.exe" : "/bin/bash",
                Arguments = isWindows ? $"/c {command}" : $"-c \"{command.Replace("\"", "\\\"")}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                // Run in the agent's workspace so relative commands (e.g. `del BOOTSTRAP.md`) target
                // the seeded ~/.dotclaw/workspace — matching the working directory advertised in the
                // system prompt and ReadFile/WriteFile's path resolution — instead of the host cwd.
                WorkingDirectory = Agent.WorkspaceMemoryProvider.WorkspaceDir,
            };

            using var process = Process.Start(psi)!;
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

            var stdout = await process.StandardOutput.ReadToEndAsync(cts.Token);
            var stderr = await process.StandardError.ReadToEndAsync(cts.Token);
            await process.WaitForExitAsync(cts.Token);

            var output = (stdout + stderr).Trim();
            var result = string.IsNullOrEmpty(output) ? "(no output)" : output;
            AnsiConsole.MarkupLine($"  [green]✓[/] [dim]{Markup.Escape(Truncate(result, 50))}[/]\n");
            return TruncateToolOutput(result, command);
        }
        catch (OperationCanceledException)
        {
            return "Error: command timed out after 60s";
        }
        catch (Exception ex)
        {
            return $"Error executing command: {ex.Message}";
        }
    }

    /// <summary>Creates the list of AIFunctions for MAF registration.</summary>
    public static IList<AIFunction> CreateAll() =>
    [
        AIFunctionFactory.Create(ReadFile, ReadFileToolName),
        AIFunctionFactory.Create(WriteFile, WriteFileToolName),
        AIFunctionFactory.Create(Exec, ExecToolName),
    ];

    private static void AtomicWrite(string path, string content)
    {
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, content);
        File.Move(tmp, path, overwrite: true);
    }

    private static string ExpandPath(string path)
    {
        path = Environment.ExpandEnvironmentVariables(path);
        if (path.StartsWith('~'))
            path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), path[2..]);

        // Relative paths resolve against the agent's workspace — the working directory advertised
        // in the system prompt (ContextBuilder.BuildBaseInstructions) — not the host process's cwd.
        // Without this, a bare "SOUL.md" would resolve next to the running .exe instead of the
        // seeded ~/.dotclaw/workspace, and the sandbox tool modes (which combine with WorkspaceDir)
        // would behave differently from cmd mode.
        if (!Path.IsPathRooted(path))
            path = Path.Combine(Agent.WorkspaceMemoryProvider.WorkspaceDir, path);

        return path;
    }

    private static string TruncateToolOutput(string output, string context)
    {
        if (output.Length <= MaxToolOutputChars)
            return output;

        var half = MaxToolOutputChars / 2;
        return output[..half]
            + $"\n\n... [truncated {output.Length - MaxToolOutputChars} chars] ...\n\n"
            + output[^half..];
    }

    private static string Truncate(string s, int max) =>
        s.Length > max ? s[..max] + "..." : s;
}
