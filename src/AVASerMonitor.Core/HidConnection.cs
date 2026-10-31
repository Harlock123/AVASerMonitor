using HidSharp;

namespace AVASerMonitor.Core;

/// <summary>
/// A USB HID device read directly: each input report it sends arrives as one chunk (its first byte the report ID,
/// 0 when the device uses none), and what is written goes out as one output report.
/// </summary>
public sealed class HidConnection(DeviceInfo device) : IConnection
{
    private HidStream? _stream;
    private Thread? _reader;
    private volatile bool _closing;
    private int _failed;

    public DeviceInfo Device { get; } = device;

    public bool IsOpen => _stream != null && !_closing;

    /// <summary>The largest output report the device takes, report ID included (0 when it takes none).</summary>
    public int MaxOutputReportLength { get; private set; }

    public event Action<Chunk>? Received;
    public event Action<string>? Failed;

    public void Open()
    {
        if (IsOpen) return;
        var hid = DeviceList.Local.GetHidDevices().FirstOrDefault(d => d.DevicePath == Device.Id)
            ?? throw new ConnectionException($"{Device.Name} isn't connected any more. Refresh the list.");
        if (!hid.TryOpen(out var stream))
            throw new ConnectionException(OperatingSystem.IsLinux()
                ? $"{Device.Name} couldn't be opened. On Linux, reading a HID device directly needs a udev rule giving you its hidraw node, e.g. a file /etc/udev/rules.d/70-avasermonitor.rules with: KERNEL==\"hidraw*\", ATTRS{{idVendor}}==\"{Device.VendorId:x4}\", ATTRS{{idProduct}}==\"{Device.ProductId:x4}\", MODE=\"0660\", TAG+=\"uaccess\""
                : $"{Device.Name} couldn't be opened: another program may have it, or the system keeps it (keyboards and mice usually are).");
        stream.ReadTimeout = Timeout.Infinite;
        try { MaxOutputReportLength = hid.GetMaxOutputReportLength(); }
        catch (Exception e) when (e is NotSupportedException or IOException) { MaxOutputReportLength = 0; }
        _stream = stream;
        _closing = false;
        _failed = 0;
        _reader = new Thread(ReadLoop) { IsBackground = true, Name = $"read {Device.Name}" };
        _reader.Start();
    }

    private void ReadLoop()
    {
        var buffer = new byte[Math.Max(65, SafeInputLength())];
        while (!_closing)
        {
            int n;
            try
            {
                n = _stream!.Read(buffer, 0, buffer.Length);
            }
            catch (TimeoutException)
            {
                continue;
            }
            catch (Exception e) when (e is IOException or ObjectDisposedException or InvalidOperationException or NullReferenceException)
            {
                if (!_closing) Fail($"{Device.Name} stopped answering ({e.Message}). Was it unplugged?");
                return;
            }
            if (n > 0) Received?.Invoke(new Chunk(DateTime.Now, buffer.AsSpan(0, n).ToArray()));
        }
    }

    private int SafeInputLength()
    {
        try { return _stream?.Device.GetMaxInputReportLength() ?? 0; }
        catch (Exception e) when (e is NotSupportedException or IOException) { return 0; }
    }

    /// <summary>
    /// Sends one output report: the bytes given, padded with zeros to the device's report length. The first byte is
    /// the report ID (0 for a device that uses none).
    /// </summary>
    public void Write(ReadOnlySpan<byte> data)
    {
        if (_stream is not { } stream) throw new ConnectionException($"{Device.Name} isn't open.");
        if (MaxOutputReportLength <= 0) throw new ConnectionException($"{Device.Name} takes no output reports: there's nothing it can be sent.");
        if (data.Length > MaxOutputReportLength)
            throw new ConnectionException($"An output report for {Device.Name} is at most {MaxOutputReportLength} bytes, the report ID first; that was {data.Length}.");
        var report = new byte[MaxOutputReportLength];
        data.CopyTo(report);
        try
        {
            stream.Write(report);
        }
        catch (Exception e) when (e is IOException or TimeoutException or InvalidOperationException)
        {
            throw new ConnectionException($"Couldn't send to {Device.Name}: {e.Message}", e);
        }
    }

    private void Fail(string why)
    {
        if (Interlocked.Exchange(ref _failed, 1) == 1) return;
        Failed?.Invoke(why);
        Close();
    }

    public void Close()
    {
        _closing = true;
        var stream = _stream;
        _stream = null;
        try { stream?.Dispose(); }
        catch (IOException) { }
        if (_reader is { } t && t != Thread.CurrentThread) t.Join(1000);
        _reader = null;
    }

    public void Dispose() => Close();
}
