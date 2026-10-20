namespace XIVMit.Core;

public enum ClockState { Stopped, Running, Paused }

/// <summary>
/// The fight clock. Advances on framework delta rather than wall time, so it stalls along with
/// the game during loading screens and hitches.
/// </summary>
public sealed class TimelineClock
{
    public ClockState State { get; private set; } = ClockState.Stopped;

    /// <summary>Seconds since pull in the fight's own timeline. Resyncs move this.</summary>
    public float Time { get; private set; }

    /// <summary>Seconds since combat actually started. Never resynced.</summary>
    public float CombatTime { get; private set; }

    /// <summary>A pull starting, manually or on combat. Not raised by <see cref="SeekTo"/>.</summary>
    public event Action? Started;

    public void Start()
    {
        Time = 0f;
        CombatTime = 0f;
        State = ClockState.Running;
        Started?.Invoke();
    }

    public void Pause() { if (State == ClockState.Running) State = ClockState.Paused; }
    public void Resume() { if (State == ClockState.Paused) State = ClockState.Running; }

    public void Stop()
    {
        State = ClockState.Stopped;
        Time = 0f;
        CombatTime = 0f;
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

    /// <summary>Jumps to an absolute time, starting the clock if needed (mid-fight catch-up).</summary>
    public void SeekTo(float t)
    {
        Time = Math.Max(0f, t);
        if (State == ClockState.Stopped) State = ClockState.Running;
    }

    public static string Format(float t)
    {
        if (t < 0) return "-" + Format(-t);
        var total = (int)t;
        return $"{total / 60:0}:{total % 60:00}";
    }
}
