using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using XIVMit.Api;

namespace XIVMit.Windows;

public sealed class ConfigWindow : ThemedWindow
{
    private readonly Plugin plugin;

    public ConfigWindow(Plugin plugin) : base("XIVMit Settings###xivmit_config")
    {
        this.plugin = plugin;
        Size = new Vector2(430, 420);
        SizeCondition = ImGuiCond.FirstUseEver;
    }

    public override void Draw()
    {
        var cfg = plugin.Config;
        var dirty = false;

        SeparatorText("Which player am I?");
        dirty |= DrawPlayerPicker();
        Hint("Matched to your character automatically where possible.");

        SeparatorText("Automation");

        dirty |= Checkbox("Start clock when combat begins", cfg.AutoStartOnCombat, v => cfg.AutoStartOnCombat = v);

        using (ImRaii.Disabled(!cfg.AutoStartOnCombat))
        {
            dirty |= Checkbox("Only in this plan's zone", cfg.RequireZoneMatchToAutoStart,
                v => cfg.RequireZoneMatchToAutoStart = v);
            Hint("Skips auto-start if this plan hasn't been used in the current duty before, " +
                 "so combat starting elsewhere (a roulette, a different fight) doesn't " +
                 "spuriously start the clock. Learns as you go: start the clock by hand once " +
                 "inside a duty (or load the plan while you're in there) and every pull after " +
                 "that starts on its own.");
        }

        dirty |= Checkbox("Stop clock on wipe or clear", cfg.AutoStopOnCombatEnd, v => cfg.AutoStopOnCombatEnd = v);

        dirty |= Checkbox("Resync from combat", cfg.SyncFromCasts, v => cfg.SyncFromCasts = v);
        Hint("Corrects clock drift when a recognized boss cast or hit is observed, or when " +
             "a new boss takes over as the fight's main target (advancing to the next phase).");

        dirty |= Checkbox("Offer to load a plan on zone-in", cfg.PromptToLoadOnZoneIn, v => cfg.PromptToLoadOnZoneIn = v);
        Hint("Remembers the last plan used in each duty and offers to reload it when you " +
             "zone back in, if a different (or no) plan is currently loaded.");

        dirty |= Checkbox("Open window in remembered zones", cfg.AutoOpenInSavedZones, v => cfg.AutoOpenInSavedZones = v);
        Hint("Opens the window on zone-in for a remembered duty, even with nothing to prompt for.");

        using (ImRaii.Disabled(cfg.PlanByTerritory.Count == 0))
        {
            if (ImGui.Button("Forget remembered zones")) cfg.ForgetZones();
            if (ImGui.IsItemHovered())
            {
                // Tooltips size to their content, so the wrap width has to be set explicitly.
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

        dirty |= Slider("Lookahead", cfg.LookaheadSeconds, 10f, 120f, v => cfg.LookaheadSeconds = v);
        dirty |= Slider("Keep after due", cfg.GraceSeconds, 0f, 20f, v => cfg.GraceSeconds = v);

        dirty |= Checkbox("Overlay mode", cfg.OverlayMode, v => cfg.OverlayMode = v);
        Hint("Draining countdown bars on a transparent window, reading as a game overlay.");

        if (plugin.Tracker.HookFailure is { } fail)
        {
            SeparatorText("Advanced");
            ImGui.TextColored(Theme.Red, "Action hook failed:");
            ImGui.TextWrapped(fail);
        }

        if (dirty) cfg.Save();
    }

    private bool DrawPlayerPicker()
    {
        var ctx = plugin.Loader.Context;
        if (ctx == null)
        {
            ImGui.TextColored(Theme.Text2, "Load a plan to pick your row.");
            return false;
        }

        var current = ctx.PlayersById.TryGetValue(plugin.Config.LocalPlayerId ?? "", out var p)
            ? Describe(p)
            : "(no match - nothing will show)";

        ImGui.SetNextItemWidth(-1);
        using var combo = ImRaii.Combo("##me", current);
        if (!combo) return false;

        var changed = false;
        foreach (var player in ctx.Plan.Players)
        {
            if (!ImGui.Selectable(Describe(player), player.Id == plugin.Config.LocalPlayerId)) continue;
            plugin.Config.LocalPlayerId = player.Id;
            changed = true;
        }
        return changed;

        static string Describe(Player p) =>
            p.Name is { Length: > 0 } n ? $"{p.Job} - {n}" : p.Job;
    }

    private static bool Checkbox(string label, bool value, Action<bool> set)
    {
        if (!ImGui.Checkbox(label, ref value)) return false;
        set(value);
        return true;
    }

    private static bool Slider(string label, float value, float min, float max, Action<float> set)
    {
        if (!ImGui.SliderFloat(label, ref value, min, max, "%.0fs")) return false;
        set(value);
        return true;
    }

    private static void Hint(string text)
    {
        using var color = ImRaii.PushColor(ImGuiCol.Text, Theme.Text2);
        ImGui.TextWrapped(text);
    }

    // These ImGui bindings have no SeparatorText().
    private static void SeparatorText(string label)
    {
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.TextColored(Theme.Text2, label);
        ImGui.Spacing();
    }
}
