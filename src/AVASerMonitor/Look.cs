using Avalonia.Media;

namespace AVASerMonitor;

/// <summary>The colours and fonts the window is drawn in: dark, like an instrument's screen.</summary>
internal static class Look
{
    public static readonly IBrush Window = new SolidColorBrush(Color.Parse("#11161D"));
    public static readonly IBrush Bar = new SolidColorBrush(Color.Parse("#18202A"));
    public static readonly IBrush Screen = new SolidColorBrush(Color.Parse("#0B1219"));
    public static readonly IBrush Border = new SolidColorBrush(Color.Parse("#2A3644"));
    public static readonly IBrush Heading = new SolidColorBrush(Color.Parse("#7C8DA0"));
    public static readonly IBrush Text = new SolidColorBrush(Color.Parse("#D7E2EE"));
    public static readonly IBrush Faint = new SolidColorBrush(Color.Parse("#5E7085"));
    public static readonly IBrush Received = new SolidColorBrush(Color.Parse("#D7E2EE"));
    public static readonly IBrush Sent = new SolidColorBrush(Color.Parse("#FFB347"));
    public static readonly IBrush Notice = new SolidColorBrush(Color.Parse("#4CC9F0"));
    public static readonly IBrush Error = new SolidColorBrush(Color.Parse("#FF6B6B"));
    public static readonly IBrush Live = new SolidColorBrush(Color.Parse("#4EF0A0"));
    public static readonly IBrush Recording = new SolidColorBrush(Color.Parse("#FF4D4D"));

    /// <summary>A fixed-width face for the terminal, with fallbacks on every platform.</summary>
    public static readonly FontFamily Mono = new("Cascadia Mono, Consolas, Menlo, DejaVu Sans Mono, Liberation Mono, monospace");
}
