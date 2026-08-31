using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.DutyState;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
using XIVMit.Api;

namespace XIVMit.Core;

/// <summary>
/// Drives the clock from live game state: starts it on combat, stops it on wipe/clear, and
/// resyncs it from what is actually happening in the pull.
///
/// Two tiers feed the sync, tried in order:
///   1. Phase advance from boss identity (<see cref="DetectPhaseAdvance"/>). Exact where
///      verified - <see cref="VerifiedPhaseOpeners"/> holds real game NPC ids confirmed, per
///      fight, to mark exactly one phase transition (by debuting, or by an existing one changing
///      targetable state - see <see cref="VerifiedPhaseOpeners.OpenerSignal"/>) - falling back
///      to a debounced heuristic (targetable enemy carrying the most max HP changes to one not
///      yet seen this pull) for the transitions that data doesn't cover, most often a boss that
///      keeps its NPC id and only changes moveset.
///   2. Cast/hit matching against authored <see cref="BossAction.GameActionId"/>s
///      (<see cref="PollCasts"/>, <see cref="OnActionUsed"/>). Finer-grained, but only covers
///      whatever fraction of the fight's boss actions have an id authored.
///
/// A third tier - crossing an authored HP percentage, for the same same-actor transformations
/// tier 1's heuristic fallback also can't always resolve confidently - is not implemented. It
/// would need a per-phase threshold authored per fight, which nothing in this repo derives.
/// </summary>
public sealed class FightTracker : IDisposable
{
    // How far the clock may be off and still be treated as drift to be nudged away rather than
    // a phase jump. Sized to comfortably exceed normal kill-speed variance within a phase.
    private const float DriftWindow = 5f;

    // A recognised boss action further than DriftWindow from the clock is treated as a jump
    // (phase skipped, plugin loaded mid-fight). Bounded so a stray match can't fling the clock
    // across the whole fight.
    private const float MaxJump = 600f;

    private readonly ICondition condition;
    private readonly IObjectTable objects;
    private readonly IDutyState dutyState;
    private readonly IPluginLog log;
    private readonly ActionWatcher actions;
    private readonly TimelineClock clock;
    private readonly Configuration config;

    private PlanContext? plan;
    private bool wasInCombat;

    // Boss actions keyed by authored game action id. Maps to *every* occurrence, not just the
    // first: repeated mechanics are the norm rather than the exception (52% of UMAD's boss
    // actions share a name with another), and resolving one to a single entry would resync the
    // clock to the wrong occurrence - see TrySync.
    private Dictionary<uint, List<BossAction>> byActionId = [];

    // Dedupe: a cast stays visible on the object table for many frames, and an action effect can
    // arrive for the same action across several targets. Both would otherwise resync repeatedly.
    private readonly Dictionary<ulong, uint> lastCastPerCaster = [];
    private (uint ActionId, float At) lastEffect;

    // Tier 1 state. committedTopHpId is the NPC id "locked in" as the current boss identity,
    // after PhaseAdvanceDebounce; seenCommittedIds is every id that has ever been committed this
    // pull. pendingId/pendingSince track a candidate that has not yet held the top-HP spot long
    // enough to trust - see DetectPhaseAdvance. All reset per pull.
    private uint? committedTopHpId;
    private readonly HashSet<uint> seenCommittedIds = [];
    private uint? pendingId;
    private float pendingSince;

    // Per-GameObjectId, for opener-matched NPCs: previous IsTargetable (detects the
    // BecomesTargetable edge) and MaxHp last seen while targetable (lets HealsAndBecomesTargetable
    // tell a real kill/heal apart from a same-pool interrupt). Clears with the rest of pull state.
    private readonly Dictionary<ulong, bool> lastOpenerTargetable = [];
    private readonly Dictionary<ulong, uint> lastOpenerTargetableMaxHp = [];

    // How long a new top-HP identity must persist before it is trusted as a real phase
    // transition rather than an add that briefly outHPs the boss. A real transition holds the
    // title for the rest of the phase; a stray add spike does not survive this window.
    private const float PhaseAdvanceDebounce = 1.5f;

    public string? LastSyncDescription { get; private set; }
    public bool HookActive => actions.Active;
    public string? HookFailure => actions.FailureReason;

    public FightTracker(
        ICondition condition,
        IObjectTable objects,
        IDutyState dutyState,
        IPluginLog log,
        ActionWatcher actions,
        TimelineClock clock,
        Configuration config)
    {
        this.condition = condition;
        this.objects = objects;
        this.dutyState = dutyState;
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
                plan.ResetRun();
                // Per-pull dedupe: without this, a pull whose first cast (or first boss) happens
                // to match whatever the previous pull ended on would be skipped as a repeat.
                ResetPullState();
                clock.Start();
                LastSyncDescription = "started on combat";
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
    /// Tier 1: advances the clock to the next authored phase from boss identity, one object-
    /// table pass doing double duty for two signals of differing confidence. The primary signal
    /// is <see cref="VerifiedPhaseOpeners"/> - trusted immediately, no debounce, since it's
    /// already cross-pull verified. The fallback is a debounced heuristic (targetable enemy
    /// carrying the most max HP changes to one not yet seen this pull), for transitions the
    /// verified table doesn't cover - most often a boss that keeps its NPC id and only changes
    /// moveset (UMAD's Kefka -> God Kefka).
    /// </summary>
    private void DetectPhaseAdvance()
    {
        if (plan!.Fight.Phases.Count == 0 || !config.AllowPhaseJump) return;

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
            if (npc.IsDead) continue;

            // Checked against every live NPC, not restricted to Combatant/targetable - a future
            // ally opener (e.g. DSR's Alphinaud/Haurchefant) would report as NpcPartyMember, and
            // restricting this would make such an entry silently unreachable.
            if (expected is { } eo && npc.BaseId == eo.BaseId)
            {
                // Recorded regardless of whether this opener fires, so a later BecomesTargetable
                // or HealsAndBecomesTargetable entry for this same BaseId (once nextIdx moves
                // past this one) has an accurate "was it targetable, and with how much HP, last
                // time we looked" to compare against.
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
                continue; // matched the opener's BaseId but hasn't fired yet - not a heuristic candidate
            }

            // Heuristic candidate: targetable Combatants only, since broadening this to allies,
            // pets, or untargetable objects would pollute "highest max HP enemy" with irrelevant
            // values.
            if (!npc.IsTargetable || npc.BattleNpcKind != BattleNpcSubKind.Combatant) continue;
            if (npc.MaxHp > bestHp) { bestHp = npc.MaxHp; bestForHeuristic = npc; }
        }

        // A verified opener is expected for this transition but has not fired yet: don't let the
        // heuristic guess in its place, since that would trade an exact signal for a noisier one
        // for no reason.
        if (expected != null) { pendingId = null; return; }

        DetectPhaseAdvanceHeuristic(bestForHeuristic);
    }

    /// <param name="bossId">
    /// The boss id, when known (verified-id path only - the heuristic path already updated this
    /// bookkeeping itself, so passes null). Keeps the heuristic's state consistent regardless of
    /// which tier triggered the jump, so it can't independently "rediscover" the same boss and
    /// fire a second, redundant advance.
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
        if (id == committedTopHpId) { pendingId = null; return; } // steady state

        if (pendingId != id)
        {
            // A new candidate for the top-HP spot: start (or restart) the debounce clock rather
            // than acting immediately, so a brief add spike cannot commit a false transition.
            pendingId = id;
            pendingSince = clock.Time;
            return;
        }
        if (clock.Time - pendingSince < PhaseAdvanceDebounce) return;

        var wasFirstSighting = committedTopHpId == null;
        committedTopHpId = id;
        pendingId = null;
        if (!seenCommittedIds.Add(id)) return; // held the title earlier this pull already
        if (wasFirstSighting) return; // the pull's starting boss, not a transition

        var currentIdx = plan!.PhaseIndexAt(clock.Time);
        var nextIdx = currentIdx + 1;
        if (currentIdx < 0 || nextIdx >= plan.Fight.Phases.Count) return;

        AdvanceToPhase(nextIdx, $"phase advance ({plan.Fight.Phases[nextIdx].Name})");
    }

    /// <summary>
    /// Scan visible hostiles for an in-progress cast we recognise. Uses the game's own cast
    /// progress so the resync lands on the true hit time rather than the authored cast length.
    /// </summary>
    private void PollCasts()
    {
        foreach (var obj in objects)
        {
            if (obj is not IBattleNpc npc) continue;
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

        if (ev.IsLocalPlayer)
        {
            MarkPressed(ev.ActionId);
            return;
        }

        if (clock.State != ClockState.Running || !config.SyncFromCasts) return;

        // One action effect fans out per target; collapse repeats of the same id in a short window.
        if (lastEffect.ActionId == ev.ActionId && clock.Time - lastEffect.At < 1.5f) return;

        if (!byActionId.TryGetValue(ev.ActionId, out var candidates)) return;
        lastEffect = (ev.ActionId, clock.Time);

        // An effect means the hit has landed now, so the clock should read the action's own time.
        TrySync(candidates, 0f, "hit");
    }

    /// <summary>Tick off a planned mitigation the local player actually pressed.</summary>
    private void MarkPressed(uint gameActionId)
    {
        // Only while the clock runs: with it stopped, clock.Time is 0 and every press would be
        // attributed to whichever mitigation happens to sit earliest in the plan.
        if (plan == null || config.LocalPlayerId == null || clock.State != ClockState.Running) return;

        var candidates = plan.ForPlayer(config.LocalPlayerId)
            .Where(m => m.GameActionId == gameActionId && !m.Pressed)
            .ToList();
        if (candidates.Count == 0) return;

        // Attribute the press to the planned use it is nearest to in time, so pressing an
        // ability early does not consume the slot intended for a much later use.
        var best = candidates.MinBy(m => Math.Abs(m.StartTime - clock.Time))!;
        best.Pressed = true;
        best.PressedAt = clock.Time;
    }

    /// <summary>
    /// Resyncs the clock to one of a mechanic's occurrences.
    ///
    /// Which one matters: a mechanic repeats through a fight (UMAD's Thunder III lands five
    /// times, 479s to 638s), so the occurrence is chosen as the one nearest the current clock
    /// rather than the first authored. Picking the first would read the second Thunder III as
    /// being 58s "late" and drag the clock backwards into the previous phase.
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

        if (Math.Abs(bestDelta) < 0.15f) return; // already within noise; leave the clock alone

        if (Math.Abs(bestDelta) <= DriftWindow)
        {
            clock.SeekTo(expected, countAsDrift: true);
            LastSyncDescription = $"{source} ({bestDelta:+0.0;-0.0}s)";
            return;
        }

        if (config.AllowPhaseJump && Math.Abs(bestDelta) <= MaxJump)
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
