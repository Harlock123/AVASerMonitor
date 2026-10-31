using System.Runtime.InteropServices;
using System.Text;
using AVASerMonitor.Core;

namespace AVASerMonitor.Tests;

/// <summary>A connection the test drives: it delivers what the test gives it and keeps what it is sent.</summary>
internal sealed class FakeConnection(DeviceInfo? device = null) : IConnection
{
    public DeviceInfo Device { get; } = device ?? new DeviceInfo(DeviceKind.Serial, "/dev/fake0", "/dev/fake0", "a test");
    public bool IsOpen { get; private set; }
    public List<byte[]> Written { get; } = [];
    public bool Disposed { get; private set; }
    public event Action<Chunk>? Received;
    public event Action<string>? Failed;
    public void Open() => IsOpen = true;
    public void Write(ReadOnlySpan<byte> data) => Written.Add(data.ToArray());
    public void Close() => IsOpen = false;
    public void Dispose() { Close(); Disposed = true; }
    public void Arrive(string text) => Received?.Invoke(new Chunk(DateTime.Now, Encoding.UTF8.GetBytes(text)));
    public void Break(string why) => Failed?.Invoke(why);
}

public class ConnectionTests
{
    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public void The_monitor_turns_bytes_into_lines_plot_rows_and_counts()
    {
        using var m = new Core.Monitor();
        var fake = new FakeConnection();
        m.Open(fake);
        fake.Arrive("hello\r\nsine:0.5,cos:1\r\n12,");
        fake.Arrive("13\r\n");
        var d = m.Drain(DateTime.Now);
        Assert.Equal(["hello", "sine:0.5,cos:1", "12,13"], d.Entries.Select(e => e.Text));
        Assert.All(d.Entries, e => Assert.Equal(EntryKind.Received, e.Kind));
        Assert.Equal(2, d.PlotRows);
        Assert.Equal(["sine", "cos", "1", "2"], m.Plot.Names);
        Assert.Equal(30, m.BytesIn);

        // In hex view the bytes come as rows; lines are still plotted.
        m.View = ViewMode.Hex;
        fake.Arrive("7\n");
        d = m.Drain(DateTime.Now);
        var row = Assert.Single(d.Entries).Text;
        Assert.StartsWith("0000  37 0A ", row);
        Assert.EndsWith("|7.|", row);
        Assert.Equal(1, d.PlotRows);
    }

    [Fact]
    public void Sending_writes_the_bytes_and_says_what_went()
    {
        using var m = new Core.Monitor();
        Assert.Contains("Connect first", Assert.Throws<ConnectionException>(() => m.Send("x", false, LineEnding.LF)).Message);
        var fake = new FakeConnection();
        m.Open(fake);
        var sent = m.Send("AT", false, LineEnding.CRLF);
        Assert.Equal("AT\r\n"u8.ToArray(), fake.Written[0]);
        Assert.Equal(("AT", EntryKind.Sent), (sent.Text, sent.Kind));
        Assert.Equal("01 FF", m.Send("01 ff", true, LineEnding.CRLF).Text);
        Assert.Equal(new byte[] { 1, 255 }, fake.Written[1]);
        Assert.Equal(6, m.BytesOut);
        Assert.Throws<FormatException>(() => m.Send("", true, LineEnding.None));
    }

    [Fact]
    public void A_broken_connection_is_reported_once_and_closed()
    {
        using var m = new Core.Monitor();
        var fake = new FakeConnection();
        m.Open(fake);
        fake.Arrive("last words\n");
        fake.Break("/dev/fake0 stopped answering. Was it unplugged?");
        var d = m.Drain(DateTime.Now);
        Assert.Equal("last words", d.Entries[0].Text);
        Assert.Equal(EntryKind.Error, d.Entries[^1].Kind);
        Assert.Contains("unplugged", d.Lost);
        Assert.False(m.IsOpen);
        Assert.True(fake.Disposed);
    }

    [Fact]
    public void A_recording_keeps_lines_with_times_or_the_raw_bytes()
    {
        var folder = Directory.CreateTempSubdirectory("avasermonitor-").FullName;
        try
        {
            using var m = new Core.Monitor();
            var fake = new FakeConnection();
            m.Open(fake);
            m.StartRecording(Path.Combine(folder, "log.txt"), RecordFormat.Text);
            fake.Arrive("one\ntwo\n");
            m.Drain(DateTime.Now);
            m.Send("go", false, LineEnding.LF);
            m.StartRecording(Path.Combine(folder, "raw.bin"), RecordFormat.Raw);
            fake.Arrive("\x01\x02three\n");
            m.Drain(DateTime.Now);
            m.StopRecording();

            var log = File.ReadAllLines(Path.Combine(folder, "log.txt"));
            Assert.Equal(3, log.Length);
            Assert.Matches(@"^\d{4}-\d\d-\d\d \d\d:\d\d:\d\d\.\d{3}   one$", log[0]);
            Assert.EndsWith(" >>go", log[2]);
            Assert.Equal("\x01\x02three\n"u8.ToArray(), File.ReadAllBytes(Path.Combine(folder, "raw.bin")));
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    [Fact]
    public async Task The_demo_prints_three_values_and_echoes_what_it_is_sent()
    {
        using var m = new Core.Monitor();
        m.Open(DemoConnection.Info, new SerialSettings());
        m.Send("hello", false, LineEnding.LF);
        var entries = new List<Entry>();
        for (var i = 0; i < 100 && m.Plot.Rows.Count < 10; i++)
        {
            await Task.Delay(20, Cancel);
            entries.AddRange(m.Drain(DateTime.Now).Entries);
        }
        Assert.True(m.Plot.Rows.Count >= 10);
        Assert.Equal(["sine", "triangle", "noise"], m.Plot.Names);
        Assert.Contains(entries, e => e.Text == "echo: hello");
        var demo = new DemoConnection();
        Assert.Equal("sine:1.000,triangle:0.500,noise:", demo.Line(25)[..32]);
    }

    [Fact]
    public void Linux_finds_a_USB_serial_ports_maker_and_product_in_sysfs()
    {
        var root = Directory.CreateTempSubdirectory("sysfs-").FullName;
        try
        {
            // /sys/class/tty/ttyACM0/device -> the USB interface, under the USB device that has the IDs.
            var usb = Path.Combine(root, "devices", "pci0", "usb1", "1-2");
            var iface = Path.Combine(usb, "1-2:1.0");
            Directory.CreateDirectory(iface);
            File.WriteAllText(Path.Combine(usb, "idVendor"), "2341\n");
            File.WriteAllText(Path.Combine(usb, "idProduct"), "0043\n");
            File.WriteAllText(Path.Combine(usb, "manufacturer"), "Arduino (www.arduino.cc)\n");
            File.WriteAllText(Path.Combine(usb, "product"), "Uno R3\n");
            var tty = Path.Combine(root, "class", "tty", "ttyACM0");
            Directory.CreateDirectory(tty);
            Directory.CreateSymbolicLink(Path.Combine(tty, "device"), iface);
            Directory.CreateDirectory(Path.Combine(root, "class", "tty", "ttyS0"));

            var found = DeviceCatalog.LinuxUsb("/dev/ttyACM0", root);
            Assert.Equal((0x2341, 0x0043, "Arduino (www.arduino.cc) Uno R3 (2341:0043)"), found);
            Assert.Null(DeviceCatalog.LinuxUsb("/dev/ttyS0", root));
            Assert.Null(DeviceCatalog.LinuxUsb("/dev/ttyUSB9", root));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void The_catalog_always_offers_the_demo_and_connects_each_kind()
    {
        var all = DeviceCatalog.All(includeHid: false);
        Assert.Equal(DemoConnection.Info, all[^1]);
        Assert.IsType<SerialConnection>(DeviceCatalog.Connect(new DeviceInfo(DeviceKind.Serial, "COM9", "COM9", ""), new SerialSettings()));
        Assert.IsType<HidConnection>(DeviceCatalog.Connect(new DeviceInfo(DeviceKind.UsbHid, "/dev/hidraw9", "Pad", ""), new SerialSettings()));
        Assert.IsType<DemoConnection>(DeviceCatalog.Connect(DemoConnection.Info, new SerialSettings()));
        Assert.Equal("8N1", new SerialSettings().Framing);
        Assert.Equal("7E2", new SerialSettings { DataBits = 7, Parity = System.IO.Ports.Parity.Even, StopBits = System.IO.Ports.StopBits.Two }.Framing);
    }

    [Fact]
    public void Opening_a_port_that_isnt_there_says_so()
    {
        var port = new SerialConnection(new DeviceInfo(DeviceKind.Serial, OperatingSystem.IsWindows() ? "COM250" : "/dev/ttyNOSUCH9", "x", ""), new SerialSettings());
        var e = Assert.Throws<ConnectionException>(port.Open);
        Assert.Contains(port.Device.Id, e.Message);
        Assert.False(port.IsOpen);
    }

    [Fact]
    public void Settings_are_kept_between_runs()
    {
        var path = Path.Combine(Directory.CreateTempSubdirectory("avasermonitor-").FullName, "settings.json");
        var s = new AppSettings { LastDevice = "/dev/ttyACM0", BaudRate = 9600, Parity = System.IO.Ports.Parity.Odd, View = ViewMode.Hex, SendHistory = ["AT"] };
        s.Save(path);
        var back = AppSettings.Load(path);
        Assert.Equal(("/dev/ttyACM0", 9600, System.IO.Ports.Parity.Odd, ViewMode.Hex, "AT"), (back.LastDevice, back.BaudRate, back.Parity, back.View, back.SendHistory.Single()));
        Assert.Contains("\"Odd\"", File.ReadAllText(path));
        File.WriteAllText(path, "{ not json");
        Assert.Equal(115200, AppSettings.Load(path).BaudRate);
        Directory.Delete(Path.GetDirectoryName(path)!, true);
    }
}

/// <summary>
/// A real serial port with no hardware: on Linux and macOS a pseudo-terminal pair stands in for a cable, the test
/// playing the device on one end while <see cref="SerialConnection"/> opens the other by its name.
/// </summary>
public class PseudoTerminalTests
{
    [DllImport("libc", SetLastError = true)]
    private static extern int posix_openpt(int flags);

    [DllImport("libc", SetLastError = true)]
    private static extern int grantpt(int fd);

    [DllImport("libc", SetLastError = true)]
    private static extern int unlockpt(int fd);

    [DllImport("libc", SetLastError = true)]
    private static extern IntPtr ptsname(int fd);

    [DllImport("libc", SetLastError = true)]
    private static extern nint write(int fd, byte[] buffer, nint count);

    [DllImport("libc", SetLastError = true)]
    private static extern nint read(int fd, byte[] buffer, nint count);

    [DllImport("libc", SetLastError = true)]
    private static extern int close(int fd);

    [Fact]
    public async Task A_serial_port_reads_what_the_device_prints_and_sends_what_is_typed()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux() || OperatingSystem.IsMacOS(), "Pseudo-terminals are a Unix thing.");
        const int ReadWrite = 2;
        var noControllingTerminal = OperatingSystem.IsMacOS() ? 0x20000 : 0x100;
        var device = posix_openpt(ReadWrite | noControllingTerminal);
        Assert.True(device >= 0, "No pseudo-terminal.");
        try
        {
            Assert.Equal(0, grantpt(device));
            Assert.Equal(0, unlockpt(device));
            var name = Marshal.PtrToStringAnsi(ptsname(device))!;

            using var m = new Core.Monitor();
            m.Open(new DeviceInfo(DeviceKind.Serial, name, name, "a pseudo-terminal"), new SerialSettings { BaudRate = 115200 });
            Assert.True(m.IsOpen);

            var printed = "temp:21.5,humidity:40\r\nhello from the board\r\n"u8.ToArray();
            write(device, printed, printed.Length);
            var entries = new List<Entry>();
            for (var i = 0; i < 200 && entries.Count < 2; i++)
            {
                await Task.Delay(10, TestContext.Current.CancellationToken);
                entries.AddRange(m.Drain(DateTime.Now).Entries);
            }
            Assert.Equal(["temp:21.5,humidity:40", "hello from the board"], entries.Select(e => e.Text));
            Assert.Equal(["temp", "humidity"], m.Plot.Names);

            m.Send("LED ON", false, LineEnding.LF);
            var got = new byte[64];
            var n = (int)read(device, got, got.Length);
            Assert.Equal("LED ON\n", Encoding.ASCII.GetString(got, 0, n));
            m.Close();
        }
        finally
        {
            close(device);
        }
    }
}
