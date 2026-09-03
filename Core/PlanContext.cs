using XIVMit.Api;

namespace XIVMit.Core;

/// <summary>One assignment joined to the ability it refers to, with level-adjusted duration.</summary>
public sealed class PlannedMit
{
    public required Assignment Assignment { get; init; }
    public required Ability Ability { get; init; }
    public required Player Player { get; init; }

    public float StartTime => Assignment.StartTime;
    public float Duration { get; init; }
    public float EndTime => StartTime + Duration;

    public uint? GameActionId => Ability.GameActionId;
    public string DisplayName => Assignment.Label is { Length: > 0 } l ? l : Ability.Name;
}

/// <summary>
/// A loaded plan resolved against its fight and the relevant job ability lists. Immutable after
/// construction.
/// </summary>
public sealed class PlanContext
{
    public Plan Plan { get; }
    public Fight Fight { get; }
    public IReadOnlyList<PlannedMit> Mits { get; }
    public IReadOnlyDictionary<string, Player> PlayersById { get; }

    // Absolute times where the displayed clock restarts, ascending. Empty for every fight
    // without a checkpoint, which is all of them but DSR.
    private readonly float[] checkpoints;

    private PlanContext(Plan plan, Fight fight, List<PlannedMit> mits)
    {
        Plan = plan;
        Fight = fight;
        Mits = mits;
        PlayersById = plan.Players.ToDictionary(p => p.Id, p => p);
        checkpoints = fight.Phases.Where(p => p.ClockReset).Select(p => p.StartTime)
            .OrderBy(x => x).ToArray();
    }

    /// <summary>
    /// The absolute time <paramref name="t"/>'s displayed clock is measured from - mirrors
    /// segmentOrigin() in the web client's gameUtils.ts. A checkpoint phase splits the fight into
    /// independent segments whose clock restarts at 0:00 (DSR's Thordan transition is the only
    /// case today); stored times stay absolute everywhere, so this only changes what's shown.
    /// Skip it and the plugin reads 4:00 where the plan being followed reads 1:15.
    /// </summary>
    public float SegmentOrigin(float t)
    {
        var origin = 0f;
        foreach (var c in checkpoints)
        {
            if (t < c) break;
            origin = c;
        }
        return origin;
    }

    /// <summary>Clock reading for an absolute time, as the web app would show it.</summary>
    public float DisplayTime(float t) => t - SegmentOrigin(t);

    public static PlanContext Build(Plan plan, Fight fight, IReadOnlyDictionary<string, List<Ability>> abilitiesByJob)
    {
        // Flatten every job's abilities into one id -> ability map. Ability ids are globally
        // unique (job-prefixed, e.g. "war_reprisal"), so a single map is safe.
        var abilityById = new Dictionary<string, Ability>(StringComparer.Ordinal);
        foreach (var list in abilitiesByJob.Values)
            foreach (var ab in list)
                abilityById[ab.Id] = ab;

        var playersById = plan.Players.ToDictionary(p => p.Id, p => p);

        var mits = new List<PlannedMit>();
        foreach (var a in plan.Assignments)
        {
            // An assignment can outlive the ability or player it names (plan edited, data
            // changed). Skip rather than throw: a partially-renderable plan beats none.
            if (!abilityById.TryGetValue(a.AbilityId, out var ability)) continue;
            if (!playersById.TryGetValue(a.PlayerId, out var player)) continue;

            var full = EffectiveDuration(ability, fight.MaxLevel);
            mits.Add(new PlannedMit
            {
                Assignment = a,
                Ability = ability,
                Player = player,
                // durationOverride is a manual shortening and is always <= the nominal length.
                Duration = a.DurationOverride is { } d ? Math.Min(d, full) : full,
            });
        }

        mits.Sort((x, y) => x.StartTime.CompareTo(y.StartTime));
        return new PlanContext(plan, fight, mits);
    }

    /// <summary>Mirror of effectiveDuration() in the web client's gameUtils.ts.</summary>
    public static float EffectiveDuration(Ability ab, int maxLevel) =>
        ab.DurationUpgrade is { } up && maxLevel >= up.MinLevel ? up.Duration : ab.Duration;

    /// <summary>Mits belonging to one player, in time order.</summary>
    public IEnumerable<PlannedMit> ForPlayer(string playerId) =>
        Mits.Where(m => m.Player.Id == playerId);

    /// <summary>The phase containing <paramref name="t"/>, or null when between phases.</summary>
    public Phase? PhaseAt(float t) =>
        Fight.Phases.FirstOrDefault(p => t >= p.StartTime && t < p.EndTime);

    /// <summary>Index of the phase containing <paramref name="t"/>, or -1 when between phases.</summary>
    public int PhaseIndexAt(float t) =>
        Fight.Phases.FindIndex(p => t >= p.StartTime && t < p.EndTime);

    /// <summary>
    /// The 1-based number a person would actually call <c>Fight.Phases[phaseIndex]</c> - skips
    /// short scripted transitions (<see cref="Phase.Intermission"/> - FRU's Intermission, TOP's
    /// P3 Transition) from the count. Those phases are still real everywhere else (StartTime/
    /// EndTime, sync, checkpoints); only the display number skips them, since "P6" for what's
    /// really the fight's 5th phase reads as a bug.
    ///
    /// An intermission phase returns the number of the counting phase before it - it's not
    /// really its own "phase N" to a player. A caller wanting to label an intermission
    /// specially (its bare name, no "P{n}:" prefix) checks <c>Intermission</c> directly instead
    /// of relying on this number.
    /// </summary>
    public int DisplayPhaseNumber(int phaseIndex)
    {
        var n = 0;
        for (var i = 0; i <= phaseIndex && i < Fight.Phases.Count; i++)
            if (!Fight.Phases[i].Intermission) n++;
        return Math.Max(n, 1);
    }
}
