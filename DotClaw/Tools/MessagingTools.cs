namespace DotClaw.Tools;

using System.ComponentModel;
using Microsoft.Extensions.AI;
using Spectre.Console;

/// <summary>
/// A self-contained demo tool: "send a text message to a contact on the user's behalf".
/// The send is <b>simulated</b> — it prints a confirmation and appends to a local outbox
/// file (<c>~/.dotclaw/outbox.log</c>) — so the side effect is visible and only happens
/// after the user has approved it. Pairs with the human-in-the-loop approval flow.
/// </summary>
public static class MessagingTools
{
    /// <summary>The tool name used both by the LLM and by the approval policy.</summary>
    public const string ToolName = "send_message";

    [Description("Send a text message to one of the user's contacts on their behalf. Use when the user asks you to text, message, or notify a person.")]
    public static string SendMessage(
        [Description("The name of the contact/recipient to message, e.g. 'Sarah' or 'Mom'.")] string recipient,
        [Description("The message body to send.")] string message)
    {
        AnsiConsole.MarkupLine($"  [bold magenta]⚡ send_message[/]  [dim]to=[/][white]{Markup.Escape(recipient)}[/]");

        var outbox = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".dotclaw", "outbox.log");
        Directory.CreateDirectory(Path.GetDirectoryName(outbox)!);
        var line = $"[{DateTime.UtcNow:yyyy-MM-dd HH:mm 'UTC'}] To {recipient}: {message}";
        File.AppendAllText(outbox, line + Environment.NewLine);

        var result = $"✅ Message sent to {recipient}.";
        AnsiConsole.MarkupLine($"  [green]✓[/] [dim]{Markup.Escape(result)}[/]\n");
        return result;
    }

    /// <summary>Creates the <c>send_message</c> AIFunction with the friendly snake_case name.</summary>
    public static AIFunction Create() =>
        AIFunctionFactory.Create(SendMessage, new AIFunctionFactoryOptions { Name = ToolName });
}
