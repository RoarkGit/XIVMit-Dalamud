using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Components;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using XIVMit.Core;

namespace XIVMit.Windows;

public sealed class MainWindow : Window, IDisposable
{
    // Rows echo the web app's timeline blocks: a role-tinted body with a brighter pip down the
    // left edge. Sized for legibility mid-pull rather than density.
    private const float RowHeight = 38f;
    private const float PipWidth = 4f;
    private const float IconSize = 28f;
    private const float IconGap = 3f;

    // Overlay mode's draining bars: shorter than timeline rows, since position no longer has to
    // carry timing information.
    private const float BarHeight = 26f;
    private const float BarGap = 3f;
    private const float NameColumnX = PipWidth + 62f;

    // Mitigations landing within this many seconds of each other are read as one call and share
    // a bar. Sized just above the largest gap that still means "press these together".
    private const float ClusterWindow = 1.5f;

    private readonly Plugin plugin;
    private string codeInput;
    private IDisposable? colors;
    private IDisposable? styles;
    private IDisposable? overlayPad;
    private IDisposable? overlayBg;

    // Smoothed spacing push, keyed by assignment id. See Space().
    private readonly Dictionary<string, float> pushByAssignment = [];
    private readonly HashSet<string> seen = [];

    // Seconds the viewport is scrolled ahead of the clock. Deliberately not persisted: it is a
    // transient "let me read ahead" gesture, not a setting.
    private float viewOffset;
    private ClockState lastClockState = ClockState.Stopped;

    // Rendering catches up to a resync over a few frames rather than snapping. See TrackClockJump.
    private float displayLag;
    private float lastClockTime;

    // Bar the cursor is over this frame. Collected during layout and rendered once afterwards,
    // since overlapping bars would otherwise each try to open a tooltip.
    private MitCluster? hoveredCluster;

    public MainWindow(Plugin plugin) : base("XIVMit###xivmit_main")
    {
        this.plugin = plugin;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(340, 200),
            MaximumSize = new Vector2(900, 1400),
        };
        // Taller than it looks like it needs to be: in dense sections the spacing pass pushes
        // the farthest bar off the bottom, and canvas height is the only thing that buys it back
        // (~25% of frames at a 400px canvas, ~15% at 600px).
        Size = new Vector2(400, 640);
        SizeCondition = ImGuiCond.FirstUseEver;
        codeInput = plugin.Config.PlanCode;
    }

    public void Dispose() { }

    public override void PreDraw()
    {
        // The timeline is a fixed canvas whose contents are positioned by the clock, so there is
        // nothing for scrolling to do. Left enabled, a stray pixel of overflow makes the wheel
        // nudge the whole view and pop a scrollbar - motion that reads as a glitch mid-pull.
        var flags = ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse;
        if (plugin.Config.LockWindow) flags |= ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoResize;
        // The title bar is the largest single piece of chrome; dropping it is most of what
        // overlay mode buys. ImGui still moves the window from a drag anywhere in the body.
        // No resize corner at all in overlay mode: it IS an overlay, and a grip hanging over the
        // game reads as a stray artifact even when its colors are cleared.
        if (Overlay) flags |= ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize;
        Flags = flags;

        colors = Theme.Push();
        styles = Theme.PushStyle();

        if (Overlay)
        {
            // Overlay reads as an overlay: the window is transparent so the bars sit on the game.
            // Buttons keep their own fill and border; bare text gets an outline instead of a
            // panel (see OverlayText). The window border goes too, since an outline around
            // nothing looks like a stray artifact.
            overlayPad = ImRaii.PushStyle(ImGuiStyleVar.WindowPadding, new Vector2(6, 5))
                .Push(ImGuiStyleVar.WindowBorderSize, 0f)
                .Push(ImGuiStyleVar.ChildBorderSize, 0f);
            var clear = new Vector4(0, 0, 0, 0);
            overlayBg = ImRaii.PushColor(ImGuiCol.WindowBg, clear)
                .Push(ImGuiCol.Border, clear)
                .Push(ImGuiCol.ResizeGrip, clear)
                .Push(ImGuiCol.ResizeGripHovered, clear)
                .Push(ImGuiCol.ResizeGripActive, clear);
        }
    }

    public override void PostDraw()
    {
        overlayBg?.Dispose();
        overlayPad?.Dispose();
        styles?.Dispose();
        colors?.Dispose();
        overlayBg = null;
        overlayPad = null;
        styles = null;
        colors = null;
    }

    /// <summary>
    /// Overlay is suppressed until a plan is loaded: its whole point is hiding the plan bar,
    /// which is also the only way to load one.
    /// </summary>
    private bool Overlay => plugin.Config.OverlayMode && plugin.Loader.Context != null;

    public override void Draw()
    {
        var ctx = plugin.Loader.Context;

        if (!Overlay)
        {
            DrawPlanBar();
            ImGui.Separator();
        }

        // Ahead of everything else, and independent of Overlay/empty-state: a duty change can
        // happen at any time, including with no plan loaded at all or while overlay is active.
        DrawZonePrompt();

        if (ctx == null)
        {
            DrawEmptyState();
            return;
        }

        // Computed first, before anything that needs it draws: t is the glided authored-time
        // position (unchanged from before CombatTime existed - see TrackClockJump). displayOffset
        // is how far the synced timeline currently sits from pure elapsed combat time, glided the
        // same way, so a label for an authored constant (a phase's start, a mit's time) eases to
        // its corrected reading in step with the bars rather than snapping ahead of them - see
        // the call sites below and DrawTimeGrid/DrawClusterTooltip/DrawPhaseButtons' tooltip.
        var t = TrackClockJump(plugin.Clock);
        var displayOffset = t - plugin.Clock.CombatTime;

        if (Overlay) DrawOverlayHeader(ctx);
        else DrawClockBar(ctx);

        DrawPhaseButtons(ctx);
        if (!Overlay) ImGui.Separator();

        // AutoReturnToNow runs in both modes so a scroll left over from reading ahead cannot
        // survive a pull spent in overlay mode.
        AutoReturnToNow();

        if (plugin.Config.LocalPlayerId == null) { DrawNoRosterRow(); return; }

        hoveredCluster = null;
        if (Overlay) DrawCountdownBars(ctx, t);
        else DrawUpcoming(ctx, t, displayOffset);
        if (hoveredCluster is { } hc) DrawClusterTooltip(ctx, hc, displayOffset);
    }

    /// <summary>
    /// Uniform square for every icon button. Sized implicitly, the item rect is derived from a
    /// glyph measurement that can be wider than the button drawn, so hover triggers slightly off
    /// the visible edge. An explicit size makes rect and visual identical.
    /// </summary>
    private static Vector2 IconSq() => new(ImGui.GetFrameHeight(), ImGui.GetFrameHeight());

    /// <summary>
    /// Text with a dark outline, for overlay mode where it sits directly on the game rather than
    /// on a panel. Buttons carry their own fill and border and need no help; bare text would
    /// otherwise vanish against a bright background.
    ///
    /// The outline goes into the draw list first and the glyphs are then emitted as a normal
    /// widget, so they land on top and the layout cursor still advances the way ImGui expects.
    /// </summary>
    private void OverlayText(Vector4 color, string text)
    {
        if (!Overlay) { ImGui.TextColored(color, text); return; }

        // The text is emitted first so its real rect is known, then the outline is filled in
        // behind it via a channel split. Taking the cursor position up front would put the
        // outline in the wrong place: AlignTextToFramePadding and SameLine offset the glyphs
        // through CurrLineTextBaseOffset without moving the cursor itself.
        var draw = ImGui.GetWindowDrawList();
        draw.ChannelsSplit(2);
        draw.ChannelsSetCurrent(1);
        ImGui.TextColored(color, text);
        var at = ImGui.GetItemRectMin();

        draw.ChannelsSetCurrent(0);
        // Fully opaque, all eight directions: partial alpha compounds where the passes overlap,
        // which is what makes an offset copy read as a smudge rather than a stroke.
        var ink = Theme.U32(new Vector4(0f, 0f, 0f, 1f));
        for (var dx = -1; dx <= 1; dx++)
            for (var dy = -1; dy <= 1; dy++)
                if (dx != 0 || dy != 0)
                    draw.AddText(at + new Vector2(dx, dy), ink, text);
        draw.ChannelsMerge();
    }

    /// <summary>
    /// Offers to load the plan last used in the duty just entered. Re-checked every frame via
    /// Plugin.PendingZonePlanCode rather than snapshotted once, so it disappears on its own the
    /// instant its plan loads by any route (not just its own Load button) - see the note in
    /// Plugin.OnPlanLoaded.
    /// </summary>
    private void DrawZonePrompt()
    {
        if (plugin.PendingZonePlanCode == null) return;

        OverlayText(Theme.Amber, $"Load {plugin.PendingZonePlanLabel} for this duty?");
        ImGui.SameLine();
        if (ImGui.SmallButton("Load##zoneprompt")) plugin.LoadPendingZonePlan();
        ImGui.SameLine();
        if (ImGui.SmallButton("x##zoneprompt")) plugin.DismissZonePrompt();
    }

    private void DrawNoRosterRow()
    {
        ImGui.Spacing();
        OverlayText(Theme.Amber, "No roster row matched your character.");
        OverlayText(Theme.Text2, "Pick which player on this plan is you.");
        ImGui.Spacing();
        if (ImGui.Button("Choose my row")) plugin.ToggleConfig();
    }

    /// <summary>
    /// Overlay mode's view: a fixed stack of draining bars rather than a scrolling axis.
    ///
    /// The two encode time differently on purpose. The full timeline puts a mitigation's
    /// position on the axis, which is what makes a whole phase readable at a glance. At overlay
    /// size there is no room for that, so position becomes plain running order and the bar's
    /// own length carries the countdown - the same trade cactbot's timeline makes.
    /// </summary>
    private void DrawCountdownBars(PlanContext ctx, float t)
    {
        var cfg = plugin.Config;
        var origin = ImGui.GetCursorScreenPos();
        var size = ImGui.GetContentRegionAvail();
        var width = size.X;

        var rows = Cluster(ctx.ForPlayer(cfg.LocalPlayerId!)
            .Where(m => m.StartTime >= t - cfg.GraceSeconds && m.StartTime <= t + cfg.LookaheadSeconds)
            .OrderBy(m => m.StartTime)
            .ToList());

        if (rows.Count == 0)
        {
            OverlayText(Theme.Text3, plugin.Clock.State == ClockState.Stopped
                ? "Clock stopped. Press play or pull."
                : "Nothing coming up.");
            return;
        }

        var fit = Math.Max(1, (int)(size.Y / (BarHeight + BarGap)));
        var y = origin.Y;

        foreach (var c in rows.Take(fit))
        {
            DrawCountdownBar(ctx, c, t, new Vector2(origin.X, y), width);
            y += BarHeight + BarGap;
        }

        ImGui.Dummy(new Vector2(width, Math.Min(size.Y, Math.Min(rows.Count, fit) * (BarHeight + BarGap))));
    }

    private void DrawCountdownBar(PlanContext ctx, MitCluster c, float t, Vector2 origin, float width)
    {
        var draw = ImGui.GetWindowDrawList();
        var dt = c.Time - t;
        var role = Theme.ForJob(c.Mits[0].Player.Job);
        var end = origin + new Vector2(width, BarHeight);

        // The bar drains as its moment approaches, emptying exactly at zero.
        var frac = Math.Clamp(dt / Math.Max(1f, plugin.Config.LookaheadSeconds), 0f, 1f);
        var due = dt <= 0f;

        // Semi-opaque so the drained portion still contrasts against whatever is behind the
        // overlay, without going back to a solid panel.
        draw.AddRectFilled(origin, end, Theme.U32(Theme.Card, 0.78f), 3f);
        if (frac > 0f)
            draw.AddRectFilled(origin, new Vector2(origin.X + width * frac, end.Y),
                Theme.U32(role.Fill, due ? 0.9f : 0.55f), 3f);
        draw.AddRectFilled(origin, new Vector2(origin.X + PipWidth, end.Y), Theme.U32(role.Border), 3f);
        if (due)
            draw.AddRect(origin, end, Theme.U32(role.Border), 3f, ImDrawFlags.None, 1.5f);

        var textY = (BarHeight - ImGui.GetTextLineHeight()) * 0.5f;

        var x = PipWidth + 5f;
        var iconSize = BarHeight - 6f;
        foreach (var m in c.Mits.Take(4))
        {
            ImGui.SetCursorScreenPos(origin + new Vector2(x, 3f));
            DrawIcon(m, iconSize);
            x += iconSize + 2f;
        }

        // Countdown right-aligned, so the numbers form a column instead of drifting with names.
        var label = due ? "NOW" : $"{dt:0.0}s";
        var lw = ImGui.CalcTextSize(label).X;
        ImGui.SetCursorScreenPos(new Vector2(end.X - lw - 7f, origin.Y + textY));
        ImGui.TextColored(Theme.Text, label);

        ImGui.SetCursorScreenPos(origin + new Vector2(x + 4f, textY));
        var name = c.Mits.Count == 1 ? c.Mits[0].DisplayName : string.Join(", ", c.Mits.Select(m => m.DisplayName));
        var avail = end.X - (origin.X + x + 4f) - lw - 14f;
        if (ImGui.CalcTextSize(name).X <= avail) ImGui.TextColored(Theme.Text, name);

        if (ImGui.IsWindowHovered() && ImGui.IsMouseHoveringRect(origin, end)) hoveredCluster = c;
    }


    /// <summary>
    /// Overlay mode's chrome: the transport controls, the clock, and the way back out. What is
    /// dropped is the between-sessions configuration (plan bar, settings, sync readout), not the
    /// controls actually reached for mid-pull.
    /// </summary>
    // Pixel size for the overlay clock/phase readout, rasterized at this size rather than
    // scaled up from the default (see ScaledFont). Larger than the rest of the UI: this line
    // sits directly on the game rather than on a panel, and is meant to be read at a glance
    // mid-pull. Fixed rather than user-configurable: 22px was landed on by trial against the
    // real in-game AXIS font (see HeaderTextYNudge below for why that took several tries).
    private const float HeaderTextPx = 22f;

    // Manual correction on top of the computed baseline centering in DrawOverlayHeader. Dear
    // ImGui does not expose a font's cap height, so that centering estimates it as a ratio of
    // Ascent (capHeightRatio below) - an approximation, confirmed against the real rendered
    // AXIS font to need this fixed -2px nudge to land exactly.
    private const float HeaderTextYNudge = -2f;

    private void DrawOverlayHeader(PlanContext ctx)
    {
        var clock = plugin.Clock;
        var style = ImGui.GetStyle();
        var px = HeaderTextPx;

        // Center the row by hand: ImGui top-aligns items on a line, which leaves the buttons
        // and the larger readout visibly out of step.
        //
        // Deliberately NOT using GetTextLineHeight() (== ImFont.FontSize) for this: FontSize is
        // just the requested pixel size, set directly and independently of the font's actual
        // Ascent/Descent (confirmed from Dear ImGui's own font-bake source - FontSize is a plain
        // assignment from the requested size, not derived from the metrics at all). AXIS is a
        // CJK-capable font, so its real ascent/descent routinely run well past the nominal size,
        // and centering against FontSize was off by exactly that gap - worse the bigger the size
        // requested. Ascent is reliable: Dear ImGui's own glyph placement does
        // `pos.y += Ascent` to go from box-top to baseline, so it is unambiguously the top-to-
        // baseline distance in screen pixels. Our strings (digits, "P3") have no descenders, so
        // their ink spans ~[boxTop, boxTop+Ascent]; center that span, not the fuller line box
        // that also reserves a descender gutter our text never uses.
        float ascent, descentMag;
        using (plugin.HeaderFont.Push(px))
        {
            ascent = ImGui.GetFont().Ascent;
            descentMag = MathF.Abs(ImGui.GetFont().Descent);
        }

        var btn = ImGui.GetFrameHeight();
        var sq = new Vector2(btn, btn);
        // True glyph extent (ascent + descent), not FontSize, sizes the row - so a CJK font's
        // taller metrics reserve enough space without clipping.
        var rowH = MathF.Max(btn, ascent + descentMag);
        var top = ImGui.GetCursorPosY();
        var rowCenter = top + rowH * 0.5f;
        var btnY = rowCenter - btn * 0.5f;

        // Ascent alone overshoots: it reserves headroom for tall CJK glyphs that plain digits
        // and "P1" never use, so centering on the full ascent puts the ink low. What we actually
        // want is the cap height, which Dear ImGui does not expose per-font, so this estimates
        // it as a ratio of ascent. The estimated cap height is BASELINE-anchored, not box-top-
        // anchored: digit/letter ink sits flush against the baseline (bottom of the ascent
        // zone), with the font's CJK headroom above it, empty - so the ink's vertical span
        // within the box is [ascent - capHeight, ascent], not [0, capHeight].
        //
        // 0.5 replaces an earlier 0.72: that's the commonly-cited ratio for ordinary Latin
        // fonts, but AXIS's ascent is inflated well beyond Latin proportions to fit full-width
        // CJK glyphs, so its digits occupy a noticeably smaller fraction of it - 0.72 still left
        // the text visibly low. Landed on 0.5 plus the fixed HeaderTextYNudge above by checking
        // against the real rendered font.
        const float capHeightRatio = 0.5f;
        var capHeight = ascent * capHeightRatio;
        var txtY = rowCenter - (ascent - capHeight * 0.5f) + HeaderTextYNudge;

        ImGui.SetCursorPosY(btnY);
        var icon = clock.State == ClockState.Running ? FontAwesomeIcon.Pause : FontAwesomeIcon.Play;
        if (ImGuiComponents.IconButton(icon, sq)) clock.Toggle();

        ImGui.SameLine();
        ImGui.SetCursorPosY(btnY);
        if (ImGuiComponents.IconButton(FontAwesomeIcon.Stop, sq)) clock.Stop();
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Stop and reset");

        // Only the readout takes the larger font, and it is rasterized at that size rather than
        // stretched from the default, so it stays sharp. Buttons keep the default metrics.
        using (plugin.HeaderFont.Push(px))
        {
            ImGui.SameLine();
            ImGui.SetCursorPosY(txtY);
            // CombatTime, not Time: the readout is the genuine combat stopwatch and never jumps
            // on a resync - see TimelineClock.CombatTime. What the sync corrects is instead
            // reflected in where the timeline's own content sits relative to it (displayOffset).
            OverlayText(Theme.Text, TimelineClock.Format(ctx.DisplayTime(clock.CombatTime)));

            var pi = ctx.PhaseIndexAt(clock.Time);
            if (pi >= 0)
            {
                ImGui.SameLine();
                ImGui.SetCursorPosY(txtY);
                // A non-counting transition phase (FRU's Intermission, TOP's P3 Transition)
                // reads as the number of the real phase before it - DisplayPhaseNumber already
                // does this, so no special-casing is needed here the way the phase BUTTONS
                // below need one (those also carry the phase's bare name, which a number-only
                // compact readout has no room for).
                OverlayText(Theme.Text2, $"P{ctx.DisplayPhaseNumber(pi)}");
            }
        }

        // Right-aligned controls, so there is always a visible way back with no title bar to use.
        // Positioned at an exact target rather than clamped against the text to its left: the
        // clamp let a long phase name push the group until the buttons ran into each other.
        var hasPhases = ctx.Fight.Phases.Count > 0;
        var buttonCount = hasPhases ? 3 : 2; // [phase toggle?], lock, expand
        var group = btn * buttonCount + style.ItemSpacing.X * (buttonCount - 1);

        ImGui.SameLine();
        ImGui.SetCursorPosX(ImGui.GetWindowWidth() - group - style.WindowPadding.X);
        ImGui.SetCursorPosY(btnY);

        if (hasPhases) { DrawPhaseToggle(ctx, sq); ImGui.SameLine(); ImGui.SetCursorPosY(btnY); }

        // Same relative order as DrawPlanBar's toolbar (overlay-toggle, then lock), so the two
        // controls don't appear to swap places between modes.
        if (ImGuiComponents.IconButton(FontAwesomeIcon.Expand, sq)) ToggleOverlay();
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Exit overlay mode");
        ImGui.SameLine();
        ImGui.SetCursorPosY(btnY);

        var locked = plugin.Config.LockWindow;
        if (ImGuiComponents.IconButton(locked ? FontAwesomeIcon.Lock : FontAwesomeIcon.LockOpen, sq))
        {
            plugin.Config.LockWindow = !locked;
            plugin.Config.Save();
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(locked ? "Unlock window position" : "Lock window position");

        if (!plugin.Tracker.HookActive)
            OverlayText(Theme.Amber, "Hit-based sync unavailable.");
    }

    private void ToggleOverlay()
    {
        plugin.Config.OverlayMode = !plugin.Config.OverlayMode;
        plugin.Config.Save();
    }

    /// <summary>
    /// Collapse toggle for the phase strip. Only drawn when the fight has phases at all, so a
    /// single-segment savage does not carry a control for something it never shows.
    /// </summary>
    private void DrawPhaseToggle(PlanContext ctx, Vector2? size = null)
    {
        if (ctx.Fight.Phases.Count == 0) return;

        var icon = plugin.Config.ShowPhaseButtons ? FontAwesomeIcon.AngleUp : FontAwesomeIcon.AngleDown;
        var shown = plugin.Config.ShowPhaseButtons;
        var hit = ImGuiComponents.IconButton(icon, size ?? IconSq());
        if (hit)
        {
            plugin.Config.ShowPhaseButtons = !shown;
            plugin.Config.Save();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(shown ? "Hide phase buttons" : "Show phase buttons");
    }

    private void DrawPlanBar()
    {
        ImGui.SetNextItemWidth(150);
        // Buffer is sized for a pasted share URL, not for the code alone - at 32 the paste got
        // silently clipped mid-URL and loaded whatever nonsense was left.
        if (ImGui.InputTextWithHint("##code", "PLAN-CODE or URL", ref codeInput, 256,
                ImGuiInputTextFlags.EnterReturnsTrue))
            LoadCode();

        ImGui.SameLine();
        using (ImRaii.Disabled(plugin.Loader.Status == LoadStatus.Loading))
        {
            if (ImGui.Button("Load")) LoadCode();
        }

        ImGui.SameLine();
        using (ImRaii.Disabled(plugin.Config.RecentPlans.Count == 0))
        {
            if (ImGuiComponents.IconButton(FontAwesomeIcon.History, IconSq()))
                ImGui.OpenPopup("##recent");
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Recent plans");
        DrawRecentPopup();

        ImGui.SameLine();
        if (ImGuiComponents.IconButton(FontAwesomeIcon.Compress, IconSq())) ToggleOverlay();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(plugin.Loader.Context == null
                ? "Overlay mode (load a plan first)"
                : "Overlay mode: clock and timeline only");

        ImGui.SameLine();
        var locked = plugin.Config.LockWindow;
        if (ImGuiComponents.IconButton(locked ? FontAwesomeIcon.Lock : FontAwesomeIcon.LockOpen, IconSq()))
        {
            plugin.Config.LockWindow = !locked;
            plugin.Config.Save();
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(locked ? "Unlock window position" : "Lock window position");

        ImGui.SameLine();
        if (ImGuiComponents.IconButton(FontAwesomeIcon.Cog, IconSq())) plugin.ToggleConfig();
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Settings");

        switch (plugin.Loader.Status)
        {
            case LoadStatus.Loading:
                ImGui.TextColored(Theme.Text2, "Loading...");
                break;
            case LoadStatus.Failed:
                // Loader.Error is already a complete, friendly sentence - see PlanLoader.Friendly.
                ImGui.TextColored(Theme.Red, plugin.Loader.Error);
                break;
            case LoadStatus.Loaded when plugin.Loader.Context is { } c:
                ImGui.TextColored(Theme.Text2,
                    $"{c.Fight.ShortName ?? c.Fight.Name}  -  {c.Mits.Count} mits");
                break;
        }
    }

    private void DrawRecentPopup()
    {
        using var popup = ImRaii.Popup("##recent");
        if (!popup) return;

        RecentPlan? remove = null;
        foreach (var r in plugin.Config.RecentPlans)
        {
            if (ImGui.Selectable($"{r.Label}##{r.Code}"))
            {
                codeInput = r.Code;
                LoadCode();
                ImGui.CloseCurrentPopup();
            }

            ImGui.SameLine();
            ImGui.TextColored(Theme.Text3, $"  {r.Fight}  {r.Code}");

            // Right-click to forget, so the list does not need a delete affordance per row.
            if (ImGui.IsItemClicked(ImGuiMouseButton.Right)) remove = r;
        }

        ImGui.Separator();
        ImGui.TextColored(Theme.Text3, "Right-click an entry to forget it.");

        if (remove == null) return;
        plugin.Config.RecentPlans.Remove(remove);
        plugin.Config.Save();
    }

    private void LoadCode()
    {
        var code = PlanCodeInput.Extract(codeInput);
        if (code.Length == 0) return;

        // Put the extracted code back in the box, so a pasted URL collapses to the thing that was
        // actually loaded rather than leaving the field scrolled into the middle of a long URL.
        codeInput = code;
        plugin.Config.PlanCode = code;
        plugin.Config.Save();
        plugin.Loader.Load(code);
    }

    private void DrawEmptyState()
    {
        ImGui.TextWrapped("Enter a plan code from xivmit.app to get started.");
        ImGui.Spacing();
        ImGui.TextColored(Theme.Text2, "Paste the whole share URL if you like, or just");
        ImGui.TextColored(Theme.Text2, "the code from it, for example UMAD-4C3ME5.");
    }

    private void DrawClockBar(PlanContext ctx)
    {
        var clock = plugin.Clock;

        var icon = clock.State == ClockState.Running ? FontAwesomeIcon.Pause : FontAwesomeIcon.Play;
        if (ImGuiComponents.IconButton(icon, IconSq())) clock.Toggle();
        ImGui.SameLine();
        if (ImGuiComponents.IconButton(FontAwesomeIcon.Stop, IconSq())) clock.Stop();

        ImGui.SameLine();
        ImGui.AlignTextToFramePadding();
        // CombatTime, not Time - see the identical note in DrawOverlayHeader.
        ImGui.TextColored(Theme.Text, TimelineClock.Format(ctx.DisplayTime(clock.CombatTime)));

        if (ctx.PhaseAt(clock.Time) is { } phase)
        {
            ImGui.SameLine();
            ImGui.TextColored(Theme.Text2, $"-  {phase.Name}");
        }

        var style = ImGui.GetStyle();
        var hasPhases = ctx.Fight.Phases.Count > 0;
        var toggleX = ImGui.GetWindowWidth() - ImGui.GetFrameHeight() - style.WindowPadding.X;

        // Right-aligned just left of the toggle, and only when it genuinely fits: squeezing it
        // in is what let the two run into each other.
        if (plugin.Tracker.LastSyncDescription is { } sync)
        {
            var w = ImGui.CalcTextSize(sync).X;
            var x = (hasPhases ? toggleX : ImGui.GetWindowWidth() - style.WindowPadding.X) - w
                    - (hasPhases ? style.ItemSpacing.X : 0f);
            ImGui.SameLine();
            if (x > ImGui.GetCursorPosX())
            {
                ImGui.SetCursorPosX(x);
                ImGui.AlignTextToFramePadding();
                ImGui.TextColored(Theme.Text3, sync);
            }
        }

        if (hasPhases)
        {
            ImGui.SameLine();
            ImGui.SetCursorPosX(toggleX);
            DrawPhaseToggle(ctx);
        }

        if (!plugin.Tracker.HookActive)
            ImGui.TextColored(Theme.Amber, "Hit-based sync unavailable (hook failed).");
    }

    /// <summary>
    /// Phase jumps as a wrapped button row. The manual counterpart to a cast resync, for when a
    /// phase ends earlier than the authored timeline expects.
    /// </summary>
    private void DrawPhaseButtons(PlanContext ctx)
    {
        if (ctx.Fight.Phases.Count == 0 || !plugin.Config.ShowPhaseButtons) return;

        var current = ctx.PhaseAt(plugin.Clock.Time);

        // Overlay drops the phase name down to its number and tightens the padding: the row
        // still reads as a phase strip, at roughly a quarter of the width.
        using var tight = ImRaii.PushStyle(ImGuiStyleVar.FramePadding, new Vector2(5, 2), Overlay);

        var avail = ImGui.GetContentRegionAvail().X;
        var spacing = ImGui.GetStyle().ItemSpacing.X;
        var used = 0f;

        for (var i = 0; i < ctx.Fight.Phases.Count; i++)
        {
            var p = ctx.Fight.Phases[i];
            // A non-counting transition (FRU's Intermission, TOP's P3 Transition) gets no
            // "P{n}:" prefix at all rather than a number that would either be wrong (the raw
            // index) or misleading (borrowing the phase before it) - its bare name says what it
            // is without claiming to be its own numbered phase.
            var label = p.Intermission ? p.Name
                : Overlay ? $"P{ctx.DisplayPhaseNumber(i)}"
                : $"P{ctx.DisplayPhaseNumber(i)}: {p.Name}";
            var w = ImGui.CalcTextSize(label).X + ImGui.GetStyle().FramePadding.X * 2;

            if (i > 0 && used + spacing + w <= avail)
            {
                ImGui.SameLine();
                used += spacing + w;
            }
            else
            {
                used = w;
            }

            var isCurrent = current != null && ReferenceEquals(current, p);
            using var accent = ImRaii.PushColor(ImGuiCol.Button, Theme.Accent2, isCurrent);

            if (ImGui.Button($"{label}##phase{i}"))
                plugin.Clock.SeekTo(p.StartTime);
            if (ImGui.IsItemHovered())
                // No time shown here (previously "jump to 1:20") - this is a resync, and the
                // readout is now the genuine combat clock (see CombatTime), which this action
                // does not move. Overlay's compact label drops the name, so the tooltip restores
                // it; the non-overlay label already carries it.
                ImGui.SetTooltip(Overlay && !p.Intermission ? $"Sync to {label}: {p.Name}" : $"Sync to {label}");
        }
    }

    /// <summary>
    /// A scrolling time axis rather than a list: mitigations enter at the bottom and rise
    /// toward the NOW line at the top, so distance-to-press is read off position rather than
    /// off a number. Positions are recomputed from the clock every frame, so the motion is
    /// continuous and needs no animation state.
    /// </summary>
    private void DrawUpcoming(PlanContext ctx, float t, float displayOffset)
    {
        var cfg = plugin.Config;

        // The wheel scrolls the viewport, not the clock: the top of the canvas shows `viewTime`
        // rather than the current time. Countdowns on the bars stay relative to the real clock,
        // so a scrolled view reports how far off each mitigation genuinely is.
        var origin = ImGui.GetCursorScreenPos();
        var size = ImGui.GetContentRegionAvail();
        if (size.Y < RowHeight * 2) return;

        HandleWheel(origin, size, ctx);

        var viewTime = t + viewOffset;

        // Draw() returns early when no roster row is picked, so this is non-null here.
        var rows = ctx.ForPlayer(cfg.LocalPlayerId!)
            .Where(m => m.StartTime >= viewTime - cfg.GraceSeconds &&
                        m.StartTime <= viewTime + cfg.LookaheadSeconds)
            .OrderBy(m => m.StartTime)
            .ToList();

        var draw = ImGui.GetWindowDrawList();
        var nowY = origin.Y + RowHeight * 0.5f + 2f;
        var span = origin.Y + size.Y - nowY;
        var right = origin.X + size.X;

        var groups = Cluster(rows);
        Space(groups, nowY, span, cfg.LookaheadSeconds, viewTime, ImGui.GetIO().DeltaTime);

        // Clip to the canvas so rows that pass the top slide out of frame instead of
        // overdrawing the controls above.
        draw.PushClipRect(origin, origin + size, true);

        DrawTimeGrid(draw, origin, right, nowY, span, cfg.LookaheadSeconds, viewTime, ctx, displayOffset);

        // Farthest first, so the most imminent row ends up drawn on top of any overlap.
        for (var i = groups.Count - 1; i >= 0; i--)
            DrawClusterBar(ctx, groups[i], t, new Vector2(origin.X, groups[i].Y - RowHeight * 0.5f),
                size.X);

        // Top line last, so it reads over the bars crossing it. It only means NOW while the view
        // sits at the clock; scrolled away it is just the top of the window, and saying "now"
        // there would be a lie.
        var scrolled = viewOffset > 0.01f;
        draw.AddLine(new Vector2(origin.X, nowY), new Vector2(right, nowY),
            Theme.U32(scrolled ? Theme.Text3 : Theme.Accent, scrolled ? 0.6f : 1f), 2f);
        draw.PopClipRect();

        if (groups.Count == 0)
        {
            var msg = scrolled ? "Nothing here." : plugin.Clock.State == ClockState.Stopped
                ? "Clock stopped. Press play or pull."
                : "Nothing coming up.";
            draw.AddText(new Vector2(origin.X + 8f, nowY + 12f), Theme.U32(Theme.Text3), msg);
        }

        if (scrolled) DrawReturnToNow(origin, size);

        // DrawReturnToNow moves the cursor to place its button; put it back before claiming the
        // canvas, or the Dummy below starts from the button and overruns the region.
        ImGui.SetCursorScreenPos(origin);

        // Claim the canvas without overrunning it: Dummy's full height plus the preceding
        // ItemSpacing would exceed the region by a few pixels and make the window scrollable.
        ImGui.Dummy(size with { Y = Math.Max(0f, size.Y - ImGui.GetStyle().ItemSpacing.Y) });
    }

    /// <summary>A set of mitigations close enough in time to read as a single call.</summary>
    private sealed class MitCluster
    {
        public float Time;                     // earliest member's time, the cluster's anchor
        public readonly List<PlannedMit> Mits = [];
        public float Y;                        // screen position after the spacing pass
    }

    /// <summary>
    /// Groups mitigations that land within <see cref="ClusterWindow"/> of each other. In real
    /// plans a large share of mitigations share an exact timestamp (89 of 280 in UMAD), which no
    /// amount of geometry can separate on a time axis - and semantically they are one call
    /// anyway, so they belong in one bar.
    /// </summary>
    private static List<MitCluster> Cluster(List<PlannedMit> sorted)
    {
        var res = new List<MitCluster>();
        foreach (var m in sorted)
        {
            var last = res.Count > 0 ? res[^1] : null;
            if (last != null && m.StartTime - last.Mits[^1].StartTime <= ClusterWindow)
            {
                last.Mits.Add(m);
                continue;
            }
            var c = new MitCluster { Time = m.StartTime };
            c.Mits.Add(m);
            res.Add(c);
        }
        return res;
    }

    /// <summary>
    /// Pushes clusters apart to a minimum on-screen gap, working outward from the NOW line so
    /// the most imminent bar keeps its true position and any distortion accrues to the ones you
    /// are not about to press. The countdown on each bar remains the exact figure.
    ///
    /// The push is then damped over time. The raw scroll is already perfectly smooth (position
    /// is a linear function of the clock), but the push is not: when a cluster leaves the top of
    /// the window everything below it stops being pushed and snaps upward. Smoothing only the
    /// push, never the true position, removes that jump without adding any lag to when a bar
    /// actually reaches the NOW line.
    /// </summary>
    private void Space(List<MitCluster> groups, float nowY, float span, float lookahead, float t, float dt)
    {
        foreach (var c in groups)
            c.Y = nowY + (c.Time - t) / lookahead * span;

        const float minGap = RowHeight + 3f;
        var spaced = new float[groups.Count];
        for (var i = 0; i < groups.Count; i++)
        {
            spaced[i] = groups[i].Y;
            if (i > 0) spaced[i] = Math.Max(spaced[i], spaced[i - 1] + minGap);
        }

        // Frame-rate independent exponential smoothing, ~90ms time constant.
        var alpha = 1f - MathF.Exp(-dt / 0.09f);
        seen.Clear();

        for (var i = 0; i < groups.Count; i++)
        {
            var c = groups[i];
            var push = spaced[i] - c.Y;

            // Carry the smoothed push across frames per assignment, so a cluster that gains or
            // loses a member (they drop off one at a time during the grace window) keeps the
            // value its surviving members already had instead of restarting from zero.
            float current = 0f;
            var found = 0;
            foreach (var m in c.Mits)
                if (pushByAssignment.TryGetValue(m.Assignment.Id, out var p)) { current += p; found++; }

            // A bar with no history is entering the window: seed it at its settled offset rather
            // than at zero. Animating the first placement would pop it in at its unpushed
            // position - which at entry is the canvas edge - and then slide it into place.
            // Smoothing is for changes to bars already on screen, not for arrivals.
            var smoothed = found > 0 ? Lerp(current / found, push, alpha) : push;
            foreach (var m in c.Mits)
            {
                pushByAssignment[m.Assignment.Id] = smoothed;
                seen.Add(m.Assignment.Id);
            }

            c.Y += smoothed;
        }

        // Drop entries for anything no longer on screen, so the map stays bounded.
        if (pushByAssignment.Count > seen.Count)
            foreach (var k in pushByAssignment.Keys.Where(k => !seen.Contains(k)).ToList())
                pushByAssignment.Remove(k);
    }

    private static float Lerp(float from, float to, float amount) => from + (to - from) * amount;

    /// <summary>
    /// Wheel over the timeline scrolls the viewport ahead of the clock; Ctrl+wheel zooms the
    /// time axis. Neither touches the clock, so scrolling ahead to read a later phase cannot
    /// desync a live pull.
    ///
    /// Direction follows the content: the future sits at the bottom and bars travel upward as
    /// time passes, so wheel-down (which moves content up everywhere else) looks further ahead.
    /// The step scales with the lookahead so a notch always covers the same fraction of the
    /// visible window regardless of zoom.
    /// </summary>
    private void HandleWheel(Vector2 origin, Vector2 size, PlanContext ctx)
    {
        var wheel = ImGui.GetIO().MouseWheel;
        if (wheel == 0f) return;
        if (!ImGui.IsWindowHovered(ImGuiHoveredFlags.ChildWindows)) return;
        if (!ImGui.IsMouseHoveringRect(origin, origin + size)) return;

        var cfg = plugin.Config;

        if (ImGui.GetIO().KeyCtrl)
        {
            // Bounds match the settings slider, so the two cannot disagree.
            var next = Math.Clamp(cfg.LookaheadSeconds - wheel * 5f, 10f, 120f);
            if (Math.Abs(next - cfg.LookaheadSeconds) < 0.01f) return;
            cfg.LookaheadSeconds = next;
            cfg.Save();
            return;
        }

        // Never below the clock (the grace window already shows what just passed) and never
        // past the end of the fight.
        var ahead = Math.Max(0f, ctx.Fight.Duration - plugin.Clock.Time);
        viewOffset = Math.Clamp(viewOffset - wheel * cfg.LookaheadSeconds * 0.25f, 0f, ahead);
    }

    /// <summary>
    /// Returns the time the timeline should render at, easing over a resync instead of
    /// teleporting.
    ///
    /// The clock stays exact - a sync sets it to the truth immediately, and the readout and the
    /// countdowns follow it. What eases is the *view*: on a correction the residual is carried
    /// in <c>displayLag</c> and decays to zero over a couple of hundred milliseconds, so the
    /// bars slide the few seconds to their corrected places instead of jumping.
    ///
    /// Only small corrections glide. Easing across a phase jump would scroll minutes of
    /// timeline past at high speed, which is noise rather than information, so those snap.
    ///
    /// The jump is detected here by comparing the clock against where it should have advanced
    /// to, rather than by subscribing to <see cref="TimelineClock.Resynced"/>: that event can
    /// fire from the game's packet thread inside the action hook, and this state belongs to the
    /// draw thread.
    /// </summary>
    private float TrackClockJump(TimelineClock clock)
    {
        const float glideMax = 10f;   // beyond this it is a phase jump, not drift
        const float tau = 0.22f;      // decay time constant

        var dt = ImGui.GetIO().DeltaTime;
        var expected = lastClockTime + (clock.State == ClockState.Running ? dt : 0f);
        var jump = clock.Time - expected;

        // Tolerance absorbs the mismatch between the render tick this runs on and the game tick
        // the clock advances on; a real correction is far larger.
        if (Math.Abs(jump) > 0.25f)
            displayLag = Math.Abs(jump) <= glideMax ? displayLag - jump : 0f;

        lastClockTime = clock.Time;
        displayLag = Lerp(displayLag, 0f, 1f - MathF.Exp(-dt / tau));
        if (Math.Abs(displayLag) < 0.01f) displayLag = 0f;

        return clock.Time + displayLag;
    }

    /// <summary>
    /// Snaps the view back to the clock when a pull starts. A stale scroll left over from
    /// reading ahead would otherwise hide the mitigations actually coming up.
    /// </summary>
    private void AutoReturnToNow()
    {
        var state = plugin.Clock.State;
        if (state == ClockState.Running && lastClockState != ClockState.Running) viewOffset = 0f;
        lastClockState = state;
    }

    private void DrawReturnToNow(Vector2 origin, Vector2 size)
    {
        var label = $"\u2191 {TimelineClock.Format(viewOffset)} ahead";
        var textW = ImGui.CalcTextSize(label).X;
        var pad = ImGui.GetStyle().FramePadding;

        ImGui.SetCursorScreenPos(new Vector2(origin.X + size.X - textW - pad.X * 2 - 8f, origin.Y + 4f));
        if (ImGui.Button(label)) viewOffset = 0f;
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Back to now");
    }

    /// <summary>
    /// Faint gridlines every 10s, labeled with the fight clock rather than a countdown.
    ///
    /// Anchored to absolute fight time, not to an offset from the clock, so the lines travel
    /// upward with the bars instead of hanging at fixed screen positions while content slides
    /// past them. Line POSITIONS use the raw authored <c>abs</c> - unaffected by displayOffset,
    /// since position already accounts for the sync via t/viewTime, same as every mit bar. Only
    /// the printed LABEL subtracts <paramref name="displayOffset"/>, so a line's number reads as
    /// what it will really be by the synced combat clock, not the raw authored constant (see the
    /// displayOffset note in <see cref="Draw"/>). Labels also run through DisplayTime, so a fight
    /// with a checkpoint (DSR) reads the same here as it does in the plan on the web.
    /// </summary>
    private static void DrawTimeGrid(ImDrawListPtr draw, Vector2 origin, float right,
        float nowY, float span, float lookahead, float viewTime, PlanContext ctx, float displayOffset)
    {
        const float step = 10f;
        var first = MathF.Ceiling(viewTime / step) * step;
        for (var abs = first; abs <= viewTime + lookahead; abs += step)
        {
            if (abs <= viewTime + 0.01f) continue;
            var y = nowY + (abs - viewTime) / lookahead * span;
            draw.AddLine(new Vector2(origin.X, y), new Vector2(right, y), Theme.U32(Theme.Border, 0.7f));
            draw.AddText(new Vector2(origin.X + 2f, y + 1f), Theme.U32(Theme.Text3),
                TimelineClock.Format(ctx.DisplayTime(abs - displayOffset)));
        }
    }

    private void DrawClusterBar(PlanContext ctx, MitCluster c, float t, Vector2 origin, float width)
    {
        var draw = ImGui.GetWindowDrawList();
        var end = origin + new Vector2(width, RowHeight);
        var dt = c.Time - t;

        // Every cluster belongs to one player, so one role color always applies.
        var role = Theme.ForJob(c.Mits[0].Player.Job);

        // Imminent rows brighten. Everything else sits at the web app's resting block opacity.
        var (fillAlpha, textColor) = dt <= 0f ? (0.90f, Theme.Text)
            : dt <= 10f ? (0.65f, Theme.Text)
            : (0.42f, role.Text);

        draw.AddRectFilled(origin, end, Theme.U32(role.Fill, fillAlpha), 3f);
        draw.AddRectFilled(origin, new Vector2(origin.X + PipWidth, end.Y), Theme.U32(role.Border), 3f);
        if (dt <= 0f)
            draw.AddRect(origin, end, Theme.U32(role.Border), 3f, ImDrawFlags.None, 1.5f);

        var textY = (RowHeight - ImGui.GetTextLineHeight()) * 0.5f;

        // Position now carries the timing, so the countdown is a secondary readout: dimmed,
        // and kept only because the exact number still matters when lining up a press.
        ImGui.SetCursorScreenPos(origin + new Vector2(PipWidth + 8f, textY));
        if (dt <= 0f) ImGui.TextColored(Theme.Text, "NOW");
        else ImGui.TextColored(textColor with { W = 0.75f }, $"{dt:0.0}s");

        // Icons shrink to fit rather than overflow, so a 10-wide party cluster still lands
        // inside the bar instead of running off the edge.
        var iconArea = Math.Max(0f, width - NameColumnX - 8f);
        var iconSize = Math.Min(IconSize, iconArea / c.Mits.Count - IconGap);
        var showNames = iconSize >= IconSize - 0.01f;
        var x = NameColumnX;

        foreach (var m in c.Mits)
        {
            ImGui.SetCursorScreenPos(origin + new Vector2(x, (RowHeight - iconSize) * 0.5f));
            DrawIcon(m, iconSize);
            x += iconSize + IconGap;
        }

        // Names only when the icons did not have to shrink; past that the bar is icon-only and
        // the tooltip carries the detail.
        if (showNames)
        {
            var label = c.Mits.Count == 1 ? c.Mits[0].DisplayName : string.Join(", ", c.Mits.Select(m => m.DisplayName));
            var avail = end.X - (origin.X + x) - 12f;
            if (ImGui.CalcTextSize(label).X <= avail)
            {
                ImGui.SetCursorScreenPos(origin + new Vector2(x + 4f, textY));
                ImGui.TextColored(textColor, label);

                if (c.Mits.Count == 1 && c.Mits[0].Assignment.Target is { Length: > 0 } tgt)
                {
                    ImGui.SameLine();
                    var name = ctx.PlayersById.TryGetValue(tgt, out var tp) ? tp.Name ?? tp.Job : tgt;
                    ImGui.TextColored(Theme.Text3, $"\u2192 {name}");
                }
            }
        }

        // Hover is tested against the rect rather than registered as an item, so the bar never
        // captures the mouse: dragging one moves the window, and the return-to-now button
        // underneath a passing bar stays clickable.
        if (ImGui.IsWindowHovered() && ImGui.IsMouseHoveringRect(origin, end)) hoveredCluster = c;
    }

    private static void DrawClusterTooltip(PlanContext ctx, MitCluster c, float displayOffset)
    {
        using var tip = ImRaii.Tooltip();
        foreach (var m in c.Mits)
        {
            var line = $"{TimelineClock.Format(ctx.DisplayTime(m.StartTime - displayOffset))}  {m.DisplayName}";
            if (m.Assignment.Target is { Length: > 0 } tgt)
                line += $" \u2192 {(ctx.PlayersById.TryGetValue(tgt, out var tp) ? tp.Name ?? tp.Job : tgt)}";
            ImGui.TextColored(Theme.Text, line);
            if (m.Assignment.Note is { Length: > 0 } note)
                ImGui.TextColored(Theme.Text2, $"    {note}");
        }
    }

    private void DrawIcon(PlannedMit m, float size)
    {
        var dims = new Vector2(size, size);

        if (m.GameActionId is not { } id)
        {
            ImGui.Dummy(dims);
            return;
        }

        var iconId = plugin.IconIdFor(id);
        if (iconId == 0)
        {
            ImGui.Dummy(dims);
            return;
        }

        var tex = Plugin.TextureProvider.GetFromGameIcon(new GameIconLookup(iconId)).GetWrapOrDefault();
        if (tex == null)
        {
            ImGui.Dummy(dims);
            return;
        }

        ImGui.Image(tex.Handle, dims, Vector2.Zero, Vector2.One, Vector4.One);
    }
}
