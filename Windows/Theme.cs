using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;

namespace XIVMit.Windows;

/// <summary>
/// Mirrors the web app's palette (client/src/styles/vars.css) so the in-game window reads as the
/// same tool. Values are copied verbatim from the CSS custom properties; keep them in sync if
/// the site's theme changes.
/// </summary>
public static class Theme
{
    private static Vector4 Hex(uint rgb, float a = 1f) => new(
        ((rgb >> 16) & 0xFF) / 255f,
        ((rgb >> 8) & 0xFF) / 255f,
        (rgb & 0xFF) / 255f,
        a);

    // --bg / --surface / --card / --border / --accent / --text / --text2 / --text3
    public static readonly Vector4 Bg = Hex(0x0b0a13);
    public static readonly Vector4 Surface = Hex(0x12101e);
    public static readonly Vector4 Card = Hex(0x1a1728);
    public static readonly Vector4 Border = Hex(0x282440);
    public static readonly Vector4 Accent = Hex(0x7c5cfc);
    public static readonly Vector4 Accent2 = Hex(0x5a3fdc);
    public static readonly Vector4 Text = Hex(0xe2deff);
    public static readonly Vector4 Text2 = Hex(0x8880b8);
    public static readonly Vector4 Text3 = Hex(0x4e4878);
    public static readonly Vector4 Green = Hex(0x3ecc68);
    public static readonly Vector4 Amber = Hex(0xf0a830);
    public static readonly Vector4 Red = Hex(0xe05050);

    public readonly record struct RoleColors(Vector4 Fill, Vector4 Border, Vector4 Text);

    // --role-*: fill is the rgb triplet the CSS uses behind a block, border/text its companions.
    private static readonly Dictionary<string, RoleColors> Roles = new()
    {
        ["tank"] = new(Hex(0x1c3eb9), Hex(0x5a8cff), Hex(0xaac8ff)),
        ["heal"] = new(Hex(0x10642e), Hex(0x32bc5f), Hex(0x7ae89a)),
        ["melee"] = new(Hex(0x9e301a), Hex(0xee6446), Hex(0xffa080)),
        ["pranged"] = new(Hex(0x766a10), Hex(0xd4c030), Hex(0xeed858)),
        ["caster"] = new(Hex(0x552a98), Hex(0xb276f2), Hex(0xd4a0ff)),
        ["extras"] = new(Hex(0x646464), Hex(0xa0a0a0), Hex(0xc0c0c0)),
    };

    // JOB_ROLE from client/src/gameUtils.ts.
    private static readonly Dictionary<string, string> JobRole = new(StringComparer.OrdinalIgnoreCase)
    {
        ["TANK"] = "tank", ["PLD"] = "tank", ["WAR"] = "tank", ["DRK"] = "tank", ["GNB"] = "tank",
        ["WHM"] = "heal", ["SCH"] = "heal", ["AST"] = "heal", ["SGE"] = "heal",
        ["MELEE"] = "melee", ["MNK"] = "melee", ["DRG"] = "melee", ["NIN"] = "melee",
        ["SAM"] = "melee", ["RPR"] = "melee", ["VPR"] = "melee",
        ["RANGED"] = "pranged", ["BRD"] = "pranged", ["MCH"] = "pranged", ["DNC"] = "pranged",
        ["CASTER"] = "caster", ["BLM"] = "caster", ["SMN"] = "caster", ["RDM"] = "caster",
        ["PCT"] = "caster",
        ["EXTRAS"] = "extras",
    };

    public static RoleColors ForJob(string job) =>
        Roles[JobRole.TryGetValue(job, out var role) ? role : "extras"];

    public static uint U32(Vector4 c) => ImGui.ColorConvertFloat4ToU32(c);

    public static uint U32(Vector4 c, float alpha) =>
        ImGui.ColorConvertFloat4ToU32(c with { W = alpha });

    /// <summary>
    /// Pushes the window chrome. Dispose in PostDraw. Kept as one call so both windows stay
    /// visually identical without duplicating the list.
    /// </summary>
    public static IDisposable Push() =>
        ImRaii.PushColor(ImGuiCol.WindowBg, Surface)
            .Push(ImGuiCol.ChildBg, Bg)
            .Push(ImGuiCol.PopupBg, Card)
            // The web app's buttons read as buttons because of their 1px border, not their
            // fill (.btn-ghost is transparent + --border). ImGui has one global border colour,
            // so this uses --text3, the lightest palette value that still belongs to the
            // chrome, and gives buttons a fill clearly lighter than the window behind them.
            .Push(ImGuiCol.Border, Text3)
            .Push(ImGuiCol.Text, Text)
            .Push(ImGuiCol.TextDisabled, Text3)
            .Push(ImGuiCol.FrameBg, Card)
            .Push(ImGuiCol.FrameBgHovered, Border)
            .Push(ImGuiCol.FrameBgActive, Border)
            .Push(ImGuiCol.Button, Border)
            .Push(ImGuiCol.ButtonHovered, Accent2)
            .Push(ImGuiCol.ButtonActive, Accent)
            .Push(ImGuiCol.Header, Accent2)
            .Push(ImGuiCol.HeaderHovered, Accent)
            .Push(ImGuiCol.HeaderActive, Accent)
            .Push(ImGuiCol.TitleBg, Bg)
            .Push(ImGuiCol.TitleBgActive, Card)
            .Push(ImGuiCol.Separator, Border)
            .Push(ImGuiCol.SliderGrab, Accent)
            .Push(ImGuiCol.SliderGrabActive, Accent)
            .Push(ImGuiCol.CheckMark, Accent)
            .Push(ImGuiCol.ScrollbarBg, Bg)
            .Push(ImGuiCol.ScrollbarGrab, Border);

    public static IDisposable PushStyle() =>
        ImRaii.PushStyle(ImGuiStyleVar.WindowRounding, 6f)
            .Push(ImGuiStyleVar.ChildRounding, 4f)
            .Push(ImGuiStyleVar.FrameRounding, 4f)
            // Every button and input gets a visible edge, matching the site's 1px borders.
            .Push(ImGuiStyleVar.FrameBorderSize, 1f)
            .Push(ImGuiStyleVar.PopupRounding, 4f)
            .Push(ImGuiStyleVar.WindowPadding, new Vector2(10, 10))
            .Push(ImGuiStyleVar.FramePadding, new Vector2(8, 5))
            .Push(ImGuiStyleVar.ItemSpacing, new Vector2(8, 7));
}
