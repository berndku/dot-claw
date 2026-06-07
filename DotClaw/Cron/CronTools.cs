namespace DotClaw.Cron;

using System.ComponentModel;
using DotClaw.Runtime;
using Microsoft.Extensions.AI;

/// <summary>
/// Route-bound cron tools exposed to the LLM: <c>cron_add</c>, <c>cron_list</c>, <c>cron_remove</c>.
/// An instance is created per user turn and carries the current <see cref="Route"/>, so a scheduled
/// reminder knows which chat to deliver itself to. These are only wired for <see cref="TurnSource.User"/>
/// turns (cron-triggered turns deliberately cannot schedule more crons — anti-recursion).
/// </summary>
public sealed class CronTools
{
    private readonly CronService _cron;
    private readonly Route _route;

    public CronTools(CronService cron, Route route)
    {
        _cron = cron;
        _route = route;
    }

    [Description("Schedule a proactive reminder to deliver to the user later. " +
                 "Use schedule 'in:<dur>' for a one-shot (e.g. 'in:1m', 'in:30s', 'in:2h') or " +
                 "'every:<dur>' for a recurring reminder (e.g. 'every:30m'). Units: s, m, h, d. " +
                 "The 'topic' is what to remind about; you will phrase the actual message in your own voice when it fires.")]
    public async Task<string> CronAdd(
        [Description("When to fire: 'in:1m' (one-shot) or 'every:30m' (recurring).")] string schedule,
        [Description("What to remind the user about, e.g. 'stretch your legs'.")] string topic)
    {
        if (!CronSchedule.TryParse(schedule, out var parsed, out var error))
            return $"Could not schedule that: {error}";

        var job = await _cron.AddAsync(parsed, topic, _route);
        var when = parsed.Kind == CronKind.Once
            ? $"once at {job.NextRunAt:HH:mm:ss} UTC"
            : $"every {FormatDuration(parsed.Interval)}";
        return $"Reminder scheduled ({when}). id={job.Id}";
    }

    [Description("List the user's currently scheduled reminders (id, schedule, topic, next run time).")]
    public async Task<string> CronList()
    {
        var jobs = await _cron.ListAsync(_route);
        if (jobs.Count == 0) return "No reminders are scheduled.";
        return string.Join("\n", jobs.Select(j =>
            $"- id={j.Id} [{j.ScheduleRaw}] \"{j.Prompt}\" next={j.NextRunAt:HH:mm:ss} UTC"));
    }

    [Description("Remove/cancel a scheduled reminder by its id (from cron_list).")]
    public async Task<string> CronRemove(
        [Description("The id of the reminder to remove.")] string id)
    {
        var ok = await _cron.RemoveAsync(id);
        return ok ? $"Reminder {id} removed." : $"No reminder found with id {id}.";
    }

    public IEnumerable<AITool> AsTools() =>
    [
        AIFunctionFactory.Create(CronAdd, "cron_add"),
        AIFunctionFactory.Create(CronList, "cron_list"),
        AIFunctionFactory.Create(CronRemove, "cron_remove"),
    ];

    private static string FormatDuration(TimeSpan t) =>
        t.TotalHours >= 1 ? $"{t.TotalHours:0.#}h" :
        t.TotalMinutes >= 1 ? $"{t.TotalMinutes:0.#}m" :
        $"{t.TotalSeconds:0}s";
}
