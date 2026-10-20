namespace XIVMit.Core;

/// <summary>
/// NPC BaseIds that reliably mark a phase transition, each checked against 3+ FFLogs pulls (and
/// live where logs can't show targetability or HP). Transitions without an entry fall back to
/// FightTracker's top-HP heuristic.
///
/// Keys are the index into <c>Fight.Phases</c> of the phase being entered, not the displayed
/// phase number (which skips intermissions), so the comments name phases instead.
/// </summary>
public static class VerifiedPhaseOpeners
{
    public enum OpenerSignal
    {
        /// <summary>Appears alive and targetable.</summary>
        Debut,
        /// <summary>Appears alive, targetable or not. For bosses that show up before they can be hit.</summary>
        Exists,
        /// <summary>An already-present actor goes from untargetable to targetable.</summary>
        BecomesTargetable,
        /// <summary>
        /// Like <see cref="BecomesTargetable"/>, but max HP must also differ from its last
        /// targetable value, so a plain untargetable window doesn't trigger it.
        /// </summary>
        HealsAndBecomesTargetable,
    }

    public readonly record struct PhaseOpener(uint BaseId, OpenerSignal Signal = OpenerSignal.Debut)
    {
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
                [3] = 12609, // left eye, Eyes. First cast matches FFLogs' phase mark to under 0.1s
                // Ser Charibert, Rewind. He also casts once during Thordan but can't be damaged
                // then, so wait for him to become targetable.
                [4] = new PhaseOpener(12603, OpenerSignal.BecomesTargetable),
                [5] = 12611, // Dark Thordan
                [6] = 12612, // Double Dragons
                [7] = 12616, // Dragon-king Thordan
            },
            ["top"] = new Dictionary<int, PhaseOpener>
            {
                [1] = 15713, // Omega M/F
                [2] = new PhaseOpener(15717, OpenerSignal.Exists), // Omega appears untargetable, P3 Transition
                [3] = new PhaseOpener(15717, OpenerSignal.BecomesTargetable), // same Omega, now targetable, Final Omega
                // Same Omega again, Blue Screen. Killed to 0 then heals to a smaller pool
                // (11,125,976 -> 4,895,429 max HP).
                [4] = new PhaseOpener(15717, OpenerSignal.HealsAndBecomesTargetable),
                [5] = 15720, // Dynamis Omega. Targetable ~3s after the boundary; its first cast is ~9s early
                [6] = 15725, // Alpha-Omega
            },
        };
}
