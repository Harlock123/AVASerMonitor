using System.IO.Ports;

namespace AVASerMonitor.Core;

/// <summary>How a serial line is set up: speed and framing, flow control, and the two control lines.</summary>
public sealed record SerialSettings
{
    public int BaudRate { get; init; } = 115200;
    public int DataBits { get; init; } = 8;
    public Parity Parity { get; init; } = Parity.None;
    public StopBits StopBits { get; init; } = StopBits.One;
    public Handshake Handshake { get; init; } = Handshake.None;

    /// <summary>
    /// Data Terminal Ready. On by default: many boards (Arduino Leonardo, Pico, ESP32 USB) only send once the host
    /// raises it, and an Uno resets when it rises, as the Arduino IDE's monitor does.
    /// </summary>
    public bool Dtr { get; init; } = true;

    public bool Rts { get; init; } = true;

    /// <summary>The speeds offered in the list; any other may be typed.</summary>
    public static readonly int[] CommonBaudRates = [300, 1200, 2400, 4800, 9600, 14400, 19200, 28800, 38400, 57600, 74880, 115200, 230400, 250000, 460800, 500000, 921600, 1000000, 2000000];

    /// <summary>Framing in the usual short form: 8N1, 7E2.</summary>
    public string Framing => $"{DataBits}{Parity switch { Parity.None => 'N', Parity.Even => 'E', Parity.Odd => 'O', Parity.Mark => 'M', _ => 'S' }}{StopBits switch { StopBits.One => "1", StopBits.OnePointFive => "1.5", _ => "2" }}";
}

/// <summary>
/// A serial port: a built-in COM port, or a USB-serial adapter or board (FTDI, CH340, CP210x, CDC ACM). Reading is a
/// loop on a thread of its own, so a slow window never makes the port drop bytes.
/// </summary>
public sealed class SerialConnection(DeviceInfo device, SerialSettings settings) : IConnection
{
    private SerialPort? _port;
    private Thread? _reader;
    private volatile bool _closing;
    private int _failed;

    public DeviceInfo Device { get; } = device;

    public SerialSettings Settings { get; private set; } = settings;

    public bool IsOpen => _port?.IsOpen == true && !_closing;

    public event Action<Chunk>? Received;
    public event Action<string>? Failed;

    public void Open()
    {
        if (IsOpen) return;
        var port = new SerialPort(Device.Id, Settings.BaudRate, Settings.Parity, Settings.DataBits, Settings.StopBits)
        {
            Handshake = Settings.Handshake,
            ReadTimeout = 200,
            WriteTimeout = 2000,
            ReadBufferSize = 1 << 16,
        };
        try
        {
            port.Open();
        }
        catch (UnauthorizedAccessException e)
        {
            port.Dispose();
            throw new ConnectionException(OperatingSystem.IsLinux()
                ? $"{Device.Id} is in use or you may not open it. On Linux, add yourself to the group that owns serial ports (dialout on Debian and Ubuntu, uucp on Arch) and log in again: sudo usermod -aG dialout $USER"
                : $"{Device.Id} is in use by another program, or you may not open it.", e);
        }
        catch (Exception e) when (e is IOException or ArgumentException or InvalidOperationException)
        {
            port.Dispose();
            throw new ConnectionException($"{Device.Id} couldn't be opened: {e.Message}", e);
        }
        _port = port;
        _closing = false;
        _failed = 0;
        // The control lines are set once the port is open: some drivers refuse them before.
        TrySet(() => port.DtrEnable = Settings.Dtr);
        TrySet(() => port.RtsEnable = Settings.Rts);
        _reader = new Thread(ReadLoop) { IsBackground = true, Name = $"read {Device.Id}" };
        _reader.Start();
    }

    /// <summary>Raises or drops DTR while open (a board reset, or a sketch waiting for it).</summary>
    public void SetDtr(bool on)
    {
        Settings = Settings with { Dtr = on };
        if (_port is { IsOpen: true } p) TrySet(() => p.DtrEnable = on);
    }

    /// <summary>Raises or drops RTS while open.</summary>
    public void SetRts(bool on)
    {
        Settings = Settings with { Rts = on };
        if (_port is { IsOpen: true } p) TrySet(() => p.RtsEnable = on);
    }

    /// <summary>Some ports (pseudo-terminals, a few adapters) have no control lines; that is not an error worth stopping for.</summary>
    private static void TrySet(Action set)
    {
        try { set(); }
        catch (Exception e) when (e is IOException or InvalidOperationException or UnauthorizedAccessException) { }
    }

    private void ReadLoop()
    {
        var buffer = new byte[16384];
        while (!_closing)
        {
            int n;
            try
            {
                n = _port!.BaseStream.Read(buffer, 0, buffer.Length);
            }
            catch (TimeoutException)
            {
                continue;
            }
            catch (Exception e) when (e is IOException or InvalidOperationException or ObjectDisposedException or UnauthorizedAccessException)
            {
                if (!_closing) Fail($"{Device.Id} stopped answering ({e.Message}). Was it unplugged?");
                return;
            }
            if (n <= 0)
            {
                if (_closing) return;
                Thread.Sleep(5);
                continue;
            }
            Received?.Invoke(new Chunk(DateTime.Now, buffer.AsSpan(0, n).ToArray()));
        }
    }

    public void Write(ReadOnlySpan<byte> data)
    {
        if (_port is not { IsOpen: true } port) throw new ConnectionException($"{Device.Id} isn't open.");
        try
        {
            port.BaseStream.Write(data);
            port.BaseStream.Flush();
        }
        catch (Exception e) when (e is IOException or TimeoutException or InvalidOperationException)
        {
            throw new ConnectionException($"Couldn't send to {Device.Id}: {e.Message}", e);
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
        var port = _port;
        _port = null;
        try { port?.Close(); }
        catch (IOException) { }
        port?.Dispose();
        if (_reader is { } t && t != Thread.CurrentThread) t.Join(1000);
        _reader = null;
    }

    public void Dispose() => Close();
}
