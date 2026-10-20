using Dalamud.Interface.GameFonts;
using Dalamud.Interface.ManagedFontAtlas;

namespace XIVMit.Core;

/// <summary>
/// A game font rasterized at a given pixel size, rebuilt only when the size changes.
/// SetWindowFontScale would just stretch the default glyphs and blur them.
/// </summary>
public sealed class ScaledFont : IDisposable
{
    private readonly IFontAtlas atlas;
    private IFontHandle? handle;
    private float builtFor;

    public ScaledFont(IFontAtlas atlas) => this.atlas = atlas;

    /// <summary>Pushes the font, or returns null while it's still building.</summary>
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
