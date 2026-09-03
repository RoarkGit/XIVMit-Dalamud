namespace XIVMit.Core;

/// <summary>
/// Real game NPC ids (Dalamud's <c>IGameObject.BaseId</c>) that mark a specific phase transition
/// - wherever a boss's identity or targetable state reliably distinguishes the boundary.
///
/// Every entry's cross-verified against 3+ independent FFLogs pulls: phase-disjoint, a real id
/// stable across every pull (not one of FFLogs' synthetic +2,000,000 merged-entity ids), plus
/// live confirmation wherever FFLogs can't see the relevant state directly (targetable, HP). A
/// transition missing from its fight's table just doesn't have a verified signal yet - usually
/// because the boss keeps its NPC id and only changes moveset - and falls back to
/// <see cref="FightTracker"/>'s heuristic (max-HP-of-targetable) tier instead.
///
/// Keys are the DESTINATION phase index into <c>Fight.Phases</c> (index 1 means "the signal for
/// reaching Phases[1]"); index 0 is never a key. Raw array indices, not the "P{n}" a player would
/// use - those diverge once a fight has an <see cref="Phase.Intermission"/> phase (see
/// <see cref="PlanContext.DisplayPhaseNumber"/>) - so the comments below name phases instead of
/// numbering them.
/// </summary>
public static class VerifiedPhaseOpeners
{
    /// <summary>What state change in a <see cref="PhaseOpener"/>'s BaseId counts as arrival.
    /// FFLogs can't see live targetable/HP state directly, so anything past <see cref="Debut"/>
    /// is a live-testing call, not something derived from log data alone.</summary>
    public enum OpenerSignal
    {
        /// <summary>Alive and targetable the moment it appears - the common case.</summary>
        Debut,
        /// <summary>Alive, targetable or not - for a boss that's on the field well before it's
        /// attackable. Debut would just sit there waiting for targetable and fire on the wrong,
        /// later boundary instead.</summary>
        Exists,
        /// <summary>An already-known BaseId flips from untargetable to targetable - the edge,
        /// not the level. For a boundary where an existing actor (already matched via an earlier
        /// Exists or BecomesTargetable entry for the same BaseId) becomes attackable, rather
        /// than a new one debuting.</summary>
        BecomesTargetable,
        /// <summary>Same as <see cref="BecomesTargetable"/>, plus MaxHp has to differ from what
        /// this BaseId last showed while targetable - so a plain interrupt (untargetable, then
        /// targetable again at the same HP) can't false-fire it. For "killed to 0, heals to a
        /// smaller pool" transitions specifically.</summary>
        HealsAndBecomesTargetable,
    }

    /// <summary>One phase-transition signal - the BaseId to watch, and what state change in it
    /// counts as arrival.</summary>
    public readonly record struct PhaseOpener(uint BaseId, OpenerSignal Signal = OpenerSignal.Debut)
    {
        // Lets a plain-Debut entry stay a bare uint literal (`[1] = 15713,`) instead of spelling
        // out the constructor every time.
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
                [3] = 12609, // left eye, Eyes - first cast lines up with FFLogs' own phase mark to under 0.1s
                // Casts once early too (a P1/Thordan cameo), but takes no damage until here -
                // BecomesTargetable instead of a plain debut guards against that cameo turning
                // out to be live-targetable in some pull we haven't checked.
                [4] = new PhaseOpener(12603, OpenerSignal.BecomesTargetable), // Ser Charibert, Rewind
                [5] = 12611, // Dark Thordan
                [6] = 12612, // Double Dragons
                [7] = 12616, // Dragon-king Thordan
            },
            ["top"] = new Dictionary<int, PhaseOpener>
            {
                [1] = 15713, // Omega M/F
                [2] = new PhaseOpener(15717, OpenerSignal.Exists), // Omega appears untargetable, P3 Transition
                [3] = new PhaseOpener(15717, OpenerSignal.BecomesTargetable), // same Omega, now targetable - Final Omega
                // Killed to 0, heals to a smaller pool (11,125,976 -> 4,895,429 max HP, confirmed
                // both live and via FFLogs). DPS-gated rather than a fixed timer, which actually
                // works in our favor: AdvanceToPhase always seeks to the authored boundary
                // regardless of when the trigger fires, so this stays accurate whether a group
                // clears fast or slow.
                [4] = new PhaseOpener(15717, OpenerSignal.HealsAndBecomesTargetable), // same Omega again - Blue Screen
                [5] = 15720, // Dynamis Omega (targetable ~3s after the boundary; first cast is ~9s early, don't trust that one)
                [6] = 15725, // Alpha-Omega
            },
        };
}
