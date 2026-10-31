using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using AVASerMonitor.Core;

namespace AVASerMonitor.Tests;

/// <summary>
/// The window, in a headless session, on the demo signal: it connects, the terminal fills, the plotter finds the three
/// series, what is typed is sent and echoed back, and F5 disconnects. Set AVASERMONITOR_SHOTS to a folder to save
/// pictures of it (the README's screenshots come from here).
/// </summary>
public class WindowTests
{
    private static string? Shots => Environment.GetEnvironmentVariable("AVASERMONITOR_SHOTS") is { Length: > 0 } d ? d : null;

    private static MainWindow Open(int width = 1360, int height = 820)
    {
        var settings = Path.Combine(Directory.CreateTempSubdirectory("avasermonitor-").FullName, "settings.json");
        var w = new MainWindow(settings) { Width = width, Height = height };
        w.Show();
        Pump();
        return w;
    }

    private static void Pump() => Dispatcher.UIThread.RunJobs();

    /// <summary>Runs the window's loop until <paramref name="done"/>, as its timer would.</summary>
    private static void RunUntil(MainWindow w, Func<bool> done, int ms = 5000)
    {
        for (var i = 0; i < ms / 10 && !done(); i++)
        {
            Thread.Sleep(10);
            w.Tick();
            Pump();
        }
        Assert.True(done(), "It didn't happen in time.");
    }

    private static void Key(Window w, Key key, RawInputModifiers modifiers = RawInputModifiers.None)
    {
        w.KeyPress(key, modifiers, PhysicalKey.None, null);
        Pump();
    }

    private static void Shot(Window w, string name)
    {
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        var frame = w.CaptureRenderedFrame();
        Assert.NotNull(frame);
        if (Shots is not { } dir) return;
        Directory.CreateDirectory(dir);
        frame.Save(Path.Combine(dir, $"AVASerMonitor-{name}.png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
    }

    [AvaloniaFact]
    public void The_demo_signal_fills_the_terminal_and_the_plotter_and_echoes_what_is_sent()
    {
        var w = Open();
        Assert.Contains((IEnumerable<DeviceInfo>)w.Devices.ItemsSource!, d => d.Kind == DeviceKind.Demo);
        w.Devices.SelectedItem = DemoConnection.Info;
        Assert.Equal("Connect", w.ConnectButton.Content);

        w.ToggleConnection();
        Assert.True(w.Monitor.IsOpen);
        Assert.Equal("Disconnect", w.ConnectButton.Content);
        Assert.False(w.Devices.IsEnabled);
        Assert.StartsWith("● Connected: the demo signal", w.Status.Text);

        RunUntil(w, () => w.Monitor.Plot.Rows.Count >= 120);
        Assert.Contains(w.Entries, e => e.Text.StartsWith("sine:", StringComparison.Ordinal));
        Assert.Equal(["sine", "triangle", "noise"], w.Plot.Series.Select(s => s.Name));
        Assert.Equal(3, w.SeriesToggles.Children.Count);
        Assert.True(w.Plot.Series[0].X.Length > 100);

        // Hiding a series takes it off the plot.
        ((CheckBox)w.SeriesToggles.Children[2]).IsChecked = false;
        Assert.False(w.Plot.Series[2].Visible);

        w.Input.Text = "hello board";
        w.Input.Focus();
        Key(w, Avalonia.Input.Key.Enter);
        Assert.Equal("", w.Input.Text);
        Assert.Contains(w.Entries, e => e.Kind == EntryKind.Sent && e.Text == "hello board");
        RunUntil(w, () => w.Entries.Any(e => e.Text == "echo: hello board"));
        Assert.Equal("hello board", w.Settings.SendHistory[^1]);
        Shot(w, "demo");

        // Hex view shows every byte.
        w.ViewChoice.SelectedIndex = 1;
        var before = w.Entries.Count;
        RunUntil(w, () => w.Entries.Skip(before).Any(e => e.Text.StartsWith("0000  73 69 6E 65", StringComparison.Ordinal) || e.Text.Contains("|sine", StringComparison.Ordinal)));
        RunUntil(w, () => w.Entries.Count > before + 40);
        Shot(w, "hex");

        Key(w, Avalonia.Input.Key.F5);
        Assert.False(w.Monitor.IsOpen);
        Assert.Contains(w.Entries, e => e.Kind == EntryKind.Notice && e.Text.StartsWith("Disconnected", StringComparison.Ordinal));
        Assert.Equal("Connect", w.ConnectButton.Content);
        w.Close();
    }

    [AvaloniaFact]
    public void Sending_with_nothing_open_or_bad_hex_says_why_and_Ctrl_L_clears()
    {
        var w = Open();
        w.Input.Text = "AT";
        w.Send();
        Assert.Contains("Connect first", w.Entries[^1].Text);
        Assert.Equal(EntryKind.Error, w.Entries[^1].Kind);
        Assert.Equal("AT", w.Input.Text);

        w.Devices.SelectedItem = DemoConnection.Info;
        w.ToggleConnection();
        w.SendHex.IsChecked = true;
        w.Input.Text = "4G";
        w.Send();
        Assert.Contains("isn't hex", w.Entries[^1].Text);
        // Stopped first: the demo's next lines would otherwise arrive between the clear and the check.
        w.ToggleConnection();
        Key(w, Avalonia.Input.Key.L, RawInputModifiers.Control);
        Assert.Empty(w.Entries);
        w.Close();
    }

    [AvaloniaFact]
    public void A_bad_baud_rate_is_refused_and_settings_are_remembered()
    {
        var w = Open();
        var serial = new DeviceInfo(DeviceKind.Serial, "/dev/ttyNOSUCH7", "/dev/ttyNOSUCH7", "a test port");
        w.Devices.ItemsSource = new[] { serial, DemoConnection.Info };
        w.Devices.SelectedItem = serial;
        w.Baud.Text = "fast";
        w.ToggleConnection();
        Assert.Contains("isn't a baud rate", w.Entries[^1].Text);
        w.Baud.Text = "57600";
        w.ToggleConnection();
        Assert.Equal(EntryKind.Error, w.Entries[^1].Kind);
        Assert.Contains("/dev/ttyNOSUCH7", w.Entries[^1].Text);
        Assert.False(w.Monitor.IsOpen);

        w.Timestamps.IsChecked = false;
        w.PlotToggle.IsChecked = false;
        w.SaveSettings();
        var path = (string)typeof(MainWindow).GetField("_settingsPath", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(w)!;
        var saved = AppSettings.Load(path);
        Assert.Equal((57600, false, false, "/dev/ttyNOSUCH7"), (saved.BaudRate, saved.Timestamps, saved.ShowPlot, saved.LastDevice));
        w.Close();
    }
}
