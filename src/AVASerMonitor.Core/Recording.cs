using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AVASerMonitor.Core;

/// <summary>How a recording is written: the bytes exactly as they came, or the lines as text with when each arrived.</summary>
public enum RecordFormat { Raw, Text }

/// <summary>
/// A recording to a file: either every byte as it arrived (.bin, for replaying or a hex editor) or each line with its
/// time ("2026-10-31 13:00:00.125  sine:0.71"), with what was sent marked by "&gt;&gt;". Thread-safe: received bytes
/// arrive on the connection's thread.
/// </summary>
public sealed class Recorder : IDisposable
{
    private readonly FileStream _file;
    private readonly StreamWriter? _text;
    private readonly Lock _lock = new();

    public Recorder(string path, RecordFormat format)
    {
        Path = path;
        Format = format;
        if (System.IO.Path.GetDirectoryName(path) is { Length: > 0 } dir) Directory.CreateDirectory(dir);
        _file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
        if (format == RecordFormat.Text) _text = new StreamWriter(_file, new UTF8Encoding(false)) { AutoFlush = true };
    }

    public string Path { get; }
    public RecordFormat Format { get; }

    /// <summary>Bytes written so far.</summary>
    public long Length
    {
        get { lock (_lock) return _file.CanWrite ? _file.Length : 0; }
    }

    /// <summary>Received bytes (a raw recording keeps them).</summary>
    public void Bytes(Chunk chunk)
    {
        if (Format != RecordFormat.Raw) return;
        lock (_lock)
        {
            _file.Write(chunk.Data);
            _file.Flush();
        }
    }

    /// <summary>A received line (a text recording keeps it).</summary>
    public void Line(DateTime time, string text) => WriteText(time, "  ", text);

    /// <summary>Something sent (a text recording marks it ">>").</summary>
    public void Sent(DateTime time, string text) => WriteText(time, ">>", text);

    private void WriteText(DateTime time, string mark, string text)
    {
        if (_text is null) return;
        lock (_lock) _text.WriteLine($"{time.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture)} {mark}{text}");
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _text?.Dispose();
            _file.Dispose();
        }
    }
}

/// <summary>What the terminal shows: lines of text, or every byte as hex.</summary>
public enum ViewMode { Text, Hex }

/// <summary>The choices the app remembers between runs.</summary>
public sealed class AppSettings
{
    public string? LastDevice { get; set; }
    public int BaudRate { get; set; } = 115200;
    public int DataBits { get; set; } = 8;
    public System.IO.Ports.Parity Parity { get; set; } = System.IO.Ports.Parity.None;
    public System.IO.Ports.StopBits StopBits { get; set; } = System.IO.Ports.StopBits.One;
    public System.IO.Ports.Handshake Handshake { get; set; } = System.IO.Ports.Handshake.None;
    public bool Dtr { get; set; } = true;
    public bool Rts { get; set; } = true;
    public TextEncoding Encoding { get; set; } = TextEncoding.Utf8;
    public LineEnding LineEnding { get; set; } = LineEnding.LF;
    public ViewMode View { get; set; } = ViewMode.Text;
    public bool Timestamps { get; set; } = true;
    public bool AutoScroll { get; set; } = true;
    public bool SendHex { get; set; }
    public bool LocalEcho { get; set; } = true;
    public int PlotWindow { get; set; } = 500;
    public bool PlotByTime { get; set; }
    public bool ShowPlot { get; set; } = true;
    public List<string> SendHistory { get; set; } = [];

    public SerialSettings Serial => new()
    {
        BaudRate = BaudRate, DataBits = DataBits, Parity = Parity, StopBits = StopBits, Handshake = Handshake, Dtr = Dtr, Rts = Rts,
    };

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };

    /// <summary>Where they are kept: AVASerMonitor/settings.json in the user's application data folder.</summary>
    public static string DefaultPath => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData, Environment.SpecialFolderOption.Create), "AVASerMonitor", "settings.json");

    /// <summary>The saved settings, or the defaults when there are none or they can't be read.</summary>
    public static AppSettings Load(string path)
    {
        try
        {
            if (File.Exists(path)) return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), Json) ?? new();
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException) { }
        return new();
    }

    /// <summary>Saves them; a failure costs only the preference.</summary>
    public void Save(string path)
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            var temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(this, Json));
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}
