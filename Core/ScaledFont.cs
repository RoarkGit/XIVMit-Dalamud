using Dalamud.Interface.GameFonts;
using Dalamud.Interface.ManagedFontAtlas;

namespace XIVMit.Core;

/// <summary>
/// A game font rasterised at a requested pixel size, rebuilt only when that size changes.
///
/// The alternative, ImGui's SetWindowFontScale, stretches the glyph bitmaps that were baked at
/// the default size, so anything above 1.0 goes soft. Rasterising at the target size keeps the
/// text sharp. Building an atlas is not free, so the handle is cached and only thrown away when
/// the size actually moves.
/// </summary>
public sealed class ScaledFont : IDisposable
{
    private readonly IFontAtlas atlas;
    private IFontHandle? handle;
    private float builtFor;

    public ScaledFont(IFontAtlas atlas) => this.atlas = atlas;

    /// <summary>
    /// Pushes the font at <paramref name="sizePx"/>, or null if it is not ready yet. A rebuild
    /// is asynchronous, so callers fall back to the default font for the frame or two it takes
    /// rather than blocking.
    /// </summary>
    public IDisposable? Push(float sizePx)
    {
        sizePx = MathF.Round(Math.Clamp(sizePx, 8f, 48f));

        if (handle == null || Math.Abs(builtFor - sizePx) > 0.01f)
        {
            handle?.Dispose();
            handle = atlas.NewGameFontHandle(new GameFontStyle(GameFontFamily.Axis, sizePx));
            builtFor = sizePx;
        }

        return handle.Available ? handle.Push() : null;
    }

    public void Dispose()
    {
        handle?.Dispose();
        handle = null;
    }
}
