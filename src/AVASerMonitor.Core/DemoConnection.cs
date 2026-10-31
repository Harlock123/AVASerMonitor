using System.Globalization;
using System.Text;

namespace AVASerMonitor.Core;

/// <summary>
/// The demo signal: an imaginary board printing three readings fifty times a second, as an Arduino sketch would with
/// Serial.print ("sine:0.71,triangle:-0.20,noise:0.03"), so the terminal and the plotter can be tried with nothing
/// plugged in. What it is sent comes back on a line of its own ("echo: …").
/// </summary>
public sealed class DemoConnection : IConnection
{
    public static readonly DeviceInfo Info = new(DeviceKind.Demo, "demo", "Demo signal", "an imaginary board printing sine, triangle and noise");

    private Timer? _timer;
    private readonly Lock _lock = new();
    private readonly Random _noise = new(1);
    private long _tick;

    public DeviceInfo Device => Info;

    /// <summary>Lines per second.</summary>
    public int Rate { get; init; } = 50;

    public bool IsOpen => _timer != null;

    public event Action<Chunk>? Received;
    public event Action<string>? Failed { add { } remove { } }

    public void Open()
    {
        if (_timer != null) return;
        _tick = 0;
        _timer = new Timer(_ => Tick(), null, 0, 1000 / Rate);
    }

    /// <summary>The line for tick <paramref name="n"/>: what the demo would print then.</summary>
    public string Line(long n)
    {
        var t = n / (double)Rate;
        var sine = Math.Sin(2 * Math.PI * 0.5 * t);
        var phase = t * 0.25 % 1;
        var triangle = 4 * Math.Abs(phase - 0.5) - 1;
        double noise;
        lock (_lock) noise = (_noise.NextDouble() - 0.5) * 0.2;
        return string.Create(CultureInfo.InvariantCulture, $"sine:{sine:0.000},triangle:{triangle:0.000},noise:{noise:0.000}\r\n");
    }

    private void Tick()
    {
        if (_timer == null) return;
        Received?.Invoke(new Chunk(DateTime.Now, Encoding.ASCII.GetBytes(Line(Interlocked.Increment(ref _tick)))));
    }

    public void Write(ReadOnlySpan<byte> data)
    {
        if (_timer == null) throw new ConnectionException("The demo signal isn't running.");
        var text = Encoding.UTF8.GetString(data).TrimEnd('\r', '\n');
        Received?.Invoke(new Chunk(DateTime.Now, Encoding.UTF8.GetBytes($"echo: {text}\r\n")));
    }

    public void Close()
    {
        var timer = _timer;
        _timer = null;
        timer?.Dispose();
    }

    public void Dispose() => Close();
}
