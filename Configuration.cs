using Dalamud.Configuration;
using XIVMit.Core;

namespace XIVMit;

[Serializable]
public sealed class Configuration : IPluginConfiguration
{
    private const int MaxRecent = 12;

    public int Version { get; set; } = 1;

    /// <summary>Last plan loaded, reloaded on startup.</summary>
    public string PlanCode { get; set; } = "";

    /// <summary>Which roster row is "me". Auto-picked when unset.</summary>
    public string? LocalPlayerId { get; set; }

    public bool AutoStartOnCombat { get; set; } = true;

    /// <summary>
    /// Only auto-start if the loaded plan has been used in this territory before (per
    /// <see cref="PlanByTerritory"/>), so combat in unrelated content doesn't start the clock.
    /// </summary>
    public bool RequireZoneMatchToAutoStart { get; set; } = true;

    public bool AutoStopOnCombatEnd { get; set; } = true;
    public bool SyncFromCasts { get; set; } = true;

    /// <summary>How far ahead the timeline looks, in seconds.</summary>
    public float LookaheadSeconds { get; set; } = 45f;

    /// <summary>How long a mitigation stays listed after its time.</summary>
    public float GraceSeconds { get; set; } = 5f;

    public bool LockWindow { get; set; }
    public bool OverlayMode { get; set; }
    public bool ShowPhaseButtons { get; set; } = true;

    /// <summary>Most recent first.</summary>
    public List<RecentPlan> RecentPlans { get; set; } = [];

    /// <summary>Last plan used in each duty, keyed by territory id.</summary>
    public Dictionary<uint, string> PlanByTerritory { get; set; } = [];

    public bool PromptToLoadOnZoneIn { get; set; } = true;
    public bool AutoOpenInSavedZones { get; set; } = true;

    public void RememberPlan(string code, string? title, string fightLabel)
    {
        RecentPlans.RemoveAll(r => PlanCodeInput.Same(r.Code, code));
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

    public void ForgetZones()
    {
        PlanByTerritory.Clear();
        Save();
    }

    public void Save() => Plugin.PluginInterface.SavePluginConfig(this);
}

[Serializable]
public sealed class RecentPlan
{
    public string Code { get; set; } = "";
    public string? Title { get; set; }
    public string Fight { get; set; } = "";
    public DateTimeOffset LastLoaded { get; set; }

    public string Label => Title is { Length: > 0 } t ? t : Code;
}
