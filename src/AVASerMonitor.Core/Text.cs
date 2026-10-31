using System.Globalization;
using System.Text;

namespace AVASerMonitor.Core;

/// <summary>How received bytes are read as text.</summary>
public enum TextEncoding { Utf8, Ascii, Latin1 }

/// <summary>What is added to the end of a line that is sent.</summary>
public enum LineEnding { None, LF, CR, CRLF }

/// <summary>
/// Turns the bytes that arrive into lines of text: split at LF (a CR before it is dropped, and a CR on its own ends a
/// line too), decoded as UTF-8, ASCII or Latin-1, with other control characters shown as their Unicode pictures (␀,
/// ␛) rather than lost. A line still coming is held until it ends, or until <see cref="Flush"/> lets it out (a
/// prompt with no newline after it).
/// </summary>
public sealed class LineAssembler(TextEncoding encoding = TextEncoding.Utf8)
{
    private readonly List<byte> _partial = [];
    private DateTime _partialSince;
    private bool _lastWasCr;

    public TextEncoding Encoding { get; set; } = encoding;

    /// <summary>The most a line may hold before it is let out anyway (a stream with no newlines at all).</summary>
    public int MaxLine { get; set; } = 4096;

    /// <summary>A line of text, when its first byte arrived, and whether it ended or was let out unfinished.</summary>
    public readonly record struct Line(DateTime Time, string Text, bool Complete);

    /// <summary>The whole lines in <paramref name="chunk"/> and any held from before.</summary>
    public IEnumerable<Line> Add(Chunk chunk)
    {
        var lines = new List<Line>();
        foreach (var b in chunk.Data)
        {
            if (_partial.Count == 0 && !(b == (byte)'\n' && _lastWasCr)) _partialSince = chunk.Time;
            if (b == (byte)'\n')
            {
                if (_lastWasCr) { _lastWasCr = false; continue; }
                lines.Add(Take(true));
            }
            else if (b == (byte)'\r')
            {
                lines.Add(Take(true));
                _lastWasCr = true;
                continue;
            }
            else
            {
                _partial.Add(b);
                if (_partial.Count >= MaxLine) lines.Add(Take(false));
            }
            _lastWasCr = false;
        }
        return lines;
    }

    /// <summary>The unfinished line, if any and it has waited at least <paramref name="after"/>: a prompt, or a device that never ends its lines.</summary>
    public Line? Flush(TimeSpan after, DateTime now)
    {
        if (_partial.Count == 0 || now - _partialSince < after) return null;
        return Take(false);
    }

    private Line Take(bool complete)
    {
        var text = Decode(_partial.ToArray(), Encoding);
        _partial.Clear();
        return new Line(_partialSince == default ? DateTime.Now : _partialSince, text, complete);
    }

    /// <summary>Bytes as text, control characters shown as their pictures.</summary>
    public static string Decode(ReadOnlySpan<byte> bytes, TextEncoding encoding)
    {
        var text = encoding switch
        {
            TextEncoding.Ascii => new string(bytes.ToArray().Select(b => b < 0x80 ? (char)b : '�').ToArray()),
            TextEncoding.Latin1 => System.Text.Encoding.Latin1.GetString(bytes),
            _ => System.Text.Encoding.UTF8.GetString(bytes),
        };
        var sb = new StringBuilder(text.Length);
        foreach (var c in text)
            sb.Append(c == '\t' ? c : c < 0x20 ? (char)(0x2400 + c) : c == 0x7F ? '␡' : c);
        return sb.ToString();
    }
}

/// <summary>Bytes as a hex dump: sixteen to a row, the offset first and the printable characters after.</summary>
public static class Hex
{
    /// <summary>One row: "0010  48 65 6C 6C 6F 0D 0A                          |Hello..|".</summary>
    public static string Row(ReadOnlySpan<byte> bytes, long offset, int width = 16)
    {
        var sb = new StringBuilder(width * 4 + 16);
        sb.Append(offset.ToString("X4", CultureInfo.InvariantCulture)).Append("  ");
        for (var i = 0; i < width; i++)
        {
            if (i < bytes.Length) sb.Append(bytes[i].ToString("X2", CultureInfo.InvariantCulture)).Append(' ');
            else sb.Append("   ");
            if (i == 7) sb.Append(' ');
        }
        sb.Append(" |");
        foreach (var b in bytes) sb.Append(b is >= 0x20 and < 0x7F ? (char)b : '.');
        return sb.Append('|').ToString();
    }

    /// <summary>A chunk as rows, the offsets counted from <paramref name="start"/>.</summary>
    public static IEnumerable<string> Rows(byte[] data, long start, int width = 16)
    {
        for (var i = 0; i < data.Length; i += width)
            yield return Row(data.AsSpan(i, Math.Min(width, data.Length - i)), start + i, width);
    }

    /// <summary>
    /// Typed hex as bytes: pairs of digits, with or without spaces, commas or 0x in front ("48 65 0x6C,6C 6F"). Throws
    /// <see cref="FormatException"/> saying what is wrong.
    /// </summary>
    public static byte[] Parse(string text)
    {
        var tokens = text.Split([' ', ',', '\t', ';', ':', '-'], StringSplitOptions.RemoveEmptyEntries);
        var bytes = new List<byte>();
        foreach (var token in tokens)
        {
            var t = token.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? token[2..] : token;
            if (t.Length == 0 || t.Length % 2 != 0 || !t.All(char.IsAsciiHexDigit))
                throw new FormatException($"\"{token}\" isn't hex bytes: give pairs of digits 0-9 and A-F, such as 48 65 6C or 0x0D.");
            for (var i = 0; i < t.Length; i += 2) bytes.Add(byte.Parse(t.AsSpan(i, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
        }
        return [.. bytes];
    }
}

/// <summary>What is typed, as the bytes to send.</summary>
public static class Outgoing
{
    /// <summary>
    /// Text to send, as UTF-8 with the line ending added, escapes such as \n, \r, \t, \0 and \xHH turned into their
    /// bytes when <paramref name="escapes"/> is set. Hex input is read as bytes with nothing added.
    /// </summary>
    public static byte[] Encode(string text, bool asHex, LineEnding ending, bool escapes = true)
    {
        if (asHex) return Hex.Parse(text);
        var bytes = new List<byte>(escapes ? Unescape(text) : Encoding.UTF8.GetBytes(text));
        bytes.AddRange(ending switch
        {
            LineEnding.LF => [(byte)'\n'],
            LineEnding.CR => [(byte)'\r'],
            LineEnding.CRLF => [(byte)'\r', (byte)'\n'],
            _ => [],
        });
        return [.. bytes];
    }

    private static byte[] Unescape(string text)
    {
        var bytes = new List<byte>();
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] != '\\' || i == text.Length - 1)
            {
                bytes.AddRange(Encoding.UTF8.GetBytes(text[i].ToString()));
                continue;
            }
            var c = text[++i];
            switch (c)
            {
                case 'n': bytes.Add(10); break;
                case 'r': bytes.Add(13); break;
                case 't': bytes.Add(9); break;
                case '0': bytes.Add(0); break;
                case '\\': bytes.Add((byte)'\\'); break;
                case 'x' when i + 2 < text.Length && char.IsAsciiHexDigit(text[i + 1]) && char.IsAsciiHexDigit(text[i + 2]):
                    bytes.Add(byte.Parse(text.AsSpan(i + 1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                    i += 2;
                    break;
                default:
                    bytes.Add((byte)'\\');
                    bytes.AddRange(Encoding.UTF8.GetBytes(c.ToString()));
                    break;
            }
        }
        return [.. bytes];
    }
}
