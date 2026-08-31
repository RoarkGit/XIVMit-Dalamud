using System.Numerics;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Object;

namespace XIVMit.Core;

/// <summary>One observed action-effect packet, flattened to what this plugin cares about.</summary>
public readonly record struct ActionUsedEvent(uint CasterEntityId, uint ActionId, byte ActionType, bool IsLocalPlayer);

/// <summary>
/// Hooks <c>ActionEffectHandler.Receive</c>, the client's entry point for ActionEffectN server
/// packets. This is what catches actions that never show a cast bar: instant boss abilities and
/// oGCD mitigations both land here and nowhere in <see cref="IBattleChara.IsCasting"/>.
///
/// The hook targets a named FFXIVClientStructs member-function address rather than a raw byte
/// signature, so a patch that shifts code around is resolved by a ClientStructs bump rather than
/// by re-scanning a signature by hand. It is still the most patch-fragile part of the plugin:
/// every failure path here degrades to "no press detection" rather than taking the plugin down.
/// </summary>
public sealed unsafe class ActionWatcher : IDisposable
{
    private delegate void ReceiveActionEffectDelegate(
        uint casterEntityId,
        Character* casterPtr,
        Vector3* targetPos,
        ActionEffectHandler.Header* header,
        ActionEffectHandler.TargetEffects* effects,
        GameObjectId* targetIds);

    private readonly Hook<ReceiveActionEffectDelegate>? hook;
    private readonly IObjectTable objects;
    private readonly IPluginLog log;

    /// <summary>Raised on the framework thread for every action effect the client receives.</summary>
    public event Action<ActionUsedEvent>? ActionUsed;

    /// <summary>False when the hook could not be created; the UI surfaces this as degraded mode.</summary>
    public bool Active => hook?.IsEnabled ?? false;

    public string? FailureReason { get; private set; }

    public ActionWatcher(IGameInteropProvider interop, IObjectTable objects, IPluginLog log)
    {
        this.objects = objects;
        this.log = log;

        try
        {
            hook = interop.HookFromAddress<ReceiveActionEffectDelegate>(
                ActionEffectHandler.MemberFunctionPointers.Receive, Detour);
            hook.Enable();
        }
        catch (Exception ex)
        {
            FailureReason = ex.Message;
            log.Error(ex, "Failed to hook ActionEffectHandler.Receive; press detection disabled.");
        }
    }

    private void Detour(
        uint casterEntityId,
        Character* casterPtr,
        Vector3* targetPos,
        ActionEffectHandler.Header* header,
        ActionEffectHandler.TargetEffects* effects,
        GameObjectId* targetIds)
    {
        // Original first and unconditionally: this is on the game's packet path, and anything
        // that throws before it would drop a real action effect on the floor.
        hook!.Original(casterEntityId, casterPtr, targetPos, header, effects, targetIds);

        try
        {
            if (header == null) return;

            var localId = objects.LocalPlayer?.EntityId ?? 0;
            ActionUsed?.Invoke(new ActionUsedEvent(
                CasterEntityId: casterEntityId,
                ActionId: header->ActionId,
                ActionType: header->ActionType,
                IsLocalPlayer: localId != 0 && casterEntityId == localId));
        }
        catch (Exception ex)
        {
            // Never let a subscriber fault propagate back into the game's packet handler.
            log.Error(ex, "XIVMit ActionWatcher subscriber threw; ignoring.");
        }
    }

    public void Dispose()
    {
        hook?.Disable();
        hook?.Dispose();
    }
}
