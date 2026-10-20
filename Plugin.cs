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

    /// <summary>The remembered plan the zone-in prompt is offering, or null if none.</summary>
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

        HeaderFont.Dispose();
        Tracker.Dispose();
        Actions.Dispose();
        Loader.Dispose();
        Api.Dispose();

        CommandManager.RemoveHandler(CommandName);
    }

    internal void LoadPlan(string code)
    {
        Config.PlanCode = code;
        Config.Save();
        Loader.Load(code);
    }

    internal void ToggleOverlay()
    {
        Config.OverlayMode = !Config.OverlayMode;
        Config.Save();
    }

    private void OnFrameworkUpdate(IFramework framework)
    {
        var delta = (float)framework.UpdateDelta.TotalSeconds;
        Clock.Update(delta);
        Tracker.Update();
        RetryAutoPick(delta);
    }

    private void OnPlanLoaded(PlanContext? ctx)
    {
        Tracker.SetPlan(ctx);
        if (ctx == null) return;

        Config.RememberPlan(ctx.Plan.Code, ctx.Plan.Title, ctx.Fight.Label);
        RememberZonePlan(ctx.Plan.Code);
        TryAutoPickPlayer(ctx);

        // Loading the offered plan by any route answers the prompt.
        if (PlanCodeInput.Same(PendingZonePlanCode, ctx.Plan.Code))
            PendingZonePlanCode = null;
    }

    private void OnTerritoryChanged(uint territory)
    {
        DismissZonePrompt();

        if (!Config.PlanByTerritory.TryGetValue(territory, out var code) || string.IsNullOrWhiteSpace(code))
            return;

        if (Config.AutoOpenInSavedZones) mainWindow.IsOpen = true;

        if (!Config.PromptToLoadOnZoneIn) return;
        if (PlanCodeInput.Same(Loader.Context?.Plan.Code, code)) return;

        PendingZonePlanCode = code;
        PendingZonePlanLabel = Config.RecentPlans.FirstOrDefault(r => PlanCodeInput.Same(r.Code, code))?.Label ?? code;
        mainWindow.IsOpen = true;
    }

    internal void LoadPendingZonePlan()
    {
        if (PendingZonePlanCode is { } code) LoadPlan(code);
    }

    internal void DismissZonePrompt()
    {
        PendingZonePlanCode = null;
        PendingZonePlanLabel = null;
    }

    // Plans are usually loaded before queueing, outside any duty, so starting the clock is the
    // main way a duty gets paired with a plan.
    private void OnClockStarted()
    {
        if (Loader.Context is { } ctx) RememberZonePlan(ctx.Plan.Code);
    }

    /// <summary>
    /// Pairs the current territory with a plan for the zone-in prompt and auto-start check.
    /// Only inside duties, so a dummy pull in a housing ward can't satisfy the zone check later.
    /// </summary>
    private void RememberZonePlan(string code)
    {
        if (DutyState.ContentFinderCondition.RowId == 0) return;

        var territory = ClientState.TerritoryType;
        if (Config.PlanByTerritory.TryGetValue(territory, out var existing) && PlanCodeInput.Same(existing, code))
            return;

        Config.PlanByTerritory[territory] = code;
        Config.Save();
        Log.Info($"XIVMit remembered plan {code} for territory {territory} - pulls here auto-start now.");
    }

    // The saved plan loads in the constructor, usually before login, so there's no local player
    // to match yet. Retry once a second until a row is picked.
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
        // Keep an existing choice unless it's not on this roster.
        if (Config.LocalPlayerId != null)
        {
            if (ctx.PlayersById.ContainsKey(Config.LocalPlayerId)) return;
            Config.LocalPlayerId = null;
        }

        if (Objects.LocalPlayer is not { } local) return;

        // Character name first, then the first row with the current job.
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
            ToggleOverlay();
            mainWindow.IsOpen = true;
        }
        else if (arg.Length > 0)
        {
            LoadPlan(PlanCodeInput.Extract(arg));
            mainWindow.IsOpen = true;
        }
        else
        {
            ToggleMain();
        }
    }

    internal void ToggleMain() => mainWindow.Toggle();
    internal void ToggleConfig() => configWindow.Toggle();
}
