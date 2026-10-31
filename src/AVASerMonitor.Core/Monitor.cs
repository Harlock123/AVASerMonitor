using System.Collections.Concurrent;
using System.Text;

namespace AVASerMonitor.Core;

/// <summary>What a line of the terminal is: something received, something sent, or a note from the app.</summary>
public enum EntryKind { Received, Sent, Notice, Error }

/// <summary>One line of the terminal: a line of text, a row of hex (<paramref name="Hex"/>, never wrapped), or a note.</summary>
public readonly record struct Entry(DateTime Time, string Text, EntryKind Kind, bool Hex = false);

/// <summary>
/// One monitoring session: the open connection, what has arrived since the window last looked, and what it comes to.
/// Bytes arrive on the connection's thread and wait in a queue; the window collects them with <see cref="Drain"/> on
/// its own thread, as terminal lines (text or hex), plot rows and counts. Nothing here touches a window, so all of it
/// can be tested without one.
/// </summary>
public sealed class Monitor : IDisposable
{
    private readonly ConcurrentQueue<Chunk> _inbox = new();
    private readonly ConcurrentQueue<string> _failures = new();
    private long _hexOffset;
    private long _bytesIn, _bytesOut;

    public IConnection? Connection { get; private set; }

    public LineAssembler Lines { get; } = new();
    public PlotParser Parser { get; } = new();
    public PlotData Plot { get; } = new();

    /// <summary>What the terminal shows for what arrives from now on.</summary>
    public ViewMode View { get; set; } = ViewMode.Text;

    /// <summary>How long an unfinished line waits before it is shown anyway.</summary>
    public TimeSpan PartialLineWait { get; set; } = TimeSpan.FromMilliseconds(400);

    /// <summary>A recording under way, if any.</summary>
    public Recorder? Recorder { get; private set; }

    public long BytesIn => Interlocked.Read(ref _bytesIn);
    public long BytesOut => Interlocked.Read(ref _bytesOut);

    public bool IsOpen => Connection?.IsOpen == true;

    /// <summary>Opens a device in place of whatever was open. Throws <see cref="ConnectionException"/> saying why it can't.</summary>
    public void Open(DeviceInfo device, SerialSettings settings) => Open(DeviceCatalog.Connect(device, settings));

    public void Open(IConnection connection)
    {
        Close();
        connection.Received += OnReceived;
        connection.Failed += OnFailed;
        try
        {
            connection.Open();
        }
        catch
        {
            connection.Received -= OnReceived;
            connection.Failed -= OnFailed;
            connection.Dispose();
            throw;
        }
        Connection = connection;
        _hexOffset = 0;
        Parser.Reset();
    }

    public void Close()
    {
        var c = Connection;
        Connection = null;
        if (c is null) return;
        c.Received -= OnReceived;
        c.Failed -= OnFailed;
        c.Dispose();
    }

    private void OnReceived(Chunk chunk)
    {
        Interlocked.Add(ref _bytesIn, chunk.Data.Length);
        Recorder?.Bytes(chunk);
        _inbox.Enqueue(chunk);
    }

    private void OnFailed(string why) => _failures.Enqueue(why);

    /// <summary>What has happened since the last call: the terminal lines, how many plot rows were added, and whether the connection was lost (and why).</summary>
    public readonly record struct Drained(List<Entry> Entries, int PlotRows, string? Lost);

    /// <summary>Collects what has arrived. Call it from one thread (the window's).</summary>
    public Drained Drain(DateTime now)
    {
        var entries = new List<Entry>();
        var plotted = 0;
        while (_inbox.TryDequeue(out var chunk))
        {
            if (View == ViewMode.Hex)
            {
                foreach (var row in Hex.Rows(chunk.Data, _hexOffset)) entries.Add(new Entry(chunk.Time, row, EntryKind.Received, Hex: true));
                _hexOffset += chunk.Data.Length;
            }
            foreach (var line in Lines.Add(chunk)) plotted += Take(line, entries);
        }
        if (Lines.Flush(PartialLineWait, now) is { } partial) plotted += Take(partial, entries);
        string? lost = null;
        if (_failures.TryDequeue(out var why))
        {
            lost = why;
            entries.Add(new Entry(now, why, EntryKind.Error));
            Close();
        }
        return new Drained(entries, plotted, lost);
    }

    /// <summary>A line: shown (in text view), recorded, and plotted if it holds values.</summary>
    private int Take(LineAssembler.Line line, List<Entry> entries)
    {
        if (View == ViewMode.Text) entries.Add(new Entry(line.Time, line.Text, EntryKind.Received));
        Recorder?.Line(line.Time, line.Text);
        if (!line.Complete || Parser.Parse(line.Text) is not { Count: > 0 } values) return 0;
        Plot.Add(line.Time, values);
        return 1;
    }

    /// <summary>
    /// Sends what was typed (text with the line ending, or hex bytes), and returns the terminal line for it. Throws
    /// <see cref="ConnectionException"/> when nothing is open or it can't be sent, and <see cref="FormatException"/>
    /// for hex that isn't.
    /// </summary>
    public Entry Send(string text, bool asHex, LineEnding ending)
    {
        if (Connection is not { IsOpen: true } c) throw new ConnectionException("Nothing is open to send to. Connect first.");
        var bytes = Outgoing.Encode(text, asHex, ending);
        if (bytes.Length == 0) throw new FormatException("There's nothing to send.");
        c.Write(bytes);
        Interlocked.Add(ref _bytesOut, bytes.Length);
        var shown = asHex || c.Device.Kind == DeviceKind.UsbHid ? string.Join(' ', bytes.Select(b => b.ToString("X2"))) : text;
        var now = DateTime.Now;
        Recorder?.Sent(now, shown);
        return new Entry(now, shown, EntryKind.Sent);
    }

    /// <summary>Starts recording to a file, ending any recording already under way.</summary>
    public void StartRecording(string path, RecordFormat format)
    {
        StopRecording();
        Recorder = new Recorder(path, format);
    }

    public void StopRecording()
    {
        var r = Recorder;
        Recorder = null;
        r?.Dispose();
    }

    /// <summary>Starts the counts again.</summary>
    public void ResetCounts()
    {
        Interlocked.Exchange(ref _bytesIn, 0);
        Interlocked.Exchange(ref _bytesOut, 0);
    }

    /// <summary>A short note of the counts: "12.4 KB in, 36 B out".</summary>
    public string Counts => $"{Size(BytesIn)} in, {Size(BytesOut)} out";

    public static string Size(long bytes) =>
        bytes < 1024 ? $"{bytes} B" : bytes < 1024 * 1024 ? $"{bytes / 1024.0:0.0} KB" : $"{bytes / (1024.0 * 1024):0.0} MB";

    public void Dispose()
    {
        Close();
        StopRecording();
    }
}
