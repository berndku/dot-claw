namespace DotClaw.Tools;

using System.ComponentModel;
using System.Text;
using DotClaw.Agent;
using Microsoft.Extensions.AI;
using Sabbour.Mxc.Sdk;
using Sabbour.Mxc.Sdk.Errors;
using Sabbour.Mxc.Sdk.Sandbox;
using Spectre.Console;

/// <summary>
/// In-process MXC tools backed by Sabbour.Mxc.Sdk.
/// </summary>
public static class CSharpSandboxTools
{
    private const string SchemaVersion = "0.6.0-alpha";
    private const int TimeoutMs = 60_000;
    private const int MaxToolOutputChars = 8000;
    private const string ReadFileToolName = "read_file";
    private const string WriteFileToolName = "write_file";
    private const string ExecToolName = "exec";
    private static readonly char[] CmdUnsafePathChars = ['"', '%', '&', '|', '<', '>', '^', '\r', '\n'];

    private static readonly SandboxSpawner Spawner = new();
    private static readonly SemaphoreSlim FileToolLock = new(1, 1);

    [Description("Read the contents of a file inside the persistent DotClaw workspace.")]
    public static async Task<string> ReadFile(
        [Description("Path to the file to read, relative to the DotClaw workspace.")] string path)
    {
        AnsiConsole.MarkupLine($"  [bold magenta]⚡ read_file[/]  [dim]mode=[/] [white]csharp-sandbox[/] [dim]path=[/][white]{Markup.Escape(Truncate(path, 60))}[/]");

        var target = ResolveInWorkspace(path, out var refusal);
        if (target is null)
            return Refused(refusal!);

        await FileToolLock.WaitAsync();
        try
        {
            var result = await RunInSandboxAsync($@"cmd /c type ""{target}""");
            return FinishSuccess(FormatResult(result));
        }
        catch (OperationCanceledException)
        {
            return FinishError("Error: command timed out after 60s");
        }
        catch (MxcException ex)
        {
            return FinishError(FormatMxcError("reading file", ex));
        }
        catch (Exception ex)
        {
            return FinishError(FormatSandboxError("reading file", ex));
        }
        finally
        {
            FileToolLock.Release();
        }
    }

    [Description("Write content to a file inside the persistent DotClaw workspace.")]
    public static async Task<string> WriteFile(
        [Description("Path to the file to write, relative to the DotClaw workspace.")] string path,
        [Description("The content to write to the file.")] string content)
    {
        AnsiConsole.MarkupLine($"  [bold magenta]⚡ write_file[/]  [dim]mode=[/] [white]csharp-sandbox[/] [dim]path=[/][white]{Markup.Escape(Truncate(path, 60))}[/]");

        var target = ResolveInWorkspace(path, out var refusal);
        if (target is null)
            return Refused(refusal!);

        var staging = Path.Combine(
            WorkspaceMemoryProvider.WorkspaceDir,
            $"dotclaw-write-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}-{Guid.NewGuid():N}.tmp");
        var dir = Path.GetDirectoryName(target) ?? WorkspaceMemoryProvider.WorkspaceDir;
        var sandboxTarget = Path.Combine(
            dir,
            $".{Path.GetFileName(target)}.dotclaw-{Guid.NewGuid():N}.tmp");

        await FileToolLock.WaitAsync();
        try
        {
            Directory.CreateDirectory(WorkspaceMemoryProvider.WorkspaceDir);
            Directory.CreateDirectory(dir);
            await File.WriteAllTextAsync(staging, content);

            var byteCount = Encoding.UTF8.GetByteCount(content);
            var result = await RunInSandboxAsync($@"cmd /c copy /Y ""{staging}"" ""{sandboxTarget}"" >nul");
            if (result.ExitCode != 0)
                return FinishSuccess(FormatResult(result));

            WorkspaceMemoryProvider.WorkspaceLock.EnterWriteLock();
            try
            {
                File.Move(sandboxTarget, target, overwrite: true);
            }
            finally
            {
                WorkspaceMemoryProvider.WorkspaceLock.ExitWriteLock();
            }

            return FinishSuccess($"Wrote {byteCount} bytes to {target}");
        }
        catch (OperationCanceledException)
        {
            return FinishError("Error: command timed out after 60s");
        }
        catch (MxcException ex)
        {
            return FinishError(FormatMxcError("writing file", ex));
        }
        catch (Exception ex)
        {
            return FinishError(FormatSandboxError("writing file", ex));
        }
        finally
        {
            try { File.Delete(staging); }
            finally
            {
                try { File.Delete(sandboxTarget); }
                finally { FileToolLock.Release(); }
            }
        }
    }

    [Description("Run a shell command inside an MXC sandbox and return the output. Network is blocked; only the persistent DotClaw workspace and built-in Windows tools in System32 are reachable.")]
    public static async Task<string> Exec(
        [Description("The shell command to execute.")] string command)
    {
        AnsiConsole.MarkupLine($"  [bold magenta]⚡ exec[/]  [dim]mode=[/] [white]csharp-sandbox[/] [dim]command=[/][white]{Markup.Escape(Truncate(command, 60))}[/]");

        try
        {
            var result = await RunInSandboxAsync($"cmd /c {command}");
            return FinishSuccess(FormatResult(result));
        }
        catch (OperationCanceledException)
        {
            return FinishError("Error: command timed out after 60s");
        }
        catch (MxcException ex)
        {
            return FinishError(FormatMxcError("executing command", ex));
        }
        catch (Exception ex)
        {
            return FinishError(FormatSandboxError("executing command", ex));
        }
    }

    /// <summary>Creates the list of AIFunctions for MAF registration.</summary>
    public static IList<AIFunction> CreateAll() =>
    [
        AIFunctionFactory.Create(ReadFile, ReadFileToolName),
        AIFunctionFactory.Create(WriteFile, WriteFileToolName),
        AIFunctionFactory.Create(Exec, ExecToolName),
    ];

    private static async Task<SandboxProcessResult> RunInSandboxAsync(string commandLine)
    {
        Directory.CreateDirectory(WorkspaceMemoryProvider.WorkspaceDir);

        var config = MxcSdk.BuildSandboxPayload(
            commandLine,
            BasePolicy(),
            WorkspaceMemoryProvider.WorkspaceDir,
            containerName: "DotClaw-CSharpSandbox");

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(TimeoutMs));
        return await Spawner.SpawnSandboxProcessAsync(
            config,
            new SandboxSpawnOptions { UsePty = false },
            cts.Token);
    }

    private static SandboxPolicy BasePolicy() => new()
    {
        Version = SchemaVersion,
        Filesystem = new FilesystemPolicy
        {
            ReadonlyPaths = [System32Dir()],
            ReadwritePaths = [DotClawDir(), WorkspaceMemoryProvider.WorkspaceDir],
        },
        Network = new NetworkPolicy { AllowOutbound = false },
        TimeoutMs = TimeoutMs,
    };

    private static string DotClawDir() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".dotclaw");

    private static string System32Dir()
    {
        var systemRoot = Environment.GetEnvironmentVariable("SystemRoot")
            ?? Environment.GetEnvironmentVariable("windir")
            ?? @"C:\Windows";
        return Path.Combine(systemRoot, "System32");
    }

    private static string? ResolveInWorkspace(string path, out string? refusal)
    {
        if (path.IndexOfAny(CmdUnsafePathChars) >= 0)
        {
            refusal = "Refused: path contains characters that cannot be safely passed to the C# sandbox file tools.";
            return null;
        }

        try
        {
            var target = Path.GetFullPath(Path.Combine(WorkspaceMemoryProvider.WorkspaceDir, path));
            var workspace = Path.GetFullPath(WorkspaceMemoryProvider.WorkspaceDir);
            if (IsWithin(target, workspace))
            {
                refusal = null;
                return target;
            }
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            refusal = $"Refused: invalid path ({ex.Message}).";
            return null;
        }

        refusal = $"Refused: path must stay inside the DotClaw workspace ({WorkspaceMemoryProvider.WorkspaceDir}).";
        return null;
    }

    private static bool IsWithin(string child, string parent)
    {
        var relative = Path.GetRelativePath(parent, child);
        return relative == "."
            || (!relative.StartsWith("..", StringComparison.Ordinal)
                && !Path.IsPathRooted(relative));
    }

    private static string FormatResult(SandboxProcessResult result)
    {
        var output = (result.Stdout + result.Stderr).Trim();
        var body = string.IsNullOrWhiteSpace(output) ? "(no output)" : output;
        var formatted = result.ExitCode == 0 ? body : $"{body}\n(exit code {result.ExitCode})";
        return TruncateToolOutput(formatted);
    }

    private static string FormatMxcError(string operation, MxcException ex)
    {
        var code = ex.Code?.ToString() ?? ex.RawCode;
        var suffix = string.IsNullOrWhiteSpace(code) ? "" : $" MXC error code: {code}.";
        return $"Error {operation} in C# sandbox: {CleanMessage(ex.Message)}.{suffix} " +
            "Ensure MXC native binaries are installed and host prep has been run.";
    }

    private static string FormatSandboxError(string operation, Exception ex) =>
        $"Error {operation} in C# sandbox: {CleanMessage(ex.Message)}. " +
        "Ensure MXC native binaries are installed and host prep has been run.";

    private static string CleanMessage(string message) =>
        message.Trim().TrimEnd('.');

    private static string Refused(string error)
    {
        AnsiConsole.MarkupLine($"  [red]✗[/] [dim]{Markup.Escape(error)}[/]\n");
        return error;
    }

    private static string FinishSuccess(string result)
    {
        AnsiConsole.MarkupLine($"  [green]✓[/] [dim]{Markup.Escape(Truncate(result, 50))}[/]\n");
        return result;
    }

    private static string FinishError(string error)
    {
        AnsiConsole.MarkupLine($"  [red]✗[/] [dim]{Markup.Escape(error)}[/]\n");
        return error;
    }

    private static string TruncateToolOutput(string output)
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
