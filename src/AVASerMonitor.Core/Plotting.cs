using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace AVASerMonitor.Core;

/// <summary>
/// Reads plot values out of lines of text, the way the Arduino IDE's Serial Plotter does, so a sketch written for that
/// plots here unchanged:
/// <list type="bullet">
/// <item>numbers separated by commas, spaces, tabs or semicolons ("12.5, 30, -4"), one series each, named 1, 2, 3…;</item>
/// <item>named values ("temp:21.5 humidity:40" or "temp=21.5,humidity=40"), a series for each name;</item>
/// <item>a line of names alone ("temp,humidity") names the plain columns that follow, as a CSV header does.</item>
/// </list>
/// A line with anything else in it (a word, a unit, a sentence) is text only, and isn't plotted.
/// </summary>
public sealed partial class PlotParser
{
    private string[]? _header;

    /// <summary>The column names a header line gave, if any.</summary>
    public IReadOnlyList<string>? Header => _header;

    public void Reset() => _header = null;

    /// <summary>The values on a line, in order, or null when it isn't plot data (a header line is remembered, and gives null).</summary>
    public IReadOnlyList<(string Name, double Value)>? Parse(string line)
    {
        var text = Pair().Replace(line.Trim(), ":");
        if (text.Length == 0) return null;
        var tokens = Separators().Split(text).Where(t => t.Length > 0).ToArray();
        if (tokens.Length == 0) return null;

        var values = new List<(string, double)>();
        var plain = 0;
        foreach (var token in tokens)
        {
            var colon = token.IndexOf(':');
            if (colon > 0)
            {
                if (!Number(token[(colon + 1)..], out var v)) return HeaderOrNothing(tokens);
                values.Add((token[..colon], v));
            }
            else if (Number(token, out var v))
            {
                values.Add((_header is { } h && plain < h.Length ? h[plain] : (plain + 1).ToString(CultureInfo.InvariantCulture), v));
                plain++;
            }
            else
            {
                return HeaderOrNothing(tokens);
            }
        }
        return values;
    }

    /// <summary>
    /// A line of two or more names alone is a header for the plain columns; anything else is just text. (A single word
    /// could be anything, "ready" or "OK", so it names nothing.)
    /// </summary>
    private IReadOnlyList<(string, double)>? HeaderOrNothing(string[] tokens)
    {
        if (tokens.Length is >= 2 and <= 32 && tokens.All(t => Name().IsMatch(t))) _header = tokens;
        return null;
    }

    private static bool Number(string s, out double v) =>
        double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v) && double.IsFinite(v);

    [GeneratedRegex(@"\s*[:=]\s*")]
    private static partial Regex Pair();

    [GeneratedRegex(@"[,;\t ]+")]
    private static partial Regex Separators();

    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_.\-/%()°]*$")]
    private static partial Regex Name();
}

/// <summary>
/// Everything plotted so far: one row per line of values, with when it arrived, and the series named in the order they
/// first appeared. Kept to <see cref="Capacity"/> rows, the oldest dropped first. Used on one thread.
/// </summary>
public sealed class PlotData
{
    private readonly List<Row> _rows = [];
    private readonly List<string> _names = [];
    private long _dropped;

    public readonly record struct Row(long Index, DateTime Time, IReadOnlyList<(string Name, double Value)> Values);

    /// <summary>The most rows kept.</summary>
    public int Capacity { get; init; } = 200_000;

    public IReadOnlyList<Row> Rows => _rows;

    /// <summary>The series, in the order they first appeared.</summary>
    public IReadOnlyList<string> Names => _names;

    /// <summary>When the first row kept arrived.</summary>
    public DateTime? Start { get; private set; }

    /// <summary>How many rows have arrived since the start, counting any dropped.</summary>
    public long Count => _dropped + _rows.Count;

    /// <summary>Raised when a series appears that wasn't there before.</summary>
    public event Action<string>? SeriesAdded;

    public void Add(DateTime time, IReadOnlyList<(string Name, double Value)> values)
    {
        Start ??= time;
        _rows.Add(new Row(Count, time, values));
        foreach (var (name, _) in values)
            if (!_names.Contains(name))
            {
                _names.Add(name);
                SeriesAdded?.Invoke(name);
            }
        if (_rows.Count > Capacity)
        {
            var drop = _rows.Count - Capacity;
            _rows.RemoveRange(0, drop);
            _dropped += drop;
        }
    }

    public void Clear()
    {
        _rows.Clear();
        _names.Clear();
        _dropped = 0;
        Start = null;
    }

    /// <summary>
    /// One series' points over the last <paramref name="last"/> rows (all of them when 0), x being the row's number or
    /// its time in seconds since the start.
    /// </summary>
    public (double[] X, double[] Y) Series(string name, int last = 0, bool byTime = false)
    {
        var from = last > 0 ? Math.Max(0, _rows.Count - last) : 0;
        var xs = new List<double>();
        var ys = new List<double>();
        var start = Start ?? DateTime.Now;
        for (var i = from; i < _rows.Count; i++)
        {
            var row = _rows[i];
            foreach (var (n, v) in row.Values)
            {
                if (n != name) continue;
                xs.Add(byTime ? (row.Time - start).TotalSeconds : row.Index);
                ys.Add(v);
                break;
            }
        }
        return ([.. xs], [.. ys]);
    }

    /// <summary>The rows as CSV: when each arrived, seconds since the start, then a column per series (blank where a row had none).</summary>
    public string ToCsv()
    {
        var sb = new StringBuilder();
        sb.Append("time,seconds");
        foreach (var n in _names) sb.Append(',').Append(Csv(n));
        sb.AppendLine();
        var start = Start ?? DateTime.Now;
        foreach (var row in _rows)
        {
            sb.Append(row.Time.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture)).Append(',')
              .Append((row.Time - start).TotalSeconds.ToString("0.000", CultureInfo.InvariantCulture));
            foreach (var n in _names)
            {
                sb.Append(',');
                foreach (var (name, v) in row.Values)
                    if (name == n) { sb.Append(v.ToString("R", CultureInfo.InvariantCulture)); break; }
            }
            sb.AppendLine();
        }
        return sb.ToString();
    }

    private static string Csv(string s) => s.IndexOfAny([',', '"', '\n']) >= 0 ? $"\"{s.Replace("\"", "\"\"")}\"" : s;
}
