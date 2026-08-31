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

    public Plugin()
    {
        Config = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();

        Clock = new TimelineClock();
        Api = new XivMitApi();
        Loader = new PlanLoader(Api, Framework, Log);
        Actions = new ActionWatcher(GameInterop, Objects, Log);
        Tracker = new FightTracker(Condition, Objects, DutyState, Log, Actions, Clock, Config);
        HeaderFont = new ScaledFont(PluginInterface.UiBuilder.FontAtlas);

        Loader.Loaded += OnPlanLoaded;

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

        if (!string.IsNullOrWhiteSpace(Config.PlanCode))
            Loader.Load(Config.PlanCode);
    }

    public void Dispose()
    {
        Framework.Update -= OnFrameworkUpdate;
        PluginInterface.UiBuilder.Draw -= WindowSystem.Draw;
        PluginInterface.UiBuilder.OpenMainUi -= ToggleMain;
        PluginInterface.UiBuilder.OpenConfigUi -= ToggleConfig;

        Loader.Loaded -= OnPlanLoaded;

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
        TryAutoPickPlayer(ctx);
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
            Config.PlanCode = arg.ToUpperInvariant();
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
