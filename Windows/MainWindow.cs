using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Components;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using XIVMit.Core;

namespace XIVMit.Windows;

public sealed class MainWindow : ThemedWindow
{
    // Timeline rows, styled after the web app's blocks: role-tinted body, brighter left pip.
    private const float RowHeight = 38f;
    private const float PipWidth = 4f;
    private const float IconSize = 28f;
    private const float IconGap = 3f;
    private const float NameColumnX = PipWidth + 62f;

    // Overlay countdown bars.
    private const float BarHeight = 26f;
    private const float BarGap = 3f;

    // Mits this close together are one call and share a bar.
    private const float ClusterWindow = 1.5f;

    // The overlay clock is drawn larger than the rest of the UI. The nudge corrects for the
    // estimated cap height in DrawOverlayHeader; both values were tuned against the AXIS font.
    private const float HeaderTextPx = 22f;
    private const float HeaderTextYNudge = -2f;

    private readonly Plugin plugin;
    private string codeInput;
    private IDisposable? overlayPad;
    private IDisposable? overlayBg;

    // Smoothed spacing push per assignment id. See Space().
    private readonly Dictionary<string, float> pushByAssignment = [];
    private readonly HashSet<string> seen = [];

    // How far the view is scrolled ahead of the clock, in seconds.
    private float viewOffset;
    private ClockState lastClockState = ClockState.Stopped;

    // See TrackClockJump.
    private float displayLag;
    private float lastClockTime;

    // Collected while drawing bars and shown once at the end, since bars can overlap.
    private MitCluster? hoveredCluster;

    public MainWindow(Plugin plugin) : base("XIVMit###xivmit_main")
    {
        this.plugin = plugin;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(340, 200),
            MaximumSize = new Vector2(900, 1400),
        };
        // Tall by default: in dense sections the spacing pass pushes the last bars off the bottom.
        Size = new Vector2(400, 640);
        SizeCondition = ImGuiCond.FirstUseEver;
        codeInput = plugin.Config.PlanCode;
    }

    // Overlay mode hides the plan bar, so it only kicks in once a plan is loaded.
    private bool Overlay => plugin.Config.OverlayMode && plugin.Loader.Context != null;

    public override void PreDraw()
    {
        // Content is positioned by the clock, so scrolling the window would only cause jitter.
        var flags = ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse;
        if (plugin.Config.LockWindow) flags |= ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoResize;
        if (Overlay) flags |= ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize;
        Flags = flags;

        base.PreDraw();

        if (Overlay)
        {
            // Transparent window with no border or resize grip, so the bars sit on the game.
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
        overlayBg = null;
        overlayPad = null;
        base.PostDraw();
    }

    public override void Draw()
    {
        var ctx = plugin.Loader.Context;

        if (!Overlay)
        {
            DrawPlanBar();
            ImGui.Separator();
        }

        DrawZonePrompt();

        if (ctx == null)
        {
            DrawEmptyState();
            return;
        }

        // t is the eased timeline position (see TrackClockJump). displayOffset is how far that
        // sits from real combat time, so time labels ease along with the bars.
        var t = TrackClockJump(plugin.Clock);
        var displayOffset = t - plugin.Clock.CombatTime;

        if (Overlay) DrawOverlayHeader(ctx);
        else DrawClockBar(ctx);

        DrawPhaseButtons(ctx);
        if (!Overlay) ImGui.Separator();

        AutoReturnToNow();

        if (plugin.Config.LocalPlayerId == null) { DrawNoRosterRow(); return; }

        hoveredCluster = null;
        if (Overlay) DrawCountdownBars(ctx, t);
        else DrawUpcoming(ctx, t, displayOffset);
        if (hoveredCluster is { } hc) DrawClusterTooltip(ctx, hc, displayOffset);
    }

    // Explicit size so the hover rect matches the drawn button.
    private static Vector2 IconSq() => new(ImGui.GetFrameHeight(), ImGui.GetFrameHeight());

    /// <summary>Text with a black outline in overlay mode, so it stays readable on the game.</summary>
    private void OverlayText(Vector4 color, string text)
    {
        if (!Overlay) { ImGui.TextColored(color, text); return; }

        // Draw the text first to get its real position (SameLine/AlignTextToFramePadding offset
        // the glyphs without moving the cursor), then put the outline behind it on channel 0.
        var draw = ImGui.GetWindowDrawList();
        draw.ChannelsSplit(2);
        draw.ChannelsSetCurrent(1);
        ImGui.TextColored(color, text);
        var at = ImGui.GetItemRectMin();

        draw.ChannelsSetCurrent(0);
        var ink = Theme.U32(new Vector4(0f, 0f, 0f, 1f));
        for (var dx = -1; dx <= 1; dx++)
            for (var dy = -1; dy <= 1; dy++)
                if (dx != 0 || dy != 0)
                    draw.AddText(at + new Vector2(dx, dy), ink, text);
        draw.ChannelsMerge();
    }

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

    private string EmptyTimelineMessage() => plugin.Clock.State == ClockState.Stopped
        ? "Clock stopped. Press play or pull."
        : "Nothing coming up.";

    /// <summary>The local player's mits from the grace window behind <paramref name="t"/> to the lookahead.</summary>
    private List<PlannedMit> MitsAround(PlanContext ctx, float t)
    {
        var cfg = plugin.Config;
        return ctx.ForPlayer(cfg.LocalPlayerId!)
            .Where(m => m.StartTime >= t - cfg.GraceSeconds && m.StartTime <= t + cfg.LookaheadSeconds)
            .ToList();
    }

    /// <summary>
    /// Overlay view: a stack of bars in running order, each draining as its mit comes due,
    /// instead of positions on a time axis.
    /// </summary>
    private void DrawCountdownBars(PlanContext ctx, float t)
    {
        var origin = ImGui.GetCursorScreenPos();
        var size = ImGui.GetContentRegionAvail();
        var width = size.X;

        var rows = Cluster(MitsAround(ctx, t));
        if (rows.Count == 0)
        {
            OverlayText(Theme.Text3, EmptyTimelineMessage());
            return;
        }

        var fit = Math.Max(1, (int)(size.Y / (BarHeight + BarGap)));
        var y = origin.Y;

        foreach (var c in rows.Take(fit))
        {
            DrawCountdownBar(c, t, new Vector2(origin.X, y), width);
            y += BarHeight + BarGap;
        }

        ImGui.Dummy(new Vector2(width, Math.Min(size.Y, Math.Min(rows.Count, fit) * (BarHeight + BarGap))));
    }

    private void DrawCountdownBar(MitCluster c, float t, Vector2 origin, float width)
    {
        var draw = ImGui.GetWindowDrawList();
        var dt = c.Time - t;
        var role = Theme.ForJob(c.Mits[0].Player.Job);
        var end = origin + new Vector2(width, BarHeight);

        var frac = Math.Clamp(dt / Math.Max(1f, plugin.Config.LookaheadSeconds), 0f, 1f);
        var due = dt <= 0f;

        // Semi-opaque background so the drained part still shows up against the game.
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

        // Right-aligned so the countdowns line up in a column.
        var label = due ? "NOW" : $"{dt:0.0}s";
        var lw = ImGui.CalcTextSize(label).X;
        ImGui.SetCursorScreenPos(new Vector2(end.X - lw - 7f, origin.Y + textY));
        ImGui.TextColored(Theme.Text, label);

        ImGui.SetCursorScreenPos(origin + new Vector2(x + 4f, textY));
        var avail = end.X - (origin.X + x + 4f) - lw - 14f;
        if (ImGui.CalcTextSize(c.Label).X <= avail) ImGui.TextColored(Theme.Text, c.Label);

        if (ImGui.IsWindowHovered() && ImGui.IsMouseHoveringRect(origin, end)) hoveredCluster = c;
    }

    private void DrawOverlayHeader(PlanContext ctx)
    {
        var clock = plugin.Clock;
        var style = ImGui.GetStyle();

        // ImGui top-aligns items on a line, so center the buttons and the larger clock by hand.
        // FontSize is just the requested size; AXIS's real ascent/descent run larger, so use
        // the metrics instead.
        float ascent, descent;
        using (plugin.HeaderFont.Push(HeaderTextPx))
        {
            ascent = ImGui.GetFont().Ascent;
            descent = MathF.Abs(ImGui.GetFont().Descent);
        }

        var btn = ImGui.GetFrameHeight();
        var sq = IconSq();
        var rowH = MathF.Max(btn, ascent + descent);
        var rowCenter = ImGui.GetCursorPosY() + rowH * 0.5f;
        var btnY = rowCenter - btn * 0.5f;

        // Center the digits' ink, which sits on the baseline at roughly half the ascent (AXIS
        // reserves a lot of headroom for CJK glyphs). ImGui doesn't expose cap height.
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

        using (plugin.HeaderFont.Push(HeaderTextPx))
        {
            ImGui.SameLine();
            ImGui.SetCursorPosY(txtY);
            // CombatTime, so the readout never jumps on a resync.
            OverlayText(Theme.Text, TimelineClock.Format(ctx.DisplayTime(clock.CombatTime)));

            var pi = ctx.PhaseIndexAt(clock.Time);
            if (pi >= 0)
            {
                ImGui.SameLine();
                ImGui.SetCursorPosY(txtY);
                OverlayText(Theme.Text2, $"P{ctx.DisplayPhaseNumber(pi)}");
            }
        }

        // Right-aligned to a fixed position so long text to the left can't push the buttons
        // into each other.
        var hasPhases = ctx.Fight.Phases.Count > 0;
        var buttonCount = hasPhases ? 3 : 2;
        var group = btn * buttonCount + style.ItemSpacing.X * (buttonCount - 1);

        ImGui.SameLine();
        ImGui.SetCursorPosX(ImGui.GetWindowWidth() - group - style.WindowPadding.X);
        ImGui.SetCursorPosY(btnY);

        if (hasPhases) { DrawPhaseToggle(); ImGui.SameLine(); ImGui.SetCursorPosY(btnY); }

        // Same order as the plan bar (overlay, then lock).
        if (ImGuiComponents.IconButton(FontAwesomeIcon.Expand, sq)) plugin.ToggleOverlay();
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Exit overlay mode");
        ImGui.SameLine();
        ImGui.SetCursorPosY(btnY);
        DrawLockButton();

        if (!plugin.Tracker.HookActive)
            OverlayText(Theme.Amber, "Hit-based sync unavailable.");
    }

    private void DrawLockButton()
    {
        var locked = plugin.Config.LockWindow;
        if (ImGuiComponents.IconButton(locked ? FontAwesomeIcon.Lock : FontAwesomeIcon.LockOpen, IconSq()))
        {
            plugin.Config.LockWindow = !locked;
            plugin.Config.Save();
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(locked ? "Unlock window position" : "Lock window position");
    }

    private void DrawPhaseToggle()
    {
        var shown = plugin.Config.ShowPhaseButtons;
        if (ImGuiComponents.IconButton(shown ? FontAwesomeIcon.AngleUp : FontAwesomeIcon.AngleDown, IconSq()))
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
        // Long enough for a pasted share URL, not just a code.
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
        if (ImGuiComponents.IconButton(FontAwesomeIcon.Compress, IconSq())) plugin.ToggleOverlay();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(plugin.Loader.Context == null
                ? "Overlay mode (load a plan first)"
                : "Overlay mode: clock and timeline only");

        ImGui.SameLine();
        DrawLockButton();

        ImGui.SameLine();
        if (ImGuiComponents.IconButton(FontAwesomeIcon.Cog, IconSq())) plugin.ToggleConfig();
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Settings");

        switch (plugin.Loader.Status)
        {
            case LoadStatus.Loading:
                ImGui.TextColored(Theme.Text2, "Loading...");
                break;
            case LoadStatus.Failed:
                ImGui.TextColored(Theme.Red, plugin.Loader.Error);
                break;
            case LoadStatus.Loaded when plugin.Loader.Context is { } c:
                ImGui.TextColored(Theme.Text2, $"{c.Fight.Label}  -  {c.Mits.Count} mits");
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

        // Collapse a pasted URL down to the code that was loaded.
        codeInput = code;
        plugin.LoadPlan(code);
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
        ImGui.TextColored(Theme.Text, TimelineClock.Format(ctx.DisplayTime(clock.CombatTime)));

        if (ctx.PhaseAt(clock.Time) is { } phase)
        {
            ImGui.SameLine();
            ImGui.TextColored(Theme.Text2, $"-  {phase.Name}");
        }

        var style = ImGui.GetStyle();
        var hasPhases = ctx.Fight.Phases.Count > 0;
        var toggleX = ImGui.GetWindowWidth() - ImGui.GetFrameHeight() - style.WindowPadding.X;

        // Right-aligned next to the phase toggle, and skipped entirely if it doesn't fit.
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
            DrawPhaseToggle();
        }

        if (!plugin.Tracker.HookActive)
            ImGui.TextColored(Theme.Amber, "Hit-based sync unavailable (hook failed).");
    }

    /// <summary>Wrapping row of phase buttons, for manually syncing to a phase.</summary>
    private void DrawPhaseButtons(PlanContext ctx)
    {
        if (ctx.Fight.Phases.Count == 0 || !plugin.Config.ShowPhaseButtons) return;

        var current = ctx.PhaseAt(plugin.Clock.Time);

        using var tight = ImRaii.PushStyle(ImGuiStyleVar.FramePadding, new Vector2(5, 2), Overlay);

        var avail = ImGui.GetContentRegionAvail().X;
        var spacing = ImGui.GetStyle().ItemSpacing.X;
        var used = 0f;

        for (var i = 0; i < ctx.Fight.Phases.Count; i++)
        {
            var p = ctx.Fight.Phases[i];
            // Intermissions show just their name, since they aren't a numbered phase.
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

            using var accent = ImRaii.PushColor(ImGuiCol.Button, Theme.Accent2, ReferenceEquals(current, p));

            if (ImGui.Button($"{label}##phase{i}"))
                plugin.Clock.SeekTo(p.StartTime);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(Overlay && !p.Intermission ? $"Sync to {label}: {p.Name}" : $"Sync to {label}");
        }
    }

    /// <summary>
    /// Scrolling time axis: mits rise from the bottom toward the NOW line at the top, so
    /// distance on screen is distance in time.
    /// </summary>
    private void DrawUpcoming(PlanContext ctx, float t, float displayOffset)
    {
        var cfg = plugin.Config;
        var origin = ImGui.GetCursorScreenPos();
        var size = ImGui.GetContentRegionAvail();
        if (size.Y < RowHeight * 2) return;

        HandleWheel(origin, size, ctx);

        // The wheel scrolls the view, not the clock. Countdowns stay relative to the real clock.
        var viewTime = t + viewOffset;

        var draw = ImGui.GetWindowDrawList();
        var nowY = origin.Y + RowHeight * 0.5f + 2f;
        var span = origin.Y + size.Y - nowY;
        var right = origin.X + size.X;

        var groups = Cluster(MitsAround(ctx, viewTime));
        Space(groups, nowY, span, cfg.LookaheadSeconds, viewTime, ImGui.GetIO().DeltaTime);

        draw.PushClipRect(origin, origin + size, true);

        DrawTimeGrid(draw, origin, right, nowY, span, cfg.LookaheadSeconds, viewTime, ctx, displayOffset);

        // Farthest first, so the most imminent bar draws on top.
        for (var i = groups.Count - 1; i >= 0; i--)
            DrawClusterBar(ctx, groups[i], t, new Vector2(origin.X, groups[i].Y - RowHeight * 0.5f),
                size.X);

        // Drawn over the bars. Dimmed while scrolled, since it isn't "now" then.
        var scrolled = viewOffset > 0.01f;
        draw.AddLine(new Vector2(origin.X, nowY), new Vector2(right, nowY),
            Theme.U32(scrolled ? Theme.Text3 : Theme.Accent, scrolled ? 0.6f : 1f), 2f);
        draw.PopClipRect();

        if (groups.Count == 0)
        {
            var msg = scrolled ? "Nothing here." : EmptyTimelineMessage();
            draw.AddText(new Vector2(origin.X + 8f, nowY + 12f), Theme.U32(Theme.Text3), msg);
        }

        if (scrolled) DrawReturnToNow(origin, size);

        // DrawReturnToNow moves the cursor, so reset it before claiming the canvas. Leave room
        // for ItemSpacing or the window becomes scrollable.
        ImGui.SetCursorScreenPos(origin);
        ImGui.Dummy(size with { Y = Math.Max(0f, size.Y - ImGui.GetStyle().ItemSpacing.Y) });
    }

    private sealed class MitCluster
    {
        public float Time; // earliest member's time
        public readonly List<PlannedMit> Mits = [];
        public float Y;    // screen position after spacing

        public string Label => string.Join(", ", Mits.Select(m => m.DisplayName));
    }

    /// <summary>
    /// Groups mits within <see cref="ClusterWindow"/> of each other. Lots of mits share an exact
    /// timestamp (89 of 280 in UMAD), and they're one call anyway.
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
    /// Pushes clusters apart to a minimum gap, working down from the NOW line so the nearest bar
    /// stays at its true position.
    ///
    /// The push is smoothed over time, because when a bar leaves the top everything below it
    /// would otherwise snap up. Only the push is smoothed, so bars still reach NOW on time.
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

        // Exponential smoothing, ~90ms time constant.
        var alpha = 1f - MathF.Exp(-dt / 0.09f);
        seen.Clear();

        foreach (var (c, target) in groups.Zip(spaced))
        {
            var push = target - c.Y;

            // Tracked per assignment, so a cluster that gains or loses a member keeps its push.
            float current = 0f;
            var found = 0;
            foreach (var m in c.Mits)
                if (pushByAssignment.TryGetValue(m.Assignment.Id, out var p)) { current += p; found++; }

            // New bars start at their settled offset instead of sliding in from the edge.
            var smoothed = found > 0 ? Lerp(current / found, push, alpha) : push;
            foreach (var m in c.Mits)
            {
                pushByAssignment[m.Assignment.Id] = smoothed;
                seen.Add(m.Assignment.Id);
            }

            c.Y += smoothed;
        }

        if (pushByAssignment.Count > seen.Count)
            foreach (var k in pushByAssignment.Keys.Where(k => !seen.Contains(k)).ToList())
                pushByAssignment.Remove(k);
    }

    private static float Lerp(float from, float to, float amount) => from + (to - from) * amount;

    /// <summary>
    /// Wheel scrolls the view ahead of the clock (down = later), Ctrl+wheel zooms. Neither
    /// touches the clock itself.
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
            // Same bounds as the settings slider.
            var next = Math.Clamp(cfg.LookaheadSeconds - wheel * 5f, 10f, 120f);
            if (Math.Abs(next - cfg.LookaheadSeconds) < 0.01f) return;
            cfg.LookaheadSeconds = next;
            cfg.Save();
            return;
        }

        var ahead = Math.Max(0f, ctx.Fight.Duration - plugin.Clock.Time);
        viewOffset = Math.Clamp(viewOffset - wheel * cfg.LookaheadSeconds * 0.25f, 0f, ahead);
    }

    /// <summary>
    /// Returns the time to render the timeline at. The clock itself jumps on a resync, but the
    /// view eases over small corrections so bars slide instead of teleporting. Big jumps (phase
    /// skips) snap.
    /// </summary>
    private float TrackClockJump(TimelineClock clock)
    {
        const float glideMax = 10f;
        const float tau = 0.22f;

        var dt = ImGui.GetIO().DeltaTime;
        var expected = lastClockTime + (clock.State == ClockState.Running ? dt : 0f);
        var jump = clock.Time - expected;

        // Render and game ticks don't line up exactly, so ignore small mismatches.
        if (Math.Abs(jump) > 0.25f)
            displayLag = Math.Abs(jump) <= glideMax ? displayLag - jump : 0f;

        lastClockTime = clock.Time;
        displayLag = Lerp(displayLag, 0f, 1f - MathF.Exp(-dt / tau));
        if (Math.Abs(displayLag) < 0.01f) displayLag = 0f;

        return clock.Time + displayLag;
    }

    // Snap back to the clock when a pull starts.
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
    /// Gridlines every 10s of fight time, moving with the bars. Labels subtract displayOffset
    /// so they match the combat clock readout.
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

        // A cluster only ever holds one player's mits.
        var role = Theme.ForJob(c.Mits[0].Player.Job);

        var (fillAlpha, textColor) = dt <= 0f ? (0.90f, Theme.Text)
            : dt <= 10f ? (0.65f, Theme.Text)
            : (0.42f, role.Text);

        draw.AddRectFilled(origin, end, Theme.U32(role.Fill, fillAlpha), 3f);
        draw.AddRectFilled(origin, new Vector2(origin.X + PipWidth, end.Y), Theme.U32(role.Border), 3f);
        if (dt <= 0f)
            draw.AddRect(origin, end, Theme.U32(role.Border), 3f, ImDrawFlags.None, 1.5f);

        var textY = (RowHeight - ImGui.GetTextLineHeight()) * 0.5f;

        ImGui.SetCursorScreenPos(origin + new Vector2(PipWidth + 8f, textY));
        if (dt <= 0f) ImGui.TextColored(Theme.Text, "NOW");
        else ImGui.TextColored(textColor with { W = 0.75f }, $"{dt:0.0}s");

        // Icons shrink to fit big clusters; names only show when they didn't have to.
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

        if (showNames)
        {
            var avail = end.X - (origin.X + x) - 12f;
            if (ImGui.CalcTextSize(c.Label).X <= avail)
            {
                ImGui.SetCursorScreenPos(origin + new Vector2(x + 4f, textY));
                ImGui.TextColored(textColor, c.Label);

                if (c.Mits.Count == 1 && c.Mits[0].Assignment.Target is { Length: > 0 } tgt)
                {
                    ImGui.SameLine();
                    ImGui.TextColored(Theme.Text3, $"\u2192 {ctx.PlayerName(tgt)}");
                }
            }
        }

        // Hit-tested rather than an ImGui item, so bars don't capture the mouse: dragging still
        // moves the window and buttons underneath stay clickable.
        if (ImGui.IsWindowHovered() && ImGui.IsMouseHoveringRect(origin, end)) hoveredCluster = c;
    }

    private static void DrawClusterTooltip(PlanContext ctx, MitCluster c, float displayOffset)
    {
        using var tip = ImRaii.Tooltip();
        foreach (var m in c.Mits)
        {
            var line = $"{TimelineClock.Format(ctx.DisplayTime(m.StartTime - displayOffset))}  {m.DisplayName}";
            if (m.Assignment.Target is { Length: > 0 } tgt)
                line += $" \u2192 {ctx.PlayerName(tgt)}";
            ImGui.TextColored(Theme.Text, line);
            if (m.Assignment.Note is { Length: > 0 } note)
                ImGui.TextColored(Theme.Text2, $"    {note}");
        }
    }

    private void DrawIcon(PlannedMit m, float size)
    {
        var dims = new Vector2(size, size);
        var tex = m.GameActionId is { } id && plugin.IconIdFor(id) is var iconId and not 0
            ? Plugin.TextureProvider.GetFromGameIcon(new GameIconLookup(iconId)).GetWrapOrDefault()
            : null;

        if (tex == null) ImGui.Dummy(dims);
        else ImGui.Image(tex.Handle, dims, Vector2.Zero, Vector2.One, Vector4.One);
    }
}
