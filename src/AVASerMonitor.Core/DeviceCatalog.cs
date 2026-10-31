using System.Globalization;
using System.IO.Ports;
using HidSharp;

namespace AVASerMonitor.Core;

/// <summary>
/// Finds the devices that can be opened: the serial ports (with the USB maker and product of an adapter or board,
/// where the system says), the USB HID devices, and the demo signal.
/// </summary>
public static class DeviceCatalog
{
    /// <summary>Every device, serial ports first, then HID devices, then the demo.</summary>
    public static IReadOnlyList<DeviceInfo> All(bool includeHid = true) =>
        [.. SerialPorts(), .. includeHid ? HidDevices() : [], DemoConnection.Info];

    /// <summary>The serial ports, named for what is on them where that can be found.</summary>
    public static IReadOnlyList<DeviceInfo> SerialPorts()
    {
        string[] names;
        try { names = SerialPort.GetPortNames(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or PlatformNotSupportedException) { names = []; }
        var ports = names.Distinct(StringComparer.Ordinal).ToList();
        // macOS lists each port twice: tty.* (waits for carrier) and cu.* (doesn't). cu.* is the one to use.
        if (OperatingSystem.IsMacOS()) ports.RemoveAll(p => p.StartsWith("/dev/tty.", StringComparison.Ordinal) && ports.Contains("/dev/cu." + p["/dev/tty.".Length..]));
        return ports.Select(Describe).OrderBy(d => d.VendorId is null ? 1 : 0).ThenBy(d => d.Id, NaturalOrder.Instance).ToList();
    }

    private static DeviceInfo Describe(string port)
    {
        if (OperatingSystem.IsLinux() && LinuxUsb(port) is { } usb)
            return new DeviceInfo(DeviceKind.Serial, port, port, usb.Description, usb.Vendor, usb.Product);
        var detail = OperatingSystem.IsMacOS() ? MacGuess(port)
            : OperatingSystem.IsLinux() ? LinuxGuess(port)
            : "serial port";
        return new DeviceInfo(DeviceKind.Serial, port, port, detail);
    }

    private static string LinuxGuess(string port) =>
        Path.GetFileName(port) switch
        {
            var n when n.StartsWith("ttyACM", StringComparison.Ordinal) => "USB serial (CDC ACM)",
            var n when n.StartsWith("ttyUSB", StringComparison.Ordinal) => "USB-serial adapter",
            var n when n.StartsWith("ttyAMA", StringComparison.Ordinal) || n.StartsWith("serial", StringComparison.Ordinal) => "on-board UART",
            _ => "serial port",
        };

    private static string MacGuess(string port) =>
        port.Contains("usbmodem", StringComparison.Ordinal) ? "USB serial (CDC ACM)"
        : port.Contains("usbserial", StringComparison.Ordinal) || port.Contains("wchusbserial", StringComparison.Ordinal) || port.Contains("SLAB", StringComparison.Ordinal) ? "USB-serial adapter"
        : port.Contains("Bluetooth", StringComparison.Ordinal) ? "Bluetooth serial"
        : "serial port";

    /// <summary>
    /// A Linux port's USB device, read from sysfs: /sys/class/tty/NAME/device leads into the USB interface, and the
    /// device above it has idVendor, idProduct, manufacturer and product. Null for a port that isn't on USB.
    /// </summary>
    internal static (int Vendor, int Product, string Description)? LinuxUsb(string port, string sysRoot = "/sys")
    {
        try
        {
            var device = Path.Combine(sysRoot, "class", "tty", Path.GetFileName(port), "device");
            if (!Directory.Exists(device)) return null;
            var dir = new DirectoryInfo(device).ResolveLinkTarget(true) as DirectoryInfo ?? new DirectoryInfo(device);
            for (var d = dir; d != null && d.FullName.Length > sysRoot.Length; d = d.Parent)
            {
                var vendor = Read(d, "idVendor");
                var product = Read(d, "idProduct");
                if (vendor is null || product is null) continue;
                var name = string.Join(" ", new[] { Read(d, "manufacturer"), Read(d, "product") }.Where(s => !string.IsNullOrWhiteSpace(s)));
                var v = int.Parse(vendor, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                var p = int.Parse(product, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                return (v, p, $"{(name.Length > 0 ? name : "USB serial")} ({v:X4}:{p:X4})");
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or FormatException) { }
        return null;
    }

    private static string? Read(DirectoryInfo dir, string file)
    {
        var path = Path.Combine(dir.FullName, file);
        return File.Exists(path) ? File.ReadAllText(path).Trim() : null;
    }

    /// <summary>The USB HID devices, each by its product name, maker and IDs.</summary>
    public static IReadOnlyList<DeviceInfo> HidDevices()
    {
        IEnumerable<HidDevice> devices;
        try { devices = DeviceList.Local.GetHidDevices().ToList(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or PlatformNotSupportedException or TypeInitializationException or DllNotFoundException) { return []; }
        var list = new List<DeviceInfo>();
        foreach (var d in devices)
        {
            string name = Safe(d.GetProductName), maker = Safe(d.GetManufacturer);
            if (name.Length == 0) name = "HID device";
            var detail = $"USB HID{(maker.Length > 0 ? $", {maker}" : "")} ({d.VendorID:X4}:{d.ProductID:X4})";
            list.Add(new DeviceInfo(DeviceKind.UsbHid, d.DevicePath, name, detail, d.VendorID, d.ProductID));
        }
        return list.OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static string Safe(Func<string> read)
    {
        try { return read()?.Trim() ?? ""; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException or InvalidOperationException) { return ""; }
    }

    /// <summary>A connection for a device, not yet open.</summary>
    public static IConnection Connect(DeviceInfo device, SerialSettings settings) => device.Kind switch
    {
        DeviceKind.Serial => new SerialConnection(device, settings),
        DeviceKind.UsbHid => new HidConnection(device),
        _ => new DemoConnection(),
    };

    /// <summary>COM2 before COM10, ttyUSB2 before ttyUSB10.</summary>
    private sealed class NaturalOrder : IComparer<string>
    {
        public static readonly NaturalOrder Instance = new();

        public int Compare(string? a, string? b)
        {
            static (string, long) Split(string s)
            {
                var i = s.Length;
                while (i > 0 && char.IsAsciiDigit(s[i - 1])) i--;
                return (s[..i], i < s.Length && long.TryParse(s[i..], out var n) ? n : -1);
            }
            var (pa, na) = Split(a ?? "");
            var (pb, nb) = Split(b ?? "");
            var c = string.CompareOrdinal(pa, pb);
            return c != 0 ? c : na.CompareTo(nb);
        }
    }
}
