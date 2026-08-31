using Dalamud.Configuration;

namespace XIVMit;

[Serializable]
public sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;

    /// <summary>Last plan code loaded, reloaded on next startup.</summary>
    public string PlanCode { get; set; } = "";

    /// <summary>
    /// Which roster row is "me". Auto-picked by job on load when unset, but a plan can hold two
    /// players of the same job, so it stays user-overridable.
    /// </summary>
    public string? LocalPlayerId { get; set; }

    public bool AutoStartOnCombat { get; set; } = true;
    public bool AutoStopOnCombatEnd { get; set; } = true;
    public bool SyncFromCasts { get; set; } = true;
    public bool AllowPhaseJump { get; set; } = true;

    /// <summary>How far ahead the upcoming list looks, in seconds.</summary>
    public float LookaheadSeconds { get; set; } = 45f;

    /// <summary>Seconds a mitigation stays listed after its planned time before dropping off.</summary>
    public float GraceSeconds { get; set; } = 5f;

    public bool LockWindow { get; set; }

    /// <summary>
    /// Strips the window down to the clock and the timeline, dropping the chrome that is only
    /// touched between sessions rather than between pulls, and renders on a transparent window
    /// so it reads as a game overlay rather than a panel.
    /// </summary>
    public bool OverlayMode { get; set; }

    /// <summary>Phase-skip strip visibility. Collapsed, its space goes to the mitigations.</summary>
    public bool ShowPhaseButtons { get; set; } = true;

    /// <summary>Recently loaded plans, most recent first.</summary>
    public List<RecentPlan> RecentPlans { get; set; } = [];

    private const int MaxRecent = 12;

    /// <summary>Moves a plan to the front of the recent list, updating its cached labels.</summary>
    public void RememberPlan(string code, string? title, string fightLabel)
    {
        RecentPlans.RemoveAll(r => string.Equals(r.Code, code, StringComparison.OrdinalIgnoreCase));
        RecentPlans.Insert(0, new RecentPlan
        {
            Code = code,
            Title = title,
            Fight = fightLabel,
            LastLoaded = DateTimeOffset.UtcNow,
        });
        if (RecentPlans.Count > MaxRecent)
            RecentPlans.RemoveRange(MaxRecent, RecentPlans.Count - MaxRecent);
        Save();
    }

    public void Save() => Plugin.PluginInterface.SavePluginConfig(this);
}

[Serializable]
public sealed class RecentPlan
{
    public string Code { get; set; } = "";

    /// <summary>Plan title as of the last load; may be null for an untitled plan.</summary>
    public string? Title { get; set; }

    /// <summary>Fight short name, cached so the list renders without refetching every plan.</summary>
    public string Fight { get; set; } = "";

    public DateTimeOffset LastLoaded { get; set; }

    public string Label => Title is { Length: > 0 } t ? t : Code;
}
