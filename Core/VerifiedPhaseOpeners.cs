namespace XIVMit.Core;

/// <summary>
/// Real game NPC ids (Dalamud's <c>IGameObject.BaseId</c>) that mark a specific phase transition,
/// for fights where a boss's identity or targetable state reliably distinguishes the boundary.
///
/// Each entry is cross-verified against 3 independent FFLogs pulls (phase-disjoint, cross-pull-
/// stable real id, not one of FFLogs' synthetic +2,000,000 merged-entity ids) plus, where FFLogs
/// can't see the relevant state directly (targetable, HP), live confirmation. A transition
/// missing from its fight's table has no verified signal - most often because the boss keeps its
/// NPC id and only changes moveset - and falls back to <see cref="FightTracker"/>'s heuristic
/// (max-HP-of-targetable) tier instead.
///
/// Keys are the DESTINATION phase index into <c>Fight.Phases</c> (index 1 = "the signal that
/// means we've reached Phases[1]"); index 0 is never a key. These are raw array indices, not the
/// "P{n}" number a player would use - those diverge once a fight has an
/// <see cref="Phase.Intermission"/> phase (see <see cref="PlanContext.DisplayPhaseNumber"/>) - so
/// comments below name phases rather than number them.
/// </summary>
public static class VerifiedPhaseOpeners
{
    /// <summary>What state change in a <see cref="PhaseOpener"/>'s BaseId counts as arrival.
    /// FFLogs can't see live targetable/HP state directly, so anything beyond <see cref="Debut"/>
    /// is a live-testing call.</summary>
    public enum OpenerSignal
    {
        /// <summary>Alive and targetable from the moment it appears - the common case.</summary>
        Debut,
        /// <summary>Alive, targetable or not - for a boss that appears well before it's
        /// attackable (Debut would wait for targetable and fire on the wrong, later boundary).</summary>
        Exists,
        /// <summary>An already-known BaseId's untargetable -> targetable edge - for a boundary
        /// marked by an existing actor (already matched via an earlier Exists/BecomesTargetable
        /// entry for the same BaseId) becoming attackable, not a new debut.</summary>
        BecomesTargetable,
        /// <summary>Like <see cref="BecomesTargetable"/>, but also requires MaxHp to differ from
        /// what this BaseId last showed while targetable - so a mere interrupt (untargetable,
        /// then targetable again with the same pool) doesn't false-fire. For a "killed to 0, then
        /// heals to a smaller pool" transition.</summary>
        HealsAndBecomesTargetable,
    }

    /// <summary>One phase-transition signal: the BaseId to watch for, and what state change in
    /// it counts as arrival.</summary>
    public readonly record struct PhaseOpener(uint BaseId, OpenerSignal Signal = OpenerSignal.Debut)
    {
        // Lets a plain-Debut entry stay a bare uint literal (`[1] = 15713,`).
        public static implicit operator PhaseOpener(uint baseId) => new(baseId);
    }

    public static readonly IReadOnlyDictionary<string, IReadOnlyDictionary<int, PhaseOpener>> ByFight =
        new Dictionary<string, IReadOnlyDictionary<int, PhaseOpener>>(StringComparer.OrdinalIgnoreCase)
        {
            ["fru"] = new Dictionary<int, PhaseOpener>
            {
                [1] = 17823, // Usurper of Frost
                [2] = 17828, // crystal of darkness, debuting at Intermission
                [3] = 17831, // Oracle of Darkness, debuting at Gaia
                [4] = 17833, // fragment of fate, debuting at Enter the Dragon
                [5] = 17839, // Pandora
            },
            ["umad"] = new Dictionary<int, PhaseOpener>
            {
                [1] = 19506, // God Kefka
                [2] = 19509, // Chaos & Exdeath
                [3] = 18475, // Kefka Says
                [4] = 19511, // Ultima Kefka
            },
            ["dsr"] = new Dictionary<int, PhaseOpener>
            {
                [1] = 12604, // Thordan
                [2] = 12605, // Nidstinien
                [3] = 12609, // left eye, Eyes (first cast matches FFLogs' own phase mark to <0.1s)
                // Casts once earlier too (P1/Thordan cameo) but takes no damage until here -
                // BecomesTargetable guards against that cameo being live-targetable in some pull.
                [4] = new PhaseOpener(12603, OpenerSignal.BecomesTargetable), // Ser Charibert, Rewind
                [5] = 12611, // Dark Thordan
                [6] = 12612, // Double Dragons
                [7] = 12616, // Dragon-king Thordan
            },
            ["top"] = new Dictionary<int, PhaseOpener>
            {
                [1] = 15713, // Omega M/F
                [2] = new PhaseOpener(15717, OpenerSignal.Exists), // Omega appears (untargetable), P3 Transition
                [3] = new PhaseOpener(15717, OpenerSignal.BecomesTargetable), // same Omega, now targetable, Final Omega
                // Killed to 0 and heals to a smaller pool (11,125,976 -> 4,895,429 max HP,
                // confirmed live and via FFLogs) - DPS-gated rather than a fixed time, so
                // AdvanceToPhase's seek-to-authored-boundary makes this signal more accurate
                // than a timer would be regardless of a group's clear speed.
                [4] = new PhaseOpener(15717, OpenerSignal.HealsAndBecomesTargetable), // same Omega, Blue Screen
                [5] = 15720, // Dynamis Omega (targetable ~3s after boundary; first cast is ~9s early - unreliable)
                [6] = 15725, // Alpha-Omega
            },
        };
}
