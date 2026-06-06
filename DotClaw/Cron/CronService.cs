namespace DotClaw.Cron;

using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using DotClaw.Runtime;

/// <summary>
/// Owns scheduled cron jobs, their persistence (<c>~/.dotclaw/cron.json</c>) and a single
/// self-re-arming timer that fires due jobs — a faithful port of OpenClaw's <c>armTimer</c>.
/// <para>
/// Timer model (T-b): one loop that sleeps until the next due job, clamped to
/// <see cref="MinFloor"/>..<see cref="MaxDelay"/>. It re-arms after every fire and whenever jobs
/// change (via a capacity-1 wake channel), so a brand-new <c>in:1m</c> job recomputes the wake
/// immediately. The ~60s ceiling is a watchdog that recovers from clock jumps / process suspend.
/// </para>
/// <para>
/// Concurrency (M2): when a job is due it runs <see cref="_onFire"/> on a background task — a real
/// <em>isolated</em> cron run that executes concurrently with live chat. A per-job reserve set gives
/// single-flight (no overlapping ticks of the same job); a bounded limiter caps how many jobs run at
/// once. Jobs run on their own session key, so they never collide with user history.
/// </para>
/// </summary>
public sealed class CronService
{
    private static readonly string CronPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dotclaw", "cron.json");

    // armTimer clamps: never busy-spin (floor), never sleep more than ~1 min (watchdog ceiling).
    private static readonly TimeSpan MinFloor = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MaxDelay = TimeSpan.FromSeconds(60);

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    // _gate guards _jobs + persistence (async). _sync guards the in-memory reserve/cancel sets (fast).
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _sync = new();
    private readonly HashSet<string> _running = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _cancelled = new(StringComparer.OrdinalIgnoreCase);

    private readonly SemaphoreSlim _runLimit;
    private readonly Channel<bool> _wake = Channel.CreateBounded<bool>(
        new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });

    private readonly List<CronJob> _jobs;
    private Func<CronJob, CancellationToken, Task>? _onFire;

    public CronService(int maxConcurrentRuns = 2)
    {
        _runLimit = new SemaphoreSlim(maxConcurrentRuns, maxConcurrentRuns);
        _jobs = LoadFromDisk();
        Console.WriteLine($"[cron] loaded {_jobs.Count} job(s) from {CronPath}");
    }

    /// <summary>Starts the timer loop. <paramref name="onFire"/> runs a job (builds + runs the agent, delivers).</summary>
    public void Start(Func<CronJob, CancellationToken, Task> onFire, CancellationToken ct)
    {
        _onFire = onFire;
        _ = Task.Run(() => TimerLoopAsync(ct), ct);
    }

    // ── Mutations (called from the agent's cron tools) ──────────────────────────

    public async Task<CronJob> AddAsync(CronSchedule schedule, string prompt, Route route)
    {
        var job = new CronJob
        {
            Kind = schedule.Kind,
            IntervalSeconds = (long)schedule.Interval.TotalSeconds,
            NextRunAt = DateTime.UtcNow + schedule.Interval,
            Prompt = prompt,
            Route = route,
            ScheduleRaw = schedule.Raw,
        };

        await _gate.WaitAsync();
        try
        {
            _jobs.Add(job);
            Save();
        }
        finally { _gate.Release(); }

        Wake(); // re-arm the timer so a near-term job fires on time
        return job;
    }

    public async Task<IReadOnlyList<CronJob>> ListAsync(Route? route = null)
    {
        await _gate.WaitAsync();
        try
        {
            IEnumerable<CronJob> q = _jobs.Where(j => j.Enabled);
            if (route != null) q = q.Where(j => j.Route == route);
            return q.OrderBy(j => j.NextRunAt).ToList();
        }
        finally { _gate.Release(); }
    }

    public async Task<bool> RemoveAsync(string id)
    {
        bool removed;
        await _gate.WaitAsync();
        try
        {
            removed = _jobs.RemoveAll(j => j.Id.Equals(id, StringComparison.OrdinalIgnoreCase)) > 0;
            if (removed) Save();
        }
        finally { _gate.Release(); }

        // If it is firing right now, suppress its delivery (cron_remove = disable immediately).
        lock (_sync)
        {
            if (_running.Contains(id)) _cancelled.Add(id);
        }

        Wake();
        return removed;
    }

    /// <summary>True if the job was removed while a fire was in-flight (delivery should be suppressed).</summary>
    public bool IsCancelled(string id)
    {
        lock (_sync) return _cancelled.Contains(id);
    }

    // ── Timer loop (armTimer) ───────────────────────────────────────────────────

    private async Task TimerLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var delay = ComputeDelay();
            await WaitDelayOrWakeAsync(delay, ct);
            if (ct.IsCancellationRequested) break;
            try { await FireDueAsync(ct); }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { Console.WriteLine($"[cron] fire scan error: {ex.Message}"); }
        }
    }

    private TimeSpan ComputeDelay()
    {
        var now = DateTime.UtcNow;
        DateTime? next = null;

        _gate.Wait();
        try
        {
            foreach (var j in _jobs)
            {
                if (!j.Enabled) continue;

                // A job currently running is being handled; ignore its (now-past) NextRunAt so an
                // overdue one-shot doesn't pin the timer to a 1Hz spin while it executes.
                lock (_sync) { if (_running.Contains(j.Id)) continue; }

                if (next is null || j.NextRunAt < next) next = j.NextRunAt;
            }
        }
        finally { _gate.Release(); }

        if (next is null) return MaxDelay; // nothing scheduled → idle watchdog poll

        var d = next.Value - now;
        if (d < MinFloor) d = MinFloor;   // overdue (incl. startup catch-up) → fire shortly
        if (d > MaxDelay) d = MaxDelay;   // watchdog ceiling
        return d;
    }

    private async Task WaitDelayOrWakeAsync(TimeSpan delay, CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var delayTask = Task.Delay(delay, linked.Token);
        var wakeTask = _wake.Reader.WaitToReadAsync(linked.Token).AsTask();

        var done = await Task.WhenAny(delayTask, wakeTask);
        if (done == wakeTask)
            while (_wake.Reader.TryRead(out _)) { } // coalesce signals

        linked.Cancel(); // cancel the losing task so it can't accumulate across iterations

        // Observe both so neither faults unobserved; cancellation here is expected.
        try { await Task.WhenAll(delayTask, wakeTask); }
        catch (OperationCanceledException) { }
    }

    private async Task FireDueAsync(CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var toFire = new List<CronJob>();

        await _gate.WaitAsync(ct);
        try
        {
            foreach (var j in _jobs)
            {
                if (!j.Enabled || j.NextRunAt > now) continue;

                bool already;
                lock (_sync) already = _running.Contains(j.Id);
                if (already) continue; // single-flight: this job is still running from a prior tick

                lock (_sync) _running.Add(j.Id);
                j.LastRunAt = now;

                // Single-flight is enforced by the in-memory _running set (so ComputeDelay/FireDue
                // skip it while it runs) — we deliberately do NOT persist a "disabled/reserved" flag,
                // so a crash mid-run lets the job retry on restart. For recurring jobs we advance
                // NextRunAt now and correct it (skip-missed) on completion.
                if (j.Kind == CronKind.Every) j.NextRunAt = now + j.Interval;

                toFire.Add(j);
            }

            if (toFire.Count > 0) Save();
        }
        finally { _gate.Release(); }

        foreach (var job in toFire)
            _ = RunOneAsync(job, ct);
    }

    private async Task RunOneAsync(CronJob job, CancellationToken ct)
    {
        var acquired = false;
        try
        {
            await _runLimit.WaitAsync(ct); // bound concurrent cron runs
            acquired = true;

            bool cancelled;
            lock (_sync) cancelled = _cancelled.Contains(job.Id);

            if (!cancelled && _onFire is not null)
                await _onFire(job, ct);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Console.WriteLine($"[cron] job {job.Id} failed: {ex.Message}");
        }
        finally
        {
            if (acquired) _runLimit.Release();

            await _gate.WaitAsync(CancellationToken.None);
            try
            {
                if (job.Kind == CronKind.Once)
                {
                    _jobs.RemoveAll(j => j.Id == job.Id);
                }
                else
                {
                    var jj = _jobs.FirstOrDefault(j => j.Id == job.Id);
                    if (jj is not null) jj.NextRunAt = DateTime.UtcNow + jj.Interval; // skip missed ticks
                }
                Save();
            }
            finally { _gate.Release(); }

            lock (_sync)
            {
                _running.Remove(job.Id);   // always cleared, even if we never got a run slot
                _cancelled.Remove(job.Id);
            }

            Wake(); // re-arm for the next due job
        }
    }

    // ── Persistence ─────────────────────────────────────────────────────────────

    private static List<CronJob> LoadFromDisk()
    {
        try
        {
            if (!File.Exists(CronPath)) return new List<CronJob>();
            var json = File.ReadAllText(CronPath);
            return JsonSerializer.Deserialize<List<CronJob>>(json, JsonOpts) ?? new List<CronJob>();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[cron] failed to load {CronPath}: {ex.Message}");
            return new List<CronJob>();
        }
    }

    /// <summary>Atomically persists the job list. Caller must hold <see cref="_gate"/>.</summary>
    private void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(CronPath)!);
        var tmp = CronPath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(_jobs, JsonOpts));
        File.Move(tmp, CronPath, overwrite: true);
    }

    private void Wake() => _wake.Writer.TryWrite(true);
}
