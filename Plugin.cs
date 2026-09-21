using Dalamud.Game.Command;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;
using XIVMit.Api;
using XIVMit.Core;
using XIVMit.Windows;
using LuminaAction = Lumina.Excel.Sheets.Action;

namespace XIVMit;

public sealed class Plugin : IDalamudPlugin
{
    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;
    [PluginService] internal static IFramework Framework { get; private set; } = null!;
    [PluginService] internal static ICondition Condition { get; private set; } = null!;
    [PluginService] internal static IClientState ClientState { get; private set; } = null!;
    [PluginService] internal static IObjectTable Objects { get; private set; } = null!;
    [PluginService] internal static IDutyState DutyState { get; private set; } = null!;
    [PluginService] internal static IDataManager DataManager { get; private set; } = null!;
    [PluginService] internal static ITextureProvider TextureProvider { get; private set; } = null!;
    [PluginService] internal static IGameInteropProvider GameInterop { get; private set; } = null!;

    private const string CommandName = "/xivmit";

    public readonly WindowSystem WindowSystem = new("XIVMit");
    private readonly MainWindow mainWindow;
    private readonly ConfigWindow configWindow;

    internal Configuration Config { get; }
    internal XivMitApi Api { get; }
    internal PlanLoader Loader { get; }
    internal TimelineClock Clock { get; }
    internal ActionWatcher Actions { get; }
    internal FightTracker Tracker { get; }
    internal ScaledFont HeaderFont { get; }

    private readonly Dictionary<uint, ushort> iconCache = [];
    private float autoPickTimer;

    /// <summary>
    /// The zone-in load prompt's current offer, if any - a remembered plan for the territory just
    /// entered, distinct from whatever (if anything) is already loaded. Null means no prompt is
    /// showing. See <see cref="OnTerritoryChanged"/>.
    /// </summary>
    internal string? PendingZonePlanCode { get; private set; }
    internal string? PendingZonePlanLabel { get; private set; }

    public Plugin()
    {
        Config = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();

        Clock = new TimelineClock();
        Api = new XivMitApi();
        Loader = new PlanLoader(Api, Framework, Log);
        Actions = new ActionWatcher(GameInterop, Log);
        Tracker = new FightTracker(Condition, Objects, DutyState, ClientState, Log, Actions, Clock, Config);
        HeaderFont = new ScaledFont(PluginInterface.UiBuilder.FontAtlas);

        Loader.Loaded += OnPlanLoaded;
        Clock.Started += OnClockStarted;

        mainWindow = new MainWindow(this);
        configWindow = new ConfigWindow(this);
        WindowSystem.AddWindow(mainWindow);
        WindowSystem.AddWindow(configWindow);

        CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Open the XIVMit window. /xivmit <code> loads a plan; /xivmit overlay toggles overlay mode.",
        });

        PluginInterface.UiBuilder.Draw += WindowSystem.Draw;
        PluginInterface.UiBuilder.OpenMainUi += ToggleMain;
        PluginInterface.UiBuilder.OpenConfigUi += ToggleConfig;
        Framework.Update += OnFrameworkUpdate;
        ClientState.TerritoryChanged += OnTerritoryChanged;

        if (!string.IsNullOrWhiteSpace(Config.PlanCode))
            Loader.Load(Config.PlanCode);
    }

    public void Dispose()
    {
        Framework.Update -= OnFrameworkUpdate;
        PluginInterface.UiBuilder.Draw -= WindowSystem.Draw;
        PluginInterface.UiBuilder.OpenMainUi -= ToggleMain;
        PluginInterface.UiBuilder.OpenConfigUi -= ToggleConfig;
        ClientState.TerritoryChanged -= OnTerritoryChanged;

        Loader.Loaded -= OnPlanLoaded;
        Clock.Started -= OnClockStarted;

        WindowSystem.RemoveAllWindows();
        mainWindow.Dispose();
        configWindow.Dispose();

        HeaderFont.Dispose();
        Tracker.Dispose();
        Actions.Dispose();
        Loader.Dispose();
        Api.Dispose();

        CommandManager.RemoveHandler(CommandName);
    }

    private void OnFrameworkUpdate(IFramework framework)
    {
        Clock.Update((float)framework.UpdateDelta.TotalSeconds);
        Tracker.Update();
        RetryAutoPick((float)framework.UpdateDelta.TotalSeconds);
    }

    private void OnPlanLoaded(PlanContext? ctx)
    {
        Tracker.SetPlan(ctx);
        if (ctx == null) return;

        Config.RememberPlan(ctx.Plan.Code, ctx.Plan.Title, ctx.Fight.ShortName ?? ctx.Fight.Name);
        // This is the one place every load path funnels through - typed in, recent list, or the
        // zone prompt itself - so recording here means the remembered plan for a duty is always
        // whichever was used there most recently.
        RememberZonePlan(ctx.Plan.Code);
        TryAutoPickPlayer(ctx);

        // Whatever just loaded satisfies any pending zone-in offer for it, even if it was loaded
        // some other way (typed the code manually, picked it from Recent) while the prompt sat
        // there unanswered.
        if (string.Equals(PendingZonePlanCode, ctx.Plan.Code, StringComparison.OrdinalIgnoreCase))
            PendingZonePlanCode = null;
    }

    /// <summary>
    /// Offers to load the plan last used in this territory, if it differs from whatever (if
    /// anything) is already loaded. Cleared and re-evaluated on every zone change, so a stale
    /// offer from the last duty never lingers into a new one.
    /// </summary>
    private void OnTerritoryChanged(uint territory)
    {
        PendingZonePlanCode = null;
        PendingZonePlanLabel = null;

        var remembered = Config.PlanByTerritory.TryGetValue(territory, out var code) && !string.IsNullOrWhiteSpace(code);

        // Independent of the prompt below: a duty where the right plan is already loaded has
        // nothing to prompt for, but should still surface the window rather than leave it closed.
        if (remembered && Config.AutoOpenInSavedZones) mainWindow.IsOpen = true;

        if (!remembered || !Config.PromptToLoadOnZoneIn) return;
        if (string.Equals(Loader.Context?.Plan.Code, code, StringComparison.OrdinalIgnoreCase)) return;

        PendingZonePlanCode = code;
        PendingZonePlanLabel = Config.RecentPlans.FirstOrDefault(r =>
            string.Equals(r.Code, code, StringComparison.OrdinalIgnoreCase))?.Label ?? code;
        mainWindow.IsOpen = true;
    }

    internal void LoadPendingZonePlan()
    {
        if (PendingZonePlanCode is not { } code) return;
        Config.PlanCode = code;
        Config.Save();
        Loader.Load(code);
    }

    internal void DismissZonePrompt()
    {
        PendingZonePlanCode = null;
        PendingZonePlanLabel = null;
    }

    /// <summary>
    /// Starting the clock by hand inside a duty is the other, previously missing half of what
    /// <see cref="Configuration.RequireZoneMatchToAutoStart"/> promises. Loading a plan is
    /// normally done before queueing, from a hub, where there is no duty to associate it with -
    /// so recording on load alone learned nothing in the usual workflow, which left
    /// <see cref="Configuration.PlanByTerritory"/> empty and auto-start permanently suppressed.
    /// A manual press in the duty says "this plan, this pull, here" just as plainly as loading it
    /// there does, so the first pull teaches it and every pull after auto-starts.
    /// </summary>
    private void OnClockStarted()
    {
        if (Loader.Context is { } ctx) RememberZonePlan(ctx.Plan.Code);
    }

    /// <summary>
    /// Associates the current territory with a plan code, for the zone-in prompt and the
    /// auto-start zone check to read back. Gated on actually being in an instanced duty:
    /// recording an overworld territory (a housing district, a city, anywhere a plan might get
    /// loaded or tested outside real content) would let a later, unrelated combat pull there (a
    /// target dummy, anything) pass the zone-match check meant to guard against exactly that.
    /// </summary>
    private void RememberZonePlan(string code)
    {
        if (DutyState.ContentFinderCondition.RowId == 0) return;

        // Every pull runs through here now, so skip the write (and the disk hit) once the pairing
        // is already the one recorded.
        var territory = ClientState.TerritoryType;
        if (Config.PlanByTerritory.TryGetValue(territory, out var existing)
            && string.Equals(existing, code, StringComparison.OrdinalIgnoreCase)) return;

        Config.PlanByTerritory[territory] = code;
        Config.Save();
        Log.Info($"XIVMit remembered plan {code} for territory {territory} - pulls here auto-start now.");
    }

    /// <summary>
    /// The plugin loads its saved plan in the constructor, which usually runs before the player
    /// is logged in - so the auto-pick at load time has no LocalPlayer to match against and
    /// silently gives up. Keep retrying (cheaply) until a row is chosen, since the timeline
    /// shows one player's mitigations and has nothing to draw without one.
    /// </summary>
    private void RetryAutoPick(float delta)
    {
        if (Config.LocalPlayerId != null) return;
        if (Loader.Context is not { } ctx) return;

        autoPickTimer += delta;
        if (autoPickTimer < 1f) return;
        autoPickTimer = 0f;

        TryAutoPickPlayer(ctx);
    }

    private void TryAutoPickPlayer(PlanContext ctx)
    {
        // Respect an existing choice, but drop one that no longer names a row on this roster.
        if (Config.LocalPlayerId != null)
        {
            if (ctx.PlayersById.ContainsKey(Config.LocalPlayerId)) return;
            Config.LocalPlayerId = null;
        }

        if (Objects.LocalPlayer is not { } local) return;

        // Prefer an exact character-name match, then the first row of the job being played. A
        // plan can hold two of the same job, so this is only a default; settings can override.
        var localName = local.Name.TextValue;
        var job = local.ClassJob.Value.Abbreviation.ExtractText();

        var match = ctx.Plan.Players.FirstOrDefault(p =>
                        string.Equals(p.Name, localName, StringComparison.OrdinalIgnoreCase))
                    ?? ctx.Plan.Players.FirstOrDefault(p =>
                        string.Equals(p.Job, job, StringComparison.OrdinalIgnoreCase));

        if (match == null) return;
        Config.LocalPlayerId = match.Id;
        Config.Save();
        Log.Info($"XIVMit matched you to roster row {match.Id} ({match.Job} {match.Name ?? "-"}).");
    }

    /// <summary>Resolve an action id to its icon id, cached (the Action sheet lookup is not free).</summary>
    internal ushort IconIdFor(uint actionId)
    {
        if (iconCache.TryGetValue(actionId, out var cached)) return cached;
        var icon = DataManager.GetExcelSheet<LuminaAction>()?.GetRowOrDefault(actionId)?.Icon ?? 0;
        iconCache[actionId] = icon;
        return icon;
    }

    private void OnCommand(string command, string args)
    {
        var arg = args.Trim();
        if (arg.Equals("overlay", StringComparison.OrdinalIgnoreCase))
        {
            Config.OverlayMode = !Config.OverlayMode;
            Config.Save();
            mainWindow.IsOpen = true;
            return;
        }
        if (arg.Length > 0)
        {
            // Same extraction as the code box: a URL is just as likely to be pasted after the
            // command as typed into the window.
            Config.PlanCode = PlanCodeInput.Extract(arg);
            Config.Save();
            Loader.Load(Config.PlanCode);
            mainWindow.IsOpen = true;
            return;
        }
        ToggleMain();
    }

    internal void ToggleMain() => mainWindow.Toggle();
    internal void ToggleConfig() => configWindow.Toggle();
}
