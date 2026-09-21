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

        using (ImRaii.Disabled(!cfg.AutoStartOnCombat))
        {
            var zoneMatch = cfg.RequireZoneMatchToAutoStart;
            if (ImGui.Checkbox("Only in this plan's zone", ref zoneMatch))
            {
                cfg.RequireZoneMatchToAutoStart = zoneMatch;
                dirty = true;
            }
            Hint("Skips auto-start if this plan hasn't been used in the current duty before, " +
                 "so combat starting elsewhere (a roulette, a different fight) doesn't " +
                 "spuriously start the clock. Learns as you go: start the clock by hand once " +
                 "inside a duty (or load the plan while you're in there) and every pull after " +
                 "that starts on its own.");
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
        Hint("Corrects clock drift when a recognized boss cast or hit is observed, or when " +
             "a new boss takes over as the fight's main target (advancing to the next phase).");

        var zonePrompt = cfg.PromptToLoadOnZoneIn;
        if (ImGui.Checkbox("Offer to load a plan on zone-in", ref zonePrompt))
        {
            cfg.PromptToLoadOnZoneIn = zonePrompt;
            dirty = true;
        }
        Hint("Remembers the last plan used in each duty and offers to reload it when you " +
             "zone back in, if a different (or no) plan is currently loaded.");

        var zoneOpen = cfg.AutoOpenInSavedZones;
        if (ImGui.Checkbox("Open window in remembered zones", ref zoneOpen))
        {
            cfg.AutoOpenInSavedZones = zoneOpen;
            dirty = true;
        }
        Hint("Opens the window on zone-in for a remembered duty, even with nothing to prompt for.");

        using (ImRaii.Disabled(cfg.PlanByTerritory.Count == 0))
        {
            if (ImGui.Button("Forget remembered zones")) plugin.Config.ForgetZones();
            if (ImGui.IsItemHovered())
            {
                // A floating tooltip has no "available width" of its own to wrap against the way
                // in-window text does (it sizes to its content), so the wrap point needs setting
                // explicitly - same reasoning as Hint, just a different mechanism to get there.
                using var tip = ImRaii.Tooltip();
                ImGui.PushTextWrapPos(ImGui.GetFontSize() * 22f);
                ImGui.TextUnformatted(cfg.PlanByTerritory.Count == 0
                    ? "Nothing remembered yet."
                    : $"Clears {cfg.PlanByTerritory.Count} remembered zone(s) - the settings " +
                      "above start relearning from scratch. Use this if one got recorded " +
                      "somewhere it shouldn't have (e.g. testing outside real content).");
                ImGui.PopTextWrapPos();
            }
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

    // TextWrapped, not manually-inserted "\n"s at a fixed character count: ImGui's font is
    // proportional, so a fixed character count wraps to a different pixel width line to line
    // (a run of "i"/"l" vs. "m"/"w") and reads as inconsistent rather than as a paragraph.
    // Wrapping against the real available width is also the only way this survives the window
    // being resized at all, which a hardcoded break point never does.
    private static void Hint(string text)
    {
        using var color = ImRaii.PushColor(ImGuiCol.Text, Theme.Text2);
        ImGui.TextWrapped(text);
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
