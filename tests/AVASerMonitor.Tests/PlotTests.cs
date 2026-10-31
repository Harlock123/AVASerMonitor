using AVASerMonitor.Core;

namespace AVASerMonitor.Tests;

public class PlotTests
{
    private static string Shape(IReadOnlyList<(string Name, double Value)>? values) =>
        values is null ? "text" : string.Join(" ", values.Select(v => $"{v.Name}={v.Value}"));

    [Theory]
    [InlineData("12.5, 30, -4", "1=12.5 2=30 3=-4")]
    [InlineData("12.5 30\t-4", "1=12.5 2=30 3=-4")]
    [InlineData("1e3;2.5E-2", "1=1000 2=0.025")]
    [InlineData("temp:21.5 humidity:40", "temp=21.5 humidity=40")]
    [InlineData("temp=21.5,humidity=40", "temp=21.5 humidity=40")]
    [InlineData("temp: 21.5, humidity : 40", "temp=21.5 humidity=40")]
    [InlineData("sine:0.707,triangle:-0.200,noise:0.031", "sine=0.707 triangle=-0.2 noise=0.031")]
    [InlineData("Temperature is 23.5 C", "text")]
    [InlineData("temp:warm", "text")]
    [InlineData("ready", "text")]
    [InlineData("", "text")]
    [InlineData("NaN", "text")]
    public void Lines_plot_the_way_the_Arduino_Serial_Plotter_reads_them(string line, string expected) =>
        Assert.Equal(expected, Shape(new PlotParser().Parse(line)));

    [Fact]
    public void A_header_line_names_the_plain_columns_after_it()
    {
        var p = new PlotParser();
        Assert.Null(p.Parse("voltage,current"));
        Assert.Equal(["voltage", "current"], p.Header);
        Assert.Equal("voltage=5 current=0.2 3=7", Shape(p.Parse("5,0.2,7")));
        // A single word is not a header: it could be "ready" or "OK".
        var q = new PlotParser();
        q.Parse("ready");
        Assert.Null(q.Header);
        p.Reset();
        Assert.Equal("1=5", Shape(p.Parse("5")));
    }

    [Fact]
    public void Plot_data_keeps_the_series_gives_a_window_of_them_and_exports_CSV()
    {
        var d = new PlotData { Capacity = 5 };
        var added = new List<string>();
        d.SeriesAdded += added.Add;
        var t0 = new DateTime(2026, 10, 31, 12, 0, 0);
        for (var i = 0; i < 8; i++)
            d.Add(t0.AddMilliseconds(100 * i), i % 2 == 0 ? [("a", i), ("b", -i)] : [("a", i)]);
        Assert.Equal(["a", "b"], added);
        Assert.Equal(5, d.Rows.Count);
        Assert.Equal(8, d.Count);

        var (x, y) = d.Series("a", last: 3);
        Assert.Equal([5.0, 6, 7], x);
        Assert.Equal([5.0, 6, 7], y);
        var (bx, by) = d.Series("b");
        Assert.Equal([4.0, 6], bx);
        Assert.Equal([-4.0, -6], by);
        var (tx, _) = d.Series("a", byTime: true);
        // Seconds since the first row arrived, the dropped ones included.
        Assert.Equal(0.3, tx[0], 9);
        Assert.Equal(0.7, tx[^1], 9);

        var csv = d.ToCsv().Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.TrimEnd('\r')).ToList();
        Assert.Equal("time,seconds,a,b", csv[0]);
        Assert.Equal("2026-10-31 12:00:00.300,0.300,3,", csv[1]);
        Assert.Equal("2026-10-31 12:00:00.400,0.400,4,-4", csv[2]);
        d.Clear();
        Assert.Empty(d.Names);
        Assert.Null(d.Start);
    }

    [Fact]
    public void Plot_ticks_and_numbers_read_well()
    {
        Assert.Equal([0.0, 0.2, 0.4, 0.6, 0.8, 1.0], PlotView.Ticks(0, 1));
        Assert.Equal([-10.0, -5, 0, 5, 10], PlotView.Ticks(-12, 12));
        Assert.Empty(PlotView.Ticks(3, 3));
        Assert.Equal("1.5k", PlotView.Format(1500));
        Assert.Equal("0.25", PlotView.Format(0.25));
        Assert.Equal("5m", PlotView.Format(0.005));
        Assert.Equal("12", PlotView.Format(12));
        Assert.Equal("0", PlotView.Format(0));
    }
}
