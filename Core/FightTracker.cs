using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.DutyState;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
using XIVMit.Api;

namespace XIVMit.Core;

/// <summary>
/// Drives the clock off live game state - starts it on combat, stops it on wipe/clear, resyncs
/// it against what's actually happening in the pull.
///
/// Two sync tiers, tried in order. First, phase advance from boss identity
/// (<see cref="DetectPhaseAdvance"/>) - exact where <see cref="VerifiedPhaseOpeners"/> has a
/// verified id for the transition (a debut, or a targetable-state change - see
/// <see cref="VerifiedPhaseOpeners.OpenerSignal"/>), otherwise a debounced heuristic: whichever
/// targetable enemy carries the most max HP, once that changes to something new this pull.
/// Second, cast/hit matching against authored <see cref="BossAction.GameActionId"/>s
/// (<see cref="PollCasts"/>, <see cref="OnActionUsed"/>) - only covers whatever fraction of the
/// fight's boss actions actually have an id authored.
///
/// There's a third tier that doesn't exist: crossing an authored HP percentage, for a same-actor
/// transformation neither of the above can resolve. Would need a per-phase threshold authored
/// per fight, and nothing here derives that yet.
/// </summary>
public sealed class FightTracker : IDisposable
{
    // Clock can be off by this much and it still counts as drift - nudge it, don't treat it as a
    // phase jump. Padded well past normal kill-speed variance within a phase.
    private const float DriftWindow = 5f;

    // Past DriftWindow a recognized boss action counts as a jump instead (skipped phase, plugin
    // loaded mid-fight). Capped so one stray match can't send the clock flying across the fight.
    private const float MaxJump = 600f;

    private readonly ICondition condition;
    private readonly IObjectTable objects;
    private readonly IDutyState dutyState;
    private readonly IClientState clientState;
    private readonly IPluginLog log;
    private readonly ActionWatcher actions;
    private readonly TimelineClock clock;
    private readonly Configuration config;

    private PlanContext? plan;
    private bool wasInCombat;

    // Boss actions keyed by game action id - maps to every occurrence, not just the first.
    // Repeated mechanics are the norm here (52% of UMAD's boss actions share a name with another
    // one), so collapsing to a single entry would resync the clock to the wrong occurrence half
    // the time. See TrySync.
    private Dictionary<uint, List<BossAction>> byActionId = [];

    // A cast stays on the object table for several frames, and one action effect fans out across
    // several targets - without this both would resync repeatedly for the same real event.
    private readonly Dictionary<ulong, uint> lastCastPerCaster = [];
    private (uint ActionId, float At) lastEffect;

    // Tier 1 bookkeeping. committedTopHpId is whichever NPC id is "locked in" as the current
    // boss, set once PhaseAdvanceDebounce elapses; seenCommittedIds is everything that's held
    // that title this pull. pendingId/pendingSince track a candidate that hasn't held it long
    // enough to trust yet - see DetectPhaseAdvance. All of it resets every pull.
    private uint? committedTopHpId;
    private readonly HashSet<uint> seenCommittedIds = [];
    private uint? pendingId;
    private float pendingSince;

    // Per GameObjectId, for whatever NPC last matched an opener: was it targetable last time we
    // checked (BecomesTargetable needs the edge, not the level), and what was its max HP while
    // targetable (HealsAndBecomesTargetable needs to tell a real kill-and-heal apart from just an
    // interrupt). Clears with everything else per pull.
    private readonly Dictionary<ulong, bool> lastOpenerTargetable = [];
    private readonly Dictionary<ulong, uint> lastOpenerTargetableMaxHp = [];

    // How long a new top-HP NPC has to hold the title before it counts as a real transition. A
    // real one holds it for the rest of the phase; an add that briefly outHPs the boss doesn't
    // survive this long.
    private const float PhaseAdvanceDebounce = 1.5f;

    public string? LastSyncDescription { get; private set; }
    public bool HookActive => actions.Active;
    public string? HookFailure => actions.FailureReason;

    public FightTracker(
        ICondition condition,
        IObjectTable objects,
        IDutyState dutyState,
        IClientState clientState,
        IPluginLog log,
        ActionWatcher actions,
        TimelineClock clock,
        Configuration config)
    {
        this.condition = condition;
        this.objects = objects;
        this.dutyState = dutyState;
        this.clientState = clientState;
        this.log = log;
        this.actions = actions;
        this.clock = clock;
        this.config = config;

        actions.ActionUsed += OnActionUsed;
        dutyState.DutyWiped += OnDutyEnded;
        dutyState.DutyCompleted += OnDutyEnded;
    }

    public void Dispose()
    {
        actions.ActionUsed -= OnActionUsed;
        dutyState.DutyWiped -= OnDutyEnded;
        dutyState.DutyCompleted -= OnDutyEnded;
    }

    public void SetPlan(PlanContext? ctx)
    {
        plan = ctx;
        byActionId = [];
        ResetPullState();
        if (ctx == null) return;

        foreach (var ba in ctx.Fight.BossActions)
        {
            if (ba.GameActionId is { } id) Add(byActionId, id, ba);
        }

        static void Add(Dictionary<uint, List<BossAction>> map, uint key, BossAction ba)
        {
            if (!map.TryGetValue(key, out var list)) map[key] = list = [];
            list.Add(ba);
        }
    }

    private void ResetPullState()
    {
        lastCastPerCaster.Clear();
        lastEffect = default;
        committedTopHpId = null;
        seenCommittedIds.Clear();
        pendingId = null;
        lastOpenerTargetable.Clear();
        lastOpenerTargetableMaxHp.Clear();
    }

    public void Update()
    {
        if (plan == null) return;

        var inCombat = condition[ConditionFlag.InCombat];
        if (inCombat != wasInCombat)
        {
            wasInCombat = inCombat;
            if (inCombat && config.AutoStartOnCombat && clock.State == ClockState.Stopped)
            {
                if (ZoneMatchesLoadedPlan())
                {
                    // Without this, a pull whose first cast (or first boss) happens to match
                    // whatever the last pull ended on gets skipped as a repeat.
                    ResetPullState();
                    clock.Start();
                    LastSyncDescription = "started on combat";
                }
                else
                {
                    LastSyncDescription = "combat started, but zone doesn't match this plan - not auto-starting";
                }
            }
            else if (!inCombat && config.AutoStopOnCombatEnd)
            {
                clock.Stop();
            }
        }

        if (clock.State != ClockState.Running || !config.SyncFromCasts) return;

        DetectPhaseAdvance();
        PollCasts();
    }

    /// <summary>
    /// Has the loaded plan been used in this territory before? Checks
    /// <see cref="Configuration.PlanByTerritory"/> - same record the zone-in prompt reads and
    /// writes, see Plugin.OnPlanLoaded. Always true if
    /// <see cref="Configuration.RequireZoneMatchToAutoStart"/> is off.
    /// </summary>
    private bool ZoneMatchesLoadedPlan()
    {
        if (!config.RequireZoneMatchToAutoStart) return true;
        return config.PlanByTerritory.TryGetValue(clientState.TerritoryType, out var code)
            && string.Equals(code, plan!.Plan.Code, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Tier 1. One pass over the object table doing double duty: check the expected
    /// <see cref="VerifiedPhaseOpeners"/> entry first (trusted the instant it fires, no
    /// debounce, it's already cross-pull verified), and track the heuristic's top-HP candidate
    /// as a fallback for whatever transition doesn't have one - usually a boss that keeps its
    /// NPC id and just changes moveset (UMAD's Kefka -> God Kefka).
    /// </summary>
    private void DetectPhaseAdvance()
    {
        if (plan!.Fight.Phases.Count == 0) return;

        var currentIdx = plan.PhaseIndexAt(clock.Time);
        var nextIdx = currentIdx + 1;
        var haveNextPhase = currentIdx >= 0 && nextIdx < plan.Fight.Phases.Count;

        var openers = haveNextPhase && VerifiedPhaseOpeners.ByFight.TryGetValue(plan.Fight.Id, out var m)
            ? m : null;
        var expected = haveNextPhase && openers != null && openers.TryGetValue(nextIdx, out var opener)
            ? opener : (VerifiedPhaseOpeners.PhaseOpener?)null;

        IBattleNpc? bestForHeuristic = null;
        var bestHp = 0u;

        foreach (var obj in objects)
        {
            if (obj is not IBattleNpc npc) continue;
            if (!npc.IsValid()) continue;
            if (npc.IsDead) continue;

            // Checked against every live NPC, not just Combatant/targetable ones - a future ally
            // opener (DSR's Alphinaud/Haurchefant are real candidates) would show up as
            // NpcPartyMember, and this would silently never reach it otherwise.
            if (expected is { } eo && npc.BaseId == eo.BaseId)
            {
                // Recorded either way, fired or not - so if a later BecomesTargetable or
                // HealsAndBecomesTargetable entry for this same BaseId comes up once nextIdx
                // moves past this one, it has an honest "was this targetable, and at what HP,
                // last time" to compare against.
                var wasTargetable = lastOpenerTargetable.TryGetValue(npc.GameObjectId, out var w) && w;
                var lastTargetableMaxHp = lastOpenerTargetableMaxHp.TryGetValue(npc.GameObjectId, out var mh)
                    ? mh : (uint?)null;
                lastOpenerTargetable[npc.GameObjectId] = npc.IsTargetable;
                if (npc.IsTargetable) lastOpenerTargetableMaxHp[npc.GameObjectId] = npc.MaxHp;

                var fires = eo.Signal switch
                {
                    VerifiedPhaseOpeners.OpenerSignal.Exists => true,
                    VerifiedPhaseOpeners.OpenerSignal.BecomesTargetable => npc.IsTargetable && !wasTargetable,
                    VerifiedPhaseOpeners.OpenerSignal.HealsAndBecomesTargetable => npc.IsTargetable
                        && !wasTargetable && lastTargetableMaxHp is { } last && npc.MaxHp != last,
                    _ => npc.IsTargetable, // Debut
                };

                if (fires)
                {
                    AdvanceToPhase(nextIdx, $"phase advance ({plan.Fight.Phases[nextIdx].Name})", npc.BaseId);
                    return;
                }
                continue; // matched the id, hasn't fired - not a heuristic candidate either way
            }

            // Heuristic candidates are targetable Combatants only - let allies, pets, or
            // untargetable stuff in and "highest max HP enemy" stops meaning anything.
            if (!npc.IsTargetable || npc.BattleNpcKind != BattleNpcSubKind.Combatant) continue;
            if (npc.MaxHp > bestHp) { bestHp = npc.MaxHp; bestForHeuristic = npc; }
        }

        // A verified opener's expected here and just hasn't fired yet - don't let the heuristic
        // guess in its place, that's trading an exact signal for a noisy one for nothing.
        if (expected != null) { pendingId = null; return; }

        DetectPhaseAdvanceHeuristic(bestForHeuristic);
    }

    /// <param name="bossId">
    /// The boss id, if this came from the verified-id path (the heuristic path already updated
    /// its own bookkeeping, so passes null). Keeping this in sync either way stops the heuristic
    /// from "rediscovering" the boss a verified id just confirmed and firing a second, redundant
    /// advance.
    /// </param>
    private void AdvanceToPhase(int phaseIdx, string reason, uint? bossId = null)
    {
        clock.SeekTo(plan!.Fight.Phases[phaseIdx].StartTime, countAsDrift: true);
        LastSyncDescription = reason;
        pendingId = null;
        if (bossId is { } id)
        {
            committedTopHpId = id;
            seenCommittedIds.Add(id);
        }
    }

    private void DetectPhaseAdvanceHeuristic(IBattleNpc? best)
    {
        if (best == null) { pendingId = null; return; }

        var id = best.BaseId;
        if (id == committedTopHpId) { pendingId = null; return; } // already the current boss

        if (pendingId != id)
        {
            // New candidate for top HP - start (or restart) the debounce rather than acting
            // right away, so an add that briefly spikes past the boss doesn't commit a false
            // transition.
            pendingId = id;
            pendingSince = clock.Time;
            return;
        }
        if (clock.Time - pendingSince < PhaseAdvanceDebounce) return;

        var wasFirstSighting = committedTopHpId == null;
        committedTopHpId = id;
        pendingId = null;
        if (!seenCommittedIds.Add(id)) return; // already held the title once this pull
        if (wasFirstSighting) return; // that's just the pull's starting boss, not a transition

        var currentIdx = plan!.PhaseIndexAt(clock.Time);
        var nextIdx = currentIdx + 1;
        if (currentIdx < 0 || nextIdx >= plan.Fight.Phases.Count) return;

        AdvanceToPhase(nextIdx, $"phase advance ({plan.Fight.Phases[nextIdx].Name})");
    }

    /// <summary>
    /// Scans for a cast in progress we recognize. Uses the game's own cast progress rather than
    /// the authored cast length, so the resync lands on the true hit time.
    /// </summary>
    private void PollCasts()
    {
        foreach (var obj in objects)
        {
            if (obj is not IBattleNpc npc) continue;
            if (!npc.IsValid()) continue;
            if (npc.BattleNpcKind != BattleNpcSubKind.Combatant) continue;
            if (!npc.IsCasting || npc.CastActionId == 0) continue;

            var actionId = npc.CastActionId;
            if (lastCastPerCaster.TryGetValue(npc.GameObjectId, out var prev) && prev == actionId)
                continue;
            lastCastPerCaster[npc.GameObjectId] = actionId;

            if (!byActionId.TryGetValue(actionId, out var candidates)) continue;

            // Remaining cast time until the hit lands.
            var remaining = Math.Max(0f, npc.TotalCastTime - npc.CurrentCastTime);
            TrySync(candidates, remaining, "cast");
            return;
        }
    }

    private void OnActionUsed(ActionUsedEvent ev)
    {
        if (plan == null) return;
        if (clock.State != ClockState.Running || !config.SyncFromCasts) return;

        // One action effect fans out per target - collapse repeats of the same id within a
        // short window, or this resyncs once per target hit instead of once per cast.
        if (lastEffect.ActionId == ev.ActionId && clock.Time - lastEffect.At < 1.5f) return;

        if (!byActionId.TryGetValue(ev.ActionId, out var candidates)) return;
        lastEffect = (ev.ActionId, clock.Time);

        // The hit's landed by the time we see this, so the clock should just read the action's
        // own time.
        TrySync(candidates, 0f, "hit");
    }

    /// <summary>
    /// Resyncs to one occurrence of a mechanic that might repeat through the fight.
    ///
    /// Picking the one nearest the current clock matters - UMAD's Thunder III lands five times,
    /// 479s to 638s, and always picking the first authored occurrence would read the second one
    /// as 58s "late" and drag the clock backward into the previous phase.
    /// </summary>
    /// <param name="candidates">Every authored occurrence of the observed mechanic.</param>
    /// <param name="leadTime">Seconds until the hit lands - remaining cast time, or 0 for a hit.</param>
    private void TrySync(List<BossAction> candidates, float leadTime, string kind)
    {
        BossAction? best = null;
        var bestDelta = 0f;
        foreach (var ba in candidates)
        {
            var delta = ba.Time - leadTime - clock.Time;
            if (best == null || Math.Abs(delta) < Math.Abs(bestDelta)) (best, bestDelta) = (ba, delta);
        }
        if (best == null) return;

        var expected = best.Time - leadTime;
        var source = $"{kind} {best.Name}";

        if (Math.Abs(bestDelta) < 0.15f) return; // close enough, leave it alone

        if (Math.Abs(bestDelta) <= DriftWindow)
        {
            clock.SeekTo(expected, countAsDrift: true);
            LastSyncDescription = $"{source} ({bestDelta:+0.0;-0.0}s)";
            return;
        }

        if (Math.Abs(bestDelta) <= MaxJump)
        {
            clock.SeekTo(expected, countAsDrift: true);
            LastSyncDescription = $"jumped to {source} ({bestDelta:+0.0;-0.0}s)";
            return;
        }

        log.Debug($"XIVMit ignored out-of-range sync: {source} delta {bestDelta:0.0}s");
    }

    private void OnDutyEnded(IDutyStateEventArgs args)
    {
        if (config.AutoStopOnCombatEnd) clock.Stop();
    }
}
