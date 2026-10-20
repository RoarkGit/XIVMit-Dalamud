using XIVMit.Api;

namespace XIVMit.Core;

/// <summary>An assignment joined to its ability and player.</summary>
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

/// <summary>A loaded plan resolved against its fight and job ability lists.</summary>
public sealed class PlanContext
{
    public Plan Plan { get; }
    public Fight Fight { get; }
    public IReadOnlyList<PlannedMit> Mits { get; }
    public IReadOnlyDictionary<string, Player> PlayersById { get; }

    // Start times of phases where the displayed clock resets to 0:00 (only DSR has one).
    private readonly float[] checkpoints;

    private PlanContext(Plan plan, Fight fight, List<PlannedMit> mits, Dictionary<string, Player> playersById)
    {
        Plan = plan;
        Fight = fight;
        Mits = mits;
        PlayersById = playersById;
        checkpoints = fight.Phases.Where(p => p.ClockReset).Select(p => p.StartTime)
            .OrderBy(x => x).ToArray();
    }

    public static PlanContext Build(Plan plan, Fight fight, IReadOnlyDictionary<string, List<Ability>> abilitiesByJob)
    {
        // Ability ids are job-prefixed ("war_reprisal"), so one flat map is safe.
        var abilityById = new Dictionary<string, Ability>(StringComparer.Ordinal);
        foreach (var ab in abilitiesByJob.Values.SelectMany(list => list))
            abilityById[ab.Id] = ab;

        var playersById = plan.Players.ToDictionary(p => p.Id, p => p);

        var mits = new List<PlannedMit>();
        foreach (var a in plan.Assignments)
        {
            // Skip assignments whose ability or player no longer exists.
            if (!abilityById.TryGetValue(a.AbilityId, out var ability)) continue;
            if (!playersById.TryGetValue(a.PlayerId, out var player)) continue;

            var full = EffectiveDuration(ability, fight.MaxLevel);
            mits.Add(new PlannedMit
            {
                Assignment = a,
                Ability = ability,
                Player = player,
                Duration = a.DurationOverride is { } d ? Math.Min(d, full) : full,
            });
        }

        mits.Sort((x, y) => x.StartTime.CompareTo(y.StartTime));
        return new PlanContext(plan, fight, mits, playersById);
    }

    // Same as effectiveDuration() in the web client's gameUtils.ts.
    private static float EffectiveDuration(Ability ab, int maxLevel) =>
        ab.DurationUpgrade is { } up && maxLevel >= up.MinLevel ? up.Duration : ab.Duration;

    /// <summary>
    /// The clock reading the web app would show for an absolute time, accounting for
    /// checkpoints (segmentOrigin() in gameUtils.ts).
    /// </summary>
    public float DisplayTime(float t)
    {
        var origin = 0f;
        foreach (var c in checkpoints)
        {
            if (t < c) break;
            origin = c;
        }
        return t - origin;
    }

    public IEnumerable<PlannedMit> ForPlayer(string playerId) =>
        Mits.Where(m => m.Player.Id == playerId);

    /// <summary>Name for a player id (falling back to job, then the raw id).</summary>
    public string PlayerName(string id) =>
        PlayersById.TryGetValue(id, out var p) ? p.Name ?? p.Job : id;

    /// <summary>Index of the phase containing <paramref name="t"/>, or -1 between phases.</summary>
    public int PhaseIndexAt(float t) =>
        Fight.Phases.FindIndex(p => t >= p.StartTime && t < p.EndTime);

    public Phase? PhaseAt(float t) =>
        PhaseIndexAt(t) is var i and >= 0 ? Fight.Phases[i] : null;

    /// <summary>
    /// The 1-based phase number a player would use, not counting intermissions. An intermission
    /// gets the number of the phase before it.
    /// </summary>
    public int DisplayPhaseNumber(int phaseIndex)
    {
        var n = 0;
        for (var i = 0; i <= phaseIndex && i < Fight.Phases.Count; i++)
            if (!Fight.Phases[i].Intermission) n++;
        return Math.Max(n, 1);
    }
}
