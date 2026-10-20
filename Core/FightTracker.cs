using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.DutyState;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
using XIVMit.Api;
using static XIVMit.Core.VerifiedPhaseOpeners;

namespace XIVMit.Core;

/// <summary>
/// Starts and stops the clock with combat and keeps it in sync with the pull.
///
/// Phase sync comes first: a verified NPC id from <see cref="VerifiedPhaseOpeners"/> where one
/// exists, otherwise a debounced "highest max HP targetable enemy changed" heuristic. After
/// that, casts and hits are matched against boss actions that have a game action id.
/// </summary>
public sealed class FightTracker : IDisposable
{
    // Offsets up to this size are treated as drift; larger ones as a jump (skipped phase,
    // plugin loaded mid-fight).
    private const float DriftWindow = 5f;

    // Larger offsets are ignored, so one stray match can't send the clock across the fight.
    private const float MaxJump = 600f;

    // How long a new top-HP NPC must hold that spot before it counts as a transition. Adds
    // that briefly out-HP the boss don't last this long.
    private const float PhaseAdvanceDebounce = 1.5f;

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

    // Every occurrence of each action, since mechanics repeat a lot. See TrySync.
    private Dictionary<uint, List<BossAction>> byActionId = [];

    // Casts stay visible for many frames and one hit lands on many targets; these stop a single
    // event from resyncing more than once.
    private readonly Dictionary<ulong, uint> lastCastPerCaster = [];
    private (uint ActionId, float At) lastEffect;

    // Heuristic state: the NPC currently treated as the boss, every NPC that has held that
    // spot this pull, and a candidate still waiting out the debounce.
    private uint? committedTopHpId;
    private readonly HashSet<uint> seenCommittedIds = [];
    private uint? pendingId;
    private float pendingSince;

    // Last seen targetability and targetable max HP per opener NPC, for the edge-triggered
    // opener signals.
    private readonly Dictionary<ulong, bool> lastOpenerTargetable = [];
    private readonly Dictionary<ulong, uint> lastOpenerTargetableMaxHp = [];

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
        byActionId = ctx == null ? [] : ctx.Fight.BossActions
            .Where(ba => ba.GameActionId != null)
            .GroupBy(ba => ba.GameActionId!.Value)
            .ToDictionary(g => g.Key, g => g.ToList());
        ResetPullState();
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
                    // Otherwise a first cast or boss matching the end of the last pull would be
                    // skipped as a repeat.
                    ResetPullState();
                    clock.Start();
                    LastSyncDescription = "started on combat";
                }
                else
                {
                    LastSyncDescription = "press play once to auto-start here";
                    log.Info(
                        $"XIVMit didn't auto-start: territory {clientState.TerritoryType} has no " +
                        $"remembered plan matching {plan.Plan.Code}. Start the clock by hand " +
                        "once here and pulls after this one start on their own.");
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

    private bool ZoneMatchesLoadedPlan()
    {
        if (!config.RequireZoneMatchToAutoStart) return true;
        return config.PlanByTerritory.TryGetValue(clientState.TerritoryType, out var code)
            && PlanCodeInput.Same(code, plan!.Plan.Code);
    }

    /// <summary>Index of the phase after the current one, or null if there isn't one.</summary>
    private int? NextPhaseIndex()
    {
        var current = plan!.PhaseIndexAt(clock.Time);
        return current >= 0 && current + 1 < plan.Fight.Phases.Count ? current + 1 : null;
    }

    /// <summary>
    /// One pass over the object table: fire the verified opener for the next phase if it's
    /// there, and otherwise track the top-HP candidate for the heuristic.
    /// </summary>
    private void DetectPhaseAdvance()
    {
        if (plan!.Fight.Phases.Count == 0) return;

        var nextIdx = NextPhaseIndex();
        PhaseOpener? expected = nextIdx is { } n
            && ByFight.TryGetValue(plan.Fight.Id, out var openers)
            && openers.TryGetValue(n, out var opener) ? opener : null;

        IBattleNpc? bestForHeuristic = null;
        var bestHp = 0u;

        foreach (var obj in objects)
        {
            if (obj is not IBattleNpc npc) continue;
            if (!npc.IsValid()) continue;
            if (npc.IsDead) continue;

            // Not limited to targetable Combatants, so an ally NPC could serve as an opener.
            if (expected is { } eo && npc.BaseId == eo.BaseId)
            {
                // Always record state, so a later edge-triggered entry for the same NPC has
                // something to compare against.
                var wasTargetable = lastOpenerTargetable.TryGetValue(npc.GameObjectId, out var w) && w;
                uint? lastMaxHp = lastOpenerTargetableMaxHp.TryGetValue(npc.GameObjectId, out var mh) ? mh : null;
                lastOpenerTargetable[npc.GameObjectId] = npc.IsTargetable;
                if (npc.IsTargetable) lastOpenerTargetableMaxHp[npc.GameObjectId] = npc.MaxHp;

                var fires = eo.Signal switch
                {
                    OpenerSignal.Exists => true,
                    OpenerSignal.BecomesTargetable => npc.IsTargetable && !wasTargetable,
                    OpenerSignal.HealsAndBecomesTargetable => npc.IsTargetable
                        && !wasTargetable && lastMaxHp is { } last && npc.MaxHp != last,
                    _ => npc.IsTargetable,
                };

                if (fires)
                {
                    AdvanceToPhase(nextIdx!.Value, npc.BaseId);
                    return;
                }
                continue;
            }

            if (!npc.IsTargetable || npc.BattleNpcKind != BattleNpcSubKind.Combatant) continue;
            if (npc.MaxHp > bestHp) { bestHp = npc.MaxHp; bestForHeuristic = npc; }
        }

        // Don't let the heuristic guess while a verified opener is still pending.
        if (expected != null) { pendingId = null; return; }

        DetectPhaseAdvanceHeuristic(bestForHeuristic);
    }

    /// <param name="bossId">
    /// Set when a verified opener fired, so the heuristic doesn't treat that boss as new and
    /// advance a second time.
    /// </param>
    private void AdvanceToPhase(int phaseIdx, uint? bossId = null)
    {
        var phase = plan!.Fight.Phases[phaseIdx];
        clock.SeekTo(phase.StartTime);
        LastSyncDescription = $"phase advance ({phase.Name})";
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
        if (id == committedTopHpId) { pendingId = null; return; }

        if (pendingId != id)
        {
            pendingId = id;
            pendingSince = clock.Time;
            return;
        }
        if (clock.Time - pendingSince < PhaseAdvanceDebounce) return;

        var wasFirstSighting = committedTopHpId == null;
        committedTopHpId = id;
        pendingId = null;
        if (!seenCommittedIds.Add(id)) return; // already had its turn this pull
        if (wasFirstSighting) return; // the pull's opening boss

        if (NextPhaseIndex() is { } next) AdvanceToPhase(next);
    }

    /// <summary>
    /// Looks for a recognized cast in progress. Uses the live cast progress rather than the
    /// authored cast time, so the sync lands on the real hit time.
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

            var remaining = Math.Max(0f, npc.TotalCastTime - npc.CurrentCastTime);
            TrySync(candidates, remaining, "cast");
            return;
        }
    }

    private void OnActionUsed(ActionUsedEvent ev)
    {
        if (plan == null) return;
        if (clock.State != ClockState.Running || !config.SyncFromCasts) return;

        if (lastEffect.ActionId == ev.ActionId && clock.Time - lastEffect.At < 1.5f) return;

        if (!byActionId.TryGetValue(ev.ActionId, out var candidates)) return;
        lastEffect = (ev.ActionId, clock.Time);

        TrySync(candidates, 0f, "hit");
    }

    /// <summary>
    /// Syncs to whichever occurrence of the action is closest to the current clock. Always
    /// taking the first one breaks repeated mechanics: UMAD's Thunder III lands five times.
    /// </summary>
    /// <param name="leadTime">Seconds until the hit lands (remaining cast time, or 0 for a hit).</param>
    private void TrySync(List<BossAction> candidates, float leadTime, string kind)
    {
        var best = candidates.MinBy(ba => Math.Abs(ba.Time - leadTime - clock.Time));
        if (best == null) return;

        var expected = best.Time - leadTime;
        var delta = expected - clock.Time;
        var source = $"{kind} {best.Name}";

        if (Math.Abs(delta) < 0.15f) return;

        if (Math.Abs(delta) > MaxJump)
        {
            log.Debug($"XIVMit ignored out-of-range sync: {source} delta {delta:0.0}s");
            return;
        }

        clock.SeekTo(expected);
        var prefix = Math.Abs(delta) <= DriftWindow ? "" : "jumped to ";
        LastSyncDescription = $"{prefix}{source} ({delta:+0.0;-0.0}s)";
    }

    private void OnDutyEnded(IDutyStateEventArgs args)
    {
        if (config.AutoStopOnCombatEnd) clock.Stop();
    }
}
