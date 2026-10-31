# AGENTS.md

Notes for anyone, human or AI agent, changing this code. `README.md` says what the app does; this says how the code
is put together and what keeps it working.

## What this is

AVASerMonitor is a serial monitor and live plotter in C# on .NET 10 with an Avalonia 12 window. It reads serial ports
(USB-serial adapters and boards included) and USB HID devices, shows what arrives as text or hex, plots the numbers in
it the way the Arduino Serial Plotter does, sends text or hex, and records to a file.

## Commands

```bash
dotnet build AVASerMonitor.slnx
dotnet test AVASerMonitor.slnx                 # headless; the window tests run on Avalonia's headless platform
dotnet run --project src/AVASerMonitor         # the app
AVASERMONITOR_SHOTS=docs/images dotnet test AVASerMonitor.slnx --filter "FullyQualifiedName~The_demo_signal"
                                               # redraws the README's screenshots
```

## Layout

- `src/AVASerMonitor.Core/`: everything but the window, with no Avalonia in it.
  - `Connections.cs` has `IConnection`, `DeviceInfo` and `Chunk`. `SerialConnection` (System.IO.Ports),
    `HidConnection` (HidSharp) and `DemoConnection` each read on a thread of their own and raise `Received`.
  - `DeviceCatalog` finds the devices: serial ports (named from sysfs on Linux), HID devices and the demo.
  - `Text.cs`: `LineAssembler` (bytes to lines), `Hex` (rows and parsing typed hex) and `Outgoing` (typed text to
    bytes, with line endings and escapes).
  - `Plotting.cs`: `PlotParser` (the Arduino Serial Plotter's rules) and `PlotData` (the rows, series and CSV).
  - `Recording.cs`: `Recorder` (text log or raw bytes) and `AppSettings` (JSON in the app-data folder).
  - `Monitor.cs` ties them together: bytes arrive on the connection's thread into a queue, and the window collects
    them with `Drain` on its own thread.
- `src/AVASerMonitor/`: the Avalonia app, built in code rather than XAML. `MainWindow` is the whole window (its
  controls are public so tests can drive them); `PlotView` draws the plot itself; `Look` has the colours.
- `tests/AVASerMonitor.Tests/`: xUnit v3 with Avalonia.Headless.XUnit. `PseudoTerminalTests` opens a real serial port
  on a pseudo-terminal pair (Linux and macOS), playing the device on the other end.

## Rules that keep it working

- **Nothing in Core touches a window.** Anything that can be tested without one goes in Core, with tests.
- **Connections raise events on their own thread.** Never touch a control from `Received` or `Failed`; queue it, as
  `Monitor` does, and handle it in `Drain` on the UI thread.
- **The plot is drawn by `PlotView` itself**, recording draw operations on the UI thread. Don't swap in a plotting
  library that renders on the render thread while the UI thread changes its data.
- **Errors say what to do.** A `ConnectionException`'s message is shown to the person as it is: name the device and
  the fix (the Linux group, the udev rule), never a bare exception.
- **Warnings are errors** (`TreatWarningsAsErrors`), and nullable reference types are on.

## CI and releases

`.github/workflows/build.yml` builds and tests every push, then publishes self-contained single-file builds for
win-x64/x86/arm64, linux-x64/arm64/arm and osx-x64/arm64. A push to `main` refreshes the rolling `latest`
pre-release; a `v*` tag makes a proper release. New repos use `main`: the release step only runs there.
