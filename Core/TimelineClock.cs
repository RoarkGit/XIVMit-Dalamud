namespace XIVMit.Core;

public enum ClockState { Stopped, Running, Paused }

/// <summary>
/// The fight clock. Advances off <see cref="Dalamud.Plugin.Services.IFramework"/> delta rather
/// than wall time so it stays in step with the game (and stalls with it during a loading screen
/// or a hitch) instead of silently running ahead.
/// </summary>
public sealed class TimelineClock
{
    public ClockState State { get; private set; } = ClockState.Stopped;

    /// <summary>
    /// Seconds since pull, in the fight's own absolute reference frame - resyncs correct this, so
    /// it is the value everything that needs to know "where are we in the authored plan" reads:
    /// phase lookups, cast-sync deltas, press attribution, mit filtering.
    /// </summary>
    public float Time { get; private set; }

    /// <summary>
    /// Seconds since combat actually started (or play was pressed), untouched by any resync -
    /// literally what a stopwatch would read. Nothing computes against this directly; it exists
    /// so a resync's correction is recoverable as <c>Time - CombatTime</c> rather than lost the
    /// moment it is folded into <see cref="Time"/>, and so a genuine "how long has this pull
    /// really been running" reading is always available uncorrupted by sync corrections.
    /// </summary>
    public float CombatTime { get; private set; }

    /// <summary>Total correction applied by resyncs this run, for display/diagnostics.</summary>
    public float TotalDrift { get; private set; }

    public event Action<float, float>? Resynced; // (from, to)

    public void Start(float at = 0f)
    {
        Time = at;
        CombatTime = at;
        TotalDrift = 0f;
        State = ClockState.Running;
    }

    public void Pause() { if (State == ClockState.Running) State = ClockState.Paused; }
    public void Resume() { if (State == ClockState.Paused) State = ClockState.Running; }

    public void Stop()
    {
        State = ClockState.Stopped;
        Time = 0f;
        CombatTime = 0f;
        TotalDrift = 0f;
    }

    public void Toggle()
    {
        switch (State)
        {
            case ClockState.Running: Pause(); break;
            case ClockState.Paused: Resume(); break;
            case ClockState.Stopped: Start(); break;
        }
    }

    public void Update(float deltaSeconds)
    {
        if (State != ClockState.Running) return;
        Time += deltaSeconds;
        CombatTime += deltaSeconds;
    }

    /// <summary>
    /// Jump the clock to an absolute time. Used both by manual phase-skip and by the sync
    /// engine. Starts the clock if it was stopped, so a mid-fight plugin load can catch up.
    /// </summary>
    public void SeekTo(float t, bool countAsDrift = false)
    {
        var from = Time;
        Time = Math.Max(0f, t);
        if (countAsDrift) TotalDrift += Time - from;
        if (State == ClockState.Stopped) State = ClockState.Running;
        Resynced?.Invoke(from, Time);
    }

    public void Nudge(float seconds) => SeekTo(Time + seconds);

    /// <summary>
    /// Moves the clock without touching its run state, for manual scrubbing. Distinct from
    /// <see cref="SeekTo"/>, which starts a stopped clock so a mid-fight resync can catch up:
    /// scrolling the timeline to look ahead should not quietly start it ticking.
    /// </summary>
    public void Scrub(float seconds)
    {
        var from = Time;
        Time = Math.Max(0f, Time + seconds);
        Resynced?.Invoke(from, Time);
    }

    public static string Format(float t)
    {
        if (t < 0) return "-" + Format(-t);
        var total = (int)t;
        return $"{total / 60:0}:{total % 60:00}";
    }
}
