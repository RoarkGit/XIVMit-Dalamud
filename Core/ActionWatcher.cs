using System.Numerics;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Object;

namespace XIVMit.Core;

public readonly record struct ActionUsedEvent(uint CasterEntityId, uint ActionId, byte ActionType);

/// <summary>
/// Hooks ActionEffectHandler.Receive to see boss actions that never show a cast bar. If the hook
/// fails (e.g. after a patch) only hit-based sync is lost; cast-bar sync keeps working.
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
    private readonly IPluginLog log;

    public event Action<ActionUsedEvent>? ActionUsed;

    public bool Active => hook?.IsEnabled ?? false;

    public string? FailureReason { get; private set; }

    public ActionWatcher(IGameInteropProvider interop, IPluginLog log)
    {
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
            log.Error(ex, "Failed to hook ActionEffectHandler.Receive; hit-based sync disabled.");
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
        // Call the original first so nothing below can drop a real packet.
        hook!.Original(casterEntityId, casterPtr, targetPos, header, effects, targetIds);

        try
        {
            if (header == null) return;
            ActionUsed?.Invoke(new ActionUsedEvent(casterEntityId, header->ActionId, header->ActionType));
        }
        catch (Exception ex)
        {
            log.Error(ex, "XIVMit ActionWatcher subscriber threw; ignoring.");
        }
    }

    public void Dispose()
    {
        hook?.Disable();
        hook?.Dispose();
    }
}
