using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Components;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using XIVMit.Core;

namespace XIVMit.Windows;

public sealed class ConfigWindow : Window, IDisposable
{
    private readonly Plugin plugin;
    private IDisposable? colors;
    private IDisposable? styles;

    public ConfigWindow(Plugin plugin) : base("XIVMit Settings###xivmit_config")
    {
        this.plugin = plugin;
        Size = new Vector2(430, 420);
        SizeCondition = ImGuiCond.FirstUseEver;
    }

    public void Dispose() { }

    public override void PreDraw()
    {
        colors = Theme.Push();
        styles = Theme.PushStyle();
    }

    public override void PostDraw()
    {
        styles?.Dispose();
        colors?.Dispose();
        styles = null;
        colors = null;
    }

    public override void Draw()
    {
        var cfg = plugin.Config;
        var dirty = false;

        SeparatorText("Which player am I?");
        DrawPlayerPicker(ref dirty);
        Hint("Matched to your character automatically where possible.");

        SeparatorText("Automation");

        var autoStart = cfg.AutoStartOnCombat;
        if (ImGui.Checkbox("Start clock when combat begins", ref autoStart))
        {
            cfg.AutoStartOnCombat = autoStart;
            dirty = true;
        }

        var autoStop = cfg.AutoStopOnCombatEnd;
        if (ImGui.Checkbox("Stop clock on wipe or clear", ref autoStop))
        {
            cfg.AutoStopOnCombatEnd = autoStop;
            dirty = true;
        }

        var sync = cfg.SyncFromCasts;
        if (ImGui.Checkbox("Resync from combat", ref sync))
        {
            cfg.SyncFromCasts = sync;
            dirty = true;
        }
        Hint("Corrects clock drift when a recognised boss cast or hit is observed.");

        using (ImRaii.Disabled(!cfg.SyncFromCasts))
        {
            var jump = cfg.AllowPhaseJump;
            if (ImGui.Checkbox("Allow large jumps", ref jump))
            {
                cfg.AllowPhaseJump = jump;
                dirty = true;
            }
            Hint("Lets the clock jump more than 5s: when a new boss takes over as the fight's\n" +
                 "main target (advancing to the next phase), or when a recognised action is\n" +
                 "far from where the clock expected it (skipped phase, loaded mid-fight). Turn\n" +
                 "off if the clock jumps unexpectedly.");
        }

        SeparatorText("Display");

        var look = cfg.LookaheadSeconds;
        if (ImGui.SliderFloat("Lookahead", ref look, 10f, 120f, "%.0fs"))
        {
            cfg.LookaheadSeconds = look;
            dirty = true;
        }

        var grace = cfg.GraceSeconds;
        if (ImGui.SliderFloat("Keep after due", ref grace, 0f, 20f, "%.0fs"))
        {
            cfg.GraceSeconds = grace;
            dirty = true;
        }

        var overlay = cfg.OverlayMode;
        if (ImGui.Checkbox("Overlay mode", ref overlay))
        {
            cfg.OverlayMode = overlay;
            dirty = true;
        }
        Hint("Draining countdown bars on a transparent window, reading as a game overlay.");

        var lockWin = cfg.LockWindow;
        if (ImGui.Checkbox("Lock window position", ref lockWin))
        {
            cfg.LockWindow = lockWin;
            dirty = true;
        }

        if (plugin.Tracker.HookFailure is { } fail)
        {
            SeparatorText("Advanced");
            ImGui.TextColored(Theme.Red, "Action hook failed:");
            ImGui.TextWrapped(fail);
        }

        if (dirty) cfg.Save();
    }

    private void DrawPlayerPicker(ref bool dirty)
    {
        var ctx = plugin.Loader.Context;
        if (ctx == null)
        {
            ImGui.TextColored(Theme.Text2, "Load a plan to pick your row.");
            return;
        }

        var current = ctx.PlayersById.TryGetValue(plugin.Config.LocalPlayerId ?? "", out var p)
            ? Describe(p)
            : "(no match - nothing will show)";

        ImGui.SetNextItemWidth(-1);
        using var combo = ImRaii.Combo("##me", current);
        if (!combo) return;

        foreach (var player in ctx.Plan.Players)
        {
            if (!ImGui.Selectable(Describe(player), player.Id == plugin.Config.LocalPlayerId)) continue;
            plugin.Config.LocalPlayerId = player.Id;
            dirty = true;
        }

        static string Describe(Api.Player p) =>
            p.Name is { Length: > 0 } n ? $"{p.Job} - {n}" : p.Job;
    }

    private static void Hint(string text)
    {
        ImGui.TextColored(Theme.Text2, text);
    }

    // This Dalamud build's ImGui bindings expose only the bare Separator(), so section
    // headings are drawn by hand rather than with SeparatorText().
    private static void SeparatorText(string label)
    {
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.TextColored(Theme.Text2, label);
        ImGui.Spacing();
    }
}
