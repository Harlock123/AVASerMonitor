namespace AVASerMonitor.Core;

/// <summary>What a device is: a serial port (USB-serial adapters and boards included), a USB HID device read directly,
/// or the built-in demo signal.</summary>
public enum DeviceKind { Serial, UsbHid, Demo }

/// <summary>
/// A device that can be opened. <see cref="Id"/> is what opens it again (a port name such as COM3 or /dev/ttyACM0, a
/// HID device's path, or "demo"); <see cref="Name"/> and <see cref="Detail"/> are for people.
/// </summary>
public sealed record DeviceInfo(DeviceKind Kind, string Id, string Name, string Detail, int? VendorId = null, int? ProductId = null)
{
    /// <summary>USB vendor and product IDs as VVVV:PPPP, when known.</summary>
    public string? UsbId => VendorId is { } v && ProductId is { } p ? $"{v:X4}:{p:X4}" : null;

    /// <summary>One line for a device list: the name, then what it is.</summary>
    public string Label => Detail.Length > 0 ? $"{Name}  —  {Detail}" : Name;

    public override string ToString() => Label;
}

/// <summary>Some bytes that arrived, and when.</summary>
public readonly record struct Chunk(DateTime Time, byte[] Data);

/// <summary>
/// An open line to a device. What arrives is raised on a worker thread, in order, as <see cref="Received"/>; a
/// connection that breaks (a cable pulled, a device reset) raises <see cref="Failed"/> once and closes itself.
/// </summary>
public interface IConnection : IDisposable
{
    DeviceInfo Device { get; }

    bool IsOpen { get; }

    /// <summary>Bytes arrived. Raised on the connection's own thread.</summary>
    event Action<Chunk>? Received;

    /// <summary>The connection broke, saying why. Raised once, on the connection's own thread; it is closed after.</summary>
    event Action<string>? Failed;

    /// <summary>Opens it, or throws <see cref="ConnectionException"/> saying why it can't be.</summary>
    void Open();

    /// <summary>Sends bytes. For a HID device they are one output report.</summary>
    void Write(ReadOnlySpan<byte> data);

    void Close();
}

/// <summary>A device couldn't be opened or written to; the message says why, and what might fix it.</summary>
public sealed class ConnectionException(string message, Exception? inner = null) : Exception(message, inner);
