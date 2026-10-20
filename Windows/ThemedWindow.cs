using Dalamud.Interface.Windowing;

namespace XIVMit.Windows;

/// <summary>A window drawn with the plugin's <see cref="Theme"/>.</summary>
public abstract class ThemedWindow(string name) : Window(name)
{
    private IDisposable? colors;
    private IDisposable? styles;

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
}
