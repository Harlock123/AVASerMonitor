using System.Collections.ObjectModel;
using System.Globalization;
using System.IO.Ports;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Styling;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using AVASerMonitor.Core;

namespace AVASerMonitor;

/// <summary>
/// The window: the device bar across the top (which device, how fast, connect, DTR and RTS), the terminal on the left
/// with what arrives as text or hex, the plotter on the right with the values in it, and the send box along the
/// bottom. A timer collects what the connection received about 25 times a second.
/// </summary>
public sealed class MainWindow : Window
{
    private readonly string _settingsPath;
    private readonly DispatcherTimer _timer;
    private DateTime _lastScan = DateTime.MinValue;
    private DateTime _lastRate = DateTime.Now;
    private long _lastBytes;
    private double _rate;
    private int _historyAt = -1;
    private bool _plotDirty;

    public AppSettings Settings { get; }
    public Core.Monitor Monitor { get; } = new();

    /// <summary>The terminal's lines, newest last.</summary>
    public ObservableCollection<Entry> Entries { get; } = [];

    /// <summary>The most lines the terminal keeps.</summary>
    public int MaxEntries { get; set; } = 20000;

    // The device bar.
    public ComboBox Devices { get; } = new() { MinWidth = 330, MaxWidth = 460 };
    public ComboBox Baud { get; } = new() { Width = 120, IsEditable = true };
    public Button ConnectButton { get; } = new() { MinWidth = 110, HorizontalContentAlignment = HorizontalAlignment.Center };
    public ToggleButton Dtr { get; } = new() { Content = "DTR" };
    public ToggleButton Rts { get; } = new() { Content = "RTS" };
    public ToggleButton PlotToggle { get; } = new() { Content = "Plotter" };

    // The terminal.
    public ListBox Terminal { get; } = new();
    public ComboBox ViewChoice { get; } = new() { ItemsSource = new[] { "Text", "Hex" }, Width = 80 };
    public CheckBox Timestamps { get; } = new() { Content = "Timestamps" };
    public CheckBox AutoScroll { get; } = new() { Content = "Auto-scroll" };
    public Button RecordButton { get; } = new();

    // The plotter.
    public PlotView Plot { get; } = new();
    public ComboBox Window_ { get; } = new() { ItemsSource = new[] { "100", "250", "500", "1000", "5000", "All" }, Width = 82 };
    public ComboBox XChoice { get; } = new() { ItemsSource = new[] { "Samples", "Time" }, Width = 100 };
    public ToggleButton Pause { get; } = new() { Content = "Pause" };
    public WrapPanel SeriesToggles { get; } = new() { Margin = new Thickness(8, 0, 8, 4) };
    private readonly Grid _body = new();
    private readonly Control _plotPanel;
    private readonly GridSplitter _splitter = new() { Background = Look.Border, Width = 4, ResizeDirection = GridResizeDirection.Columns };

    // Sending.
    public TextBox Input { get; } = new() { FontFamily = Look.Mono, FontSize = 13, MinHeight = 32, VerticalContentAlignment = VerticalAlignment.Center };
    public CheckBox SendHex { get; } = new() { Content = "Hex" };
    public ComboBox Ending { get; } = new() { ItemsSource = new[] { "No line ending", "LF  \\n", "CR  \\r", "CR LF  \\r\\n" }, Width = 140 };
    public Button SendButton { get; } = new() { Content = "Send", MinWidth = 80, HorizontalContentAlignment = HorizontalAlignment.Center };

    // The status bar.
    public TextBlock Status { get; } = new() { Foreground = Look.Text, VerticalAlignment = VerticalAlignment.Center };
    public TextBlock Counts { get; } = new() { Foreground = Look.Heading, VerticalAlignment = VerticalAlignment.Center };

    public MainWindow(string settingsPath)
    {
        _settingsPath = settingsPath;
        Settings = AppSettings.Load(settingsPath);
        Title = "AVASerMonitor";
        Width = 1360;
        Height = 820;
        MinWidth = 720;
        MinHeight = 420;
        Background = Look.Window;
        try { Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://AVASerMonitor/Assets/AVASerMonitor.png"))); }
        catch (Exception e) when (e is IOException or FileNotFoundException or InvalidOperationException) { }

        Monitor.View = Settings.View;
        Monitor.Lines.Encoding = Settings.Encoding;
        _plotPanel = BuildPlotter();

        var root = new DockPanel();
        var top = BuildDeviceBar();
        DockPanel.SetDock(top, Dock.Top);
        root.Children.Add(top);
        var status = BuildStatusBar();
        DockPanel.SetDock(status, Dock.Bottom);
        root.Children.Add(status);
        var send = BuildSendBar();
        DockPanel.SetDock(send, Dock.Bottom);
        root.Children.Add(send);

        _body.ColumnDefinitions = new ColumnDefinitions("*,4,*");
        var terminal = BuildTerminal();
        Grid.SetColumn(terminal, 0);
        Grid.SetColumn(_splitter, 1);
        Grid.SetColumn(_plotPanel, 2);
        _body.Children.Add(terminal);
        _body.Children.Add(_splitter);
        _body.Children.Add(_plotPanel);
        root.Children.Add(_body);
        Content = root;

        ApplySettings();
        ScanDevices(selectSaved: true);
        UpdateConnectionState();

        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(40), DispatcherPriority.Background, (_, _) => Tick());
        _timer.Start();
        KeyDown += OnKeyDown;
        Closing += (_, _) =>
        {
            _timer.Stop();
            Monitor.Dispose();
            SaveSettings();
        };
    }

    // ───────────────────────────── building ─────────────────────────────

    private Control BuildDeviceBar()
    {
        var refresh = Button("Refresh", ScanDevicesNow, "Look for devices again (plugging one in or out is noticed by itself)");
        Baud.ItemsSource = SerialSettings.CommonBaudRates.Select(b => b.ToString(CultureInfo.InvariantCulture)).ToList();
        ToolTip.SetTip(Baud, "Bits per second: pick one or type any other. A USB HID device and the demo ignore it.");
        var line = new Button { Content = "Line ▾", Flyout = new Flyout { Content = BuildLineSettings(), Placement = PlacementMode.BottomEdgeAlignedLeft } };
        ToolTip.SetTip(line, "Data bits, parity, stop bits, flow control and how text is decoded");
        ConnectButton.Click += (_, _) => ToggleConnection();
        ToolTip.SetTip(ConnectButton, "Open the device, or close it (F5)");
        Dtr.IsCheckedChanged += (_, _) => { Settings.Dtr = Dtr.IsChecked == true; (Monitor.Connection as SerialConnection)?.SetDtr(Settings.Dtr); };
        Rts.IsCheckedChanged += (_, _) => { Settings.Rts = Rts.IsChecked == true; (Monitor.Connection as SerialConnection)?.SetRts(Settings.Rts); };
        ToolTip.SetTip(Dtr, "Data Terminal Ready. Many boards wait for it before sending; dropping and raising it resets an Arduino Uno.");
        ToolTip.SetTip(Rts, "Request To Send");
        var reset = Button("Reset", ResetBoard, "Drop DTR and RTS for a moment and raise them again, which resets most Arduino-style boards");
        PlotToggle.IsCheckedChanged += (_, _) => { Settings.ShowPlot = PlotToggle.IsChecked == true; ShowPlot(Settings.ShowPlot); };
        ToolTip.SetTip(PlotToggle, "Show or hide the plotter");
        Devices.SelectionChanged += (_, _) => UpdateConnectionState();
        ToolTip.SetTip(Devices, "Serial ports (USB-serial adapters and boards named by their maker where the system says), USB HID devices, and the demo signal");

        var bar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        bar.Children.Add(Heading("DEVICE"));
        bar.Children.Add(Devices);
        bar.Children.Add(refresh);
        bar.Children.Add(Heading("BAUD", 10));
        bar.Children.Add(Baud);
        bar.Children.Add(line);
        bar.Children.Add(ConnectButton);
        bar.Children.Add(new Border { Width = 1, Background = Look.Border, Margin = new Thickness(6, 4) });
        bar.Children.Add(Dtr);
        bar.Children.Add(Rts);
        bar.Children.Add(reset);
        var right = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        right.Children.Add(PlotToggle);
        var dock = new DockPanel { Margin = new Thickness(10, 8) };
        DockPanel.SetDock(right, Dock.Right);
        dock.Children.Add(right);
        dock.Children.Add(bar);
        return new Border { Background = Look.Bar, BorderBrush = Look.Border, BorderThickness = new Thickness(0, 0, 0, 1), Child = dock };
    }

    public ComboBox DataBits { get; } = new() { ItemsSource = new[] { "5", "6", "7", "8" }, Width = 120 };
    public ComboBox ParityChoice { get; } = new() { ItemsSource = Enum.GetNames<Parity>(), Width = 120 };
    public ComboBox StopChoice { get; } = new() { ItemsSource = new[] { "1", "1.5", "2" }, Width = 120 };
    public ComboBox FlowChoice { get; } = new() { ItemsSource = new[] { "None", "RTS/CTS", "XON/XOFF", "Both" }, Width = 120 };
    public ComboBox EncodingChoice { get; } = new() { ItemsSource = new[] { "UTF-8", "ASCII", "Latin-1" }, Width = 120 };

    private Control BuildLineSettings()
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto"), RowDefinitions = new RowDefinitions("Auto,Auto,Auto,Auto,Auto,Auto"), Margin = new Thickness(4) };
        void Row(int r, string label, Control control, string tip)
        {
            var l = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 4, 12, 4), Foreground = Look.Text };
            Grid.SetRow(l, r);
            Grid.SetRow(control, r);
            Grid.SetColumn(control, 1);
            control.Margin = new Thickness(0, 4);
            ToolTip.SetTip(control, tip);
            grid.Children.Add(l);
            grid.Children.Add(control);
        }
        Row(0, "Data bits", DataBits, "Bits in each character: 8 nearly always");
        Row(1, "Parity", ParityChoice, "An extra bit to catch errors: None nearly always");
        Row(2, "Stop bits", StopChoice, "The gap after each character");
        Row(3, "Flow control", FlowChoice, "How either end says \"wait\": by the RTS and CTS wires, by XON and XOFF characters, or not at all");
        Row(4, "Text", EncodingChoice, "How the bytes that arrive are read as text");
        var note = new TextBlock { Text = "Takes effect when you next connect (text at once).", FontSize = 11, Foreground = Look.Heading, Margin = new Thickness(0, 6, 0, 0) };
        Grid.SetRow(note, 5);
        Grid.SetColumnSpan(note, 2);
        grid.Children.Add(note);
        DataBits.SelectionChanged += (_, _) => { if (DataBits.SelectedItem is string s) Settings.DataBits = int.Parse(s, CultureInfo.InvariantCulture); };
        ParityChoice.SelectionChanged += (_, _) => { if (ParityChoice.SelectedItem is string s) Settings.Parity = Enum.Parse<Parity>(s); };
        StopChoice.SelectionChanged += (_, _) => Settings.StopBits = StopChoice.SelectedIndex switch { 1 => StopBits.OnePointFive, 2 => StopBits.Two, _ => StopBits.One };
        FlowChoice.SelectionChanged += (_, _) => Settings.Handshake = FlowChoice.SelectedIndex switch { 1 => Handshake.RequestToSend, 2 => Handshake.XOnXOff, 3 => Handshake.RequestToSendXOnXOff, _ => Handshake.None };
        EncodingChoice.SelectionChanged += (_, _) =>
        {
            Settings.Encoding = EncodingChoice.SelectedIndex switch { 1 => TextEncoding.Ascii, 2 => TextEncoding.Latin1, _ => TextEncoding.Utf8 };
            Monitor.Lines.Encoding = Settings.Encoding;
        };
        return grid;
    }

    private Control BuildTerminal()
    {
        Terminal.ItemsSource = Entries;
        ScrollViewer.SetHorizontalScrollBarVisibility(Terminal, ScrollBarVisibility.Auto);
        Terminal.Background = Look.Screen;
        Terminal.ItemTemplate = new FuncDataTemplate<Entry>((entry, _) => new TextBlock
        {
            Text = Shown(entry),
            FontFamily = Look.Mono,
            FontSize = 13,
            // Hex rows keep their columns: the terminal scrolls sideways rather than wrapping one.
            TextWrapping = entry.Hex ? TextWrapping.NoWrap : TextWrapping.Wrap,
            Foreground = entry.Kind switch { EntryKind.Sent => Look.Sent, EntryKind.Notice => Look.Notice, EntryKind.Error => Look.Error, _ => Look.Received },
        });
        Terminal.Styles.Add(new Avalonia.Styling.Style(x => x.OfType<ListBoxItem>())
        {
            Setters = { new Avalonia.Styling.Setter(ListBoxItem.PaddingProperty, new Thickness(8, 0)), new Avalonia.Styling.Setter(ListBoxItem.MinHeightProperty, 0.0) },
        });
        ViewChoice.SelectionChanged += (_, _) =>
        {
            Settings.View = ViewChoice.SelectedIndex == 1 ? ViewMode.Hex : ViewMode.Text;
            Monitor.View = Settings.View;
        };
        ToolTip.SetTip(ViewChoice, "Text: what arrives, line by line. Hex: every byte, sixteen to a row.");
        Timestamps.IsCheckedChanged += (_, _) => { Settings.Timestamps = Timestamps.IsChecked == true; RefreshTerminal(); };
        AutoScroll.IsCheckedChanged += (_, _) => Settings.AutoScroll = AutoScroll.IsChecked == true;
        RecordButton.Click += async (_, _) => await ToggleRecording();
        UpdateRecordButton();

        var bar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        bar.Children.Add(Heading("TERMINAL"));
        bar.Children.Add(ViewChoice);
        bar.Children.Add(Timestamps);
        bar.Children.Add(AutoScroll);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        actions.Children.Add(RecordButton);
        actions.Children.Add(Button("Copy", async () => await CopyTerminal(), "Copy everything in the terminal"));
        actions.Children.Add(Button("Clear", ClearTerminal, "Clear the terminal (Ctrl+L)"));
        var header = new DockPanel { Margin = new Thickness(10, 6) };
        DockPanel.SetDock(actions, Dock.Right);
        header.Children.Add(actions);
        header.Children.Add(bar);
        var panel = new DockPanel();
        DockPanel.SetDock(header, Dock.Top);
        panel.Children.Add(header);
        panel.Children.Add(new Border { BorderBrush = Look.Border, BorderThickness = new Thickness(0, 1, 0, 0), Child = Terminal });
        return panel;
    }

    private Control BuildPlotter()
    {
        Window_.SelectionChanged += (_, _) =>
        {
            Settings.PlotWindow = Window_.SelectedItem is string s && int.TryParse(s, out var n) ? n : 0;
            _plotDirty = true;
        };
        ToolTip.SetTip(Window_, "How many of the latest samples to show");
        XChoice.SelectionChanged += (_, _) => { Settings.PlotByTime = XChoice.SelectedIndex == 1; _plotDirty = true; };
        ToolTip.SetTip(XChoice, "Plot against the sample number or the time each line arrived");
        Pause.IsCheckedChanged += (_, _) => { Pause.Content = Pause.IsChecked == true ? "Resume" : "Pause"; _plotDirty = true; };
        ToolTip.SetTip(Pause, "Hold the plot still to look at it; values keep being collected");
        Monitor.Plot.SeriesAdded += name => _plotDirty = true;

        var bar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        bar.Children.Add(Heading("PLOTTER"));
        bar.Children.Add(new TextBlock { Text = "Last", Foreground = Look.Heading, VerticalAlignment = VerticalAlignment.Center });
        bar.Children.Add(Window_);
        bar.Children.Add(XChoice);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        actions.Children.Add(Pause);
        actions.Children.Add(Button("Export CSV…", async () => await ExportCsv(), "Save every value plotted as a CSV file, a column per series"));
        actions.Children.Add(Button("Clear", ClearPlot, "Forget the values plotted so far"));
        var header = new DockPanel { Margin = new Thickness(10, 6) };
        DockPanel.SetDock(actions, Dock.Right);
        header.Children.Add(actions);
        header.Children.Add(bar);
        var top = new StackPanel { Children = { header, SeriesToggles } };
        var panel = new DockPanel();
        DockPanel.SetDock(top, Dock.Top);
        panel.Children.Add(top);
        panel.Children.Add(new Border { BorderBrush = Look.Border, BorderThickness = new Thickness(0, 1, 0, 0), Child = Plot });
        return panel;
    }

    private Control BuildSendBar()
    {
        // Tunnelling, so Enter and the arrows reach this before the box acts on them itself.
        Input.AddHandler(KeyDownEvent, OnInputKey, RoutingStrategies.Tunnel);
        SendButton.Click += (_, _) => Send();
        SendHex.IsCheckedChanged += (_, _) => { Settings.SendHex = SendHex.IsChecked == true; UpdatePlaceholder(); };
        ToolTip.SetTip(SendHex, "Send the bytes typed in hex (48 65 6C 6C 6F), with no line ending");
        Ending.SelectionChanged += (_, _) => Settings.LineEnding = (LineEnding)Math.Max(0, Ending.SelectedIndex);
        ToolTip.SetTip(Ending, "What is sent after each line of text");
        var right = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
        right.Children.Add(SendHex);
        right.Children.Add(Ending);
        right.Children.Add(SendButton);
        var dock = new DockPanel { Margin = new Thickness(10, 8) };
        DockPanel.SetDock(right, Dock.Right);
        dock.Children.Add(right);
        dock.Children.Add(Input);
        return new Border { Background = Look.Bar, BorderBrush = Look.Border, BorderThickness = new Thickness(0, 1, 0, 0), Child = dock };
    }

    private Control BuildStatusBar()
    {
        var dock = new DockPanel { Margin = new Thickness(10, 4) };
        DockPanel.SetDock(Counts, Dock.Right);
        dock.Children.Add(Counts);
        dock.Children.Add(Status);
        return new Border { Background = Look.Bar, BorderBrush = Look.Border, BorderThickness = new Thickness(0, 1, 0, 0), Child = dock };
    }

    private static TextBlock Heading(string text, double left = 0) => new()
    {
        Text = text, FontSize = 11, FontWeight = FontWeight.SemiBold, Foreground = Look.Heading, VerticalAlignment = VerticalAlignment.Center,
        Margin = new Thickness(left, 0, 2, 0),
    };

    private static Button Button(string text, Action click, string tip)
    {
        var b = new Button { Content = text };
        b.Click += (_, _) => click();
        ToolTip.SetTip(b, tip);
        return b;
    }

    private void ApplySettings()
    {
        Baud.Text = Settings.BaudRate.ToString(CultureInfo.InvariantCulture);
        DataBits.SelectedItem = Settings.DataBits.ToString(CultureInfo.InvariantCulture);
        ParityChoice.SelectedItem = Settings.Parity.ToString();
        StopChoice.SelectedIndex = Settings.StopBits switch { StopBits.OnePointFive => 1, StopBits.Two => 2, _ => 0 };
        FlowChoice.SelectedIndex = Settings.Handshake switch { Handshake.RequestToSend => 1, Handshake.XOnXOff => 2, Handshake.RequestToSendXOnXOff => 3, _ => 0 };
        EncodingChoice.SelectedIndex = (int)Settings.Encoding;
        Dtr.IsChecked = Settings.Dtr;
        Rts.IsChecked = Settings.Rts;
        ViewChoice.SelectedIndex = Settings.View == ViewMode.Hex ? 1 : 0;
        Timestamps.IsChecked = Settings.Timestamps;
        AutoScroll.IsChecked = Settings.AutoScroll;
        SendHex.IsChecked = Settings.SendHex;
        Ending.SelectedIndex = (int)Settings.LineEnding;
        Window_.SelectedItem = Settings.PlotWindow > 0 ? Settings.PlotWindow.ToString(CultureInfo.InvariantCulture) : "All";
        if (Window_.SelectedIndex < 0) Window_.SelectedItem = "500";
        XChoice.SelectedIndex = Settings.PlotByTime ? 1 : 0;
        PlotToggle.IsChecked = Settings.ShowPlot;
        ShowPlot(Settings.ShowPlot);
        UpdatePlaceholder();
    }

    private void ShowPlot(bool show)
    {
        _plotPanel.IsVisible = show;
        _splitter.IsVisible = show;
        _body.ColumnDefinitions[1].Width = new GridLength(show ? 4 : 0);
        _body.ColumnDefinitions[2].Width = show ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
    }

    public void SaveSettings()
    {
        if (int.TryParse(Baud.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var baud) && baud > 0) Settings.BaudRate = baud;
        if (Devices.SelectedItem is DeviceInfo d) Settings.LastDevice = d.Id;
        Settings.Save(_settingsPath);
    }

    // ───────────────────────────── devices ─────────────────────────────

    private void ScanDevicesNow() => ScanDevices(selectSaved: false);

    /// <summary>Lists the devices again, keeping the one chosen (or choosing the saved one).</summary>
    public void ScanDevices(bool selectSaved)
    {
        _lastScan = DateTime.Now;
        var chosen = (Devices.SelectedItem as DeviceInfo)?.Id ?? (selectSaved ? Settings.LastDevice : null);
        var devices = DeviceCatalog.All().ToList();
        if (Monitor.Connection is { } open && devices.All(d => d.Id != open.Device.Id)) devices.Insert(0, open.Device);
        var current = Devices.ItemsSource as IEnumerable<DeviceInfo>;
        if (current != null && current.Select(d => d.Id).SequenceEqual(devices.Select(d => d.Id))) return;
        Devices.ItemsSource = devices;
        Devices.SelectedItem = devices.FirstOrDefault(d => d.Id == chosen) ?? devices.FirstOrDefault(d => d.Kind == DeviceKind.Serial && d.VendorId != null) ?? devices.FirstOrDefault();
    }

    /// <summary>Opens the chosen device, or closes the open one.</summary>
    public void ToggleConnection()
    {
        if (Monitor.IsOpen)
        {
            var name = Monitor.Connection!.Device.Name;
            Monitor.Close();
            Note($"Disconnected from {name}.", EntryKind.Notice);
            UpdateConnectionState();
            return;
        }
        if (Devices.SelectedItem is not DeviceInfo device)
        {
            Note("Choose a device first.", EntryKind.Error);
            return;
        }
        if (device.Kind == DeviceKind.Serial && !(int.TryParse(Baud.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var baud) && baud is > 0 and <= 20_000_000))
        {
            Note($"\"{Baud.Text}\" isn't a baud rate: give bits per second, such as 9600 or 115200.", EntryKind.Error);
            return;
        }
        SaveSettings();
        try
        {
            Monitor.Open(device, Settings.Serial);
            Monitor.Plot.Clear();
            Plot.Clear();
            SeriesToggles.Children.Clear();
            Note(device.Kind switch
            {
                DeviceKind.Serial => $"Connected to {device.Id} at {Settings.BaudRate} baud, {Settings.Serial.Framing}.",
                DeviceKind.UsbHid => $"Connected to {device.Name} ({device.UsbId}). Each input report shows as a row of hex; send output reports in hex, the report ID first.",
                _ => "The demo signal is running: three values fifty times a second. Type something and press Enter to hear it back.",
            }, EntryKind.Notice);
            if (device.Kind == DeviceKind.UsbHid && ViewChoice.SelectedIndex == 0) ViewChoice.SelectedIndex = 1;
        }
        catch (ConnectionException e)
        {
            Note(e.Message, EntryKind.Error);
        }
        UpdateConnectionState();
    }

    /// <summary>Resets an Arduino-style board: DTR and RTS low for a moment, then back up.</summary>
    private async void ResetBoard()
    {
        if (Monitor.Connection is not SerialConnection serial)
        {
            Note("Reset works on a serial port: connect to one first.", EntryKind.Error);
            return;
        }
        serial.SetDtr(false);
        serial.SetRts(false);
        await Task.Delay(120);
        serial.SetDtr(Settings.Dtr);
        serial.SetRts(Settings.Rts);
        Note("Reset the board (DTR and RTS pulsed).", EntryKind.Notice);
    }

    private void UpdateConnectionState()
    {
        var open = Monitor.IsOpen;
        ConnectButton.Content = open ? "Disconnect" : "Connect";
        ConnectButton.Classes.Set("accent", !open);
        Devices.IsEnabled = !open;
        Baud.IsEnabled = !open;
        var device = open ? Monitor.Connection!.Device : Devices.SelectedItem as DeviceInfo;
        var serial = device?.Kind == DeviceKind.Serial;
        Dtr.IsEnabled = Rts.IsEnabled = serial;
        SendButton.IsEnabled = open;
        UpdatePlaceholder();
        Title = open ? $"AVASerMonitor — {device!.Name}" : "AVASerMonitor";
        Status.Text = open
            ? device!.Kind switch
            {
                DeviceKind.Serial => $"● Connected: {device.Id}, {Settings.BaudRate} baud {Settings.Serial.Framing}{(device.Detail.Length > 0 ? $"  ·  {device.Detail}" : "")}",
                DeviceKind.UsbHid => $"● Connected: {device.Name}, {device.Detail}",
                _ => "● Connected: the demo signal",
            }
            : device is null ? "No devices found. Plug one in, or choose the demo signal." : $"Not connected. {device.Label}";
        Status.Foreground = open ? Look.Live : Look.Text;
    }

    private void UpdatePlaceholder()
    {
        var hid = (Monitor.Connection?.Device ?? Devices.SelectedItem as DeviceInfo)?.Kind == DeviceKind.UsbHid;
        Input.PlaceholderText = hid ? "Output report in hex, the report ID first: 00 01 FF…"
            : SendHex.IsChecked == true ? "Bytes in hex: 48 65 6C 6C 6F…"
            : "Text to send (Enter sends; \\n, \\r, \\t and \\xHH work; ↑ and ↓ for history)";
    }

    // ───────────────────────────── the loop ─────────────────────────────

    /// <summary>Collects what arrived and shows it: about 25 times a second.</summary>
    public void Tick()
    {
        var now = DateTime.Now;
        var drained = Monitor.Drain(now);
        if (drained.Entries.Count > 0) Append(drained.Entries);
        if (drained.Lost != null) UpdateConnectionState();
        if (drained.PlotRows > 0) _plotDirty = true;
        if (_plotDirty && Pause.IsChecked != true) RedrawPlot();
        if ((now - _lastRate).TotalSeconds >= 1)
        {
            _rate = (Monitor.BytesIn - _lastBytes) / (now - _lastRate).TotalSeconds;
            _lastBytes = Monitor.BytesIn;
            _lastRate = now;
        }
        Counts.Text = $"{Monitor.Counts}  ·  {Core.Monitor.Size((long)_rate)}/s" +
                      (Monitor.Plot.Count > 0 ? $"  ·  {Monitor.Plot.Count:N0} plotted" : "") +
                      (Monitor.Recorder is { } r ? $"  ·  ● recording {Path.GetFileName(r.Path)} ({Core.Monitor.Size(r.Length)})" : "");
        if (!Monitor.IsOpen && (now - _lastScan).TotalSeconds >= 2) ScanDevices(selectSaved: false);
    }

    private void Append(List<Entry> entries)
    {
        foreach (var e in entries) Entries.Add(e);
        if (Entries.Count > MaxEntries)
        {
            var extra = Entries.Count - MaxEntries;
            // Dropped in a batch, a tenth at a time, rather than one by one as each arrives.
            var drop = Math.Max(extra, MaxEntries / 10);
            for (var i = 0; i < drop && Entries.Count > 0; i++) Entries.RemoveAt(0);
        }
        if (AutoScroll.IsChecked == true && Entries.Count > 0) Terminal.ScrollIntoView(Entries[^1]);
    }

    private void Note(string text, EntryKind kind) => Append([new Entry(DateTime.Now, text, kind)]);

    private string Shown(Entry e)
    {
        var mark = e.Kind switch { EntryKind.Sent => "» ", EntryKind.Notice => "— ", EntryKind.Error => "✗ ", _ => "" };
        return Settings.Timestamps ? $"{e.Time:HH:mm:ss.fff}  {mark}{e.Text}" : mark + e.Text;
    }

    /// <summary>Redraws the terminal's lines (after the timestamps are switched).</summary>
    private void RefreshTerminal()
    {
        var all = Entries.ToList();
        Entries.Clear();
        foreach (var e in all) Entries.Add(e);
        if (Entries.Count > 0) Terminal.ScrollIntoView(Entries[^1]);
    }

    private void RedrawPlot()
    {
        _plotDirty = false;
        var data = Monitor.Plot;
        foreach (var name in data.Names)
        {
            var s = Plot.Get(name);
            (s.X, s.Y) = data.Series(name, Settings.PlotWindow, Settings.PlotByTime);
            if (SeriesToggles.Children.OfType<CheckBox>().All(c => c.Tag as string != name)) AddSeriesToggle(s);
        }
        Plot.XUnit = Settings.PlotByTime ? "s" : "sample";
        Plot.InvalidateVisual();
    }

    private void AddSeriesToggle(PlotSeries s)
    {
        var swatch = new Border { Width = 12, Height = 12, CornerRadius = new CornerRadius(2), Background = new SolidColorBrush(s.Colour), Margin = new Thickness(0, 0, 6, 0) };
        var box = new CheckBox
        {
            Tag = s.Name, IsChecked = true, Margin = new Thickness(0, 0, 14, 0),
            Content = new StackPanel { Orientation = Orientation.Horizontal, Children = { swatch, new TextBlock { Text = s.Name, Foreground = Look.Text } } },
        };
        box.IsCheckedChanged += (_, _) => { s.Visible = box.IsChecked == true; Plot.InvalidateVisual(); };
        ToolTip.SetTip(box, $"Show or hide {s.Name}");
        SeriesToggles.Children.Add(box);
    }

    // ───────────────────────────── sending ─────────────────────────────

    /// <summary>Sends what is in the input box.</summary>
    public void Send()
    {
        var text = Input.Text ?? "";
        if (text.Length == 0 && SendHex.IsChecked != true && Settings.LineEnding == LineEnding.None) return;
        try
        {
            var hid = Monitor.Connection?.Device.Kind == DeviceKind.UsbHid;
            var sent = Monitor.Send(text, SendHex.IsChecked == true || hid, Settings.LineEnding);
            if (Settings.LocalEcho) Append([sent]);
            Remember(text);
            Input.Text = "";
        }
        catch (Exception e) when (e is ConnectionException or FormatException)
        {
            Note(e.Message, EntryKind.Error);
        }
    }

    private void Remember(string text)
    {
        if (text.Length == 0) return;
        Settings.SendHistory.Remove(text);
        Settings.SendHistory.Add(text);
        if (Settings.SendHistory.Count > 50) Settings.SendHistory.RemoveAt(0);
        _historyAt = -1;
    }

    private void OnInputKey(object? sender, KeyEventArgs e)
    {
        var history = Settings.SendHistory;
        switch (e.Key)
        {
            case Key.Enter:
                Send();
                e.Handled = true;
                break;
            case Key.Up when history.Count > 0:
                _historyAt = _historyAt < 0 ? history.Count - 1 : Math.Max(0, _historyAt - 1);
                Input.Text = history[_historyAt];
                Input.CaretIndex = Input.Text.Length;
                e.Handled = true;
                break;
            case Key.Down when _historyAt >= 0:
                _historyAt++;
                Input.Text = _historyAt < history.Count ? history[_historyAt] : "";
                if (_historyAt >= history.Count) _historyAt = -1;
                Input.CaretIndex = Input.Text?.Length ?? 0;
                e.Handled = true;
                break;
        }
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.F5) { ToggleConnection(); e.Handled = true; }
        else if (e.Key == Key.L && e.KeyModifiers == KeyModifiers.Control) { ClearTerminal(); e.Handled = true; }
    }

    // ───────────────────────────── the terminal's buttons ─────────────────────────────

    public void ClearTerminal() => Entries.Clear();

    private void ClearPlot()
    {
        Monitor.Plot.Clear();
        Monitor.Parser.Reset();
        Plot.Clear();
        SeriesToggles.Children.Clear();
    }

    private async Task CopyTerminal()
    {
        if (Clipboard is not { } clipboard) return;
        await clipboard.SetTextAsync(string.Join(Environment.NewLine, Entries.Select(Shown)));
        Note($"Copied {Entries.Count} lines.", EntryKind.Notice);
    }

    private async Task ToggleRecording()
    {
        if (Monitor.Recorder is { } r)
        {
            Monitor.StopRecording();
            Note($"Stopped recording: {r.Path} ({Core.Monitor.Size(new FileInfo(r.Path).Length)}).", EntryKind.Notice);
            UpdateRecordButton();
            return;
        }
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Record to a file",
            SuggestedFileName = $"capture-{DateTime.Now:yyyyMMdd-HHmmss}.log",
            FileTypeChoices =
            [
                new FilePickerFileType("Text log, each line with its time") { Patterns = ["*.log", "*.txt"] },
                new FilePickerFileType("Raw bytes, exactly as they arrived") { Patterns = ["*.bin"] },
            ],
        });
        if (file?.TryGetLocalPath() is not { } path) return;
        var format = Path.GetExtension(path).Equals(".bin", StringComparison.OrdinalIgnoreCase) ? RecordFormat.Raw : RecordFormat.Text;
        try
        {
            Monitor.StartRecording(path, format);
            Note($"Recording {(format == RecordFormat.Raw ? "every byte" : "each line")} to {path}.", EntryKind.Notice);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Note($"Couldn't record to {path}: {e.Message}", EntryKind.Error);
        }
        UpdateRecordButton();
    }

    private void UpdateRecordButton()
    {
        var on = Monitor.Recorder != null;
        RecordButton.Content = on ? "■ Stop recording" : "● Record…";
        RecordButton.Foreground = on ? Look.Recording : Look.Text;
        ToolTip.SetTip(RecordButton, on ? "Stop recording and close the file" : "Record what arrives to a file: a text log with times, or the raw bytes (.bin)");
    }

    private async Task ExportCsv()
    {
        if (Monitor.Plot.Rows.Count == 0)
        {
            Note("Nothing has been plotted yet.", EntryKind.Error);
            return;
        }
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export the plotted values",
            SuggestedFileName = $"plot-{DateTime.Now:yyyyMMdd-HHmmss}.csv",
            FileTypeChoices = [new FilePickerFileType("CSV") { Patterns = ["*.csv"] }],
        });
        if (file?.TryGetLocalPath() is not { } path) return;
        try
        {
            await File.WriteAllTextAsync(path, Monitor.Plot.ToCsv());
            Note($"Exported {Monitor.Plot.Rows.Count:N0} rows to {path}.", EntryKind.Notice);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Note($"Couldn't write {path}: {e.Message}", EntryKind.Error);
        }
    }
}
