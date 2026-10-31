using System.Text;
using AVASerMonitor.Core;

namespace AVASerMonitor.Tests;

public class TextTests
{
    private static Chunk C(string s) => new(new DateTime(2026, 10, 31, 12, 0, 0), Encoding.UTF8.GetBytes(s));

    private static List<string> Lines(LineAssembler a, params string[] chunks) => chunks.SelectMany(c => a.Add(C(c))).Select(l => l.Text).ToList();

    [Fact]
    public void Lines_end_at_LF_CR_or_CRLF_even_split_across_chunks()
    {
        var a = new LineAssembler();
        Assert.Equal(["one", "two", "three", "four"], Lines(a, "one\r\ntw", "o\nthree\r", "\nfour\r"));
        // A CRLF split between chunks is still one line end.
        Assert.Equal(["five"], Lines(a, "five\r", "\n"));
        Assert.Equal([""], Lines(a, "\n"));
    }

    [Fact]
    public void Control_characters_show_as_their_pictures_and_text_decodes_as_chosen()
    {
        Assert.Equal("A␀B␛C\tD␡", LineAssembler.Decode([(byte)'A', 0, (byte)'B', 0x1B, (byte)'C', 9, (byte)'D', 0x7F], TextEncoding.Utf8));
        Assert.Equal("°C", LineAssembler.Decode([0xC2, 0xB0, (byte)'C'], TextEncoding.Utf8));
        Assert.Equal("Â°C", LineAssembler.Decode([0xC2, 0xB0, (byte)'C'], TextEncoding.Latin1));
        Assert.Equal("��C", LineAssembler.Decode([0xC2, 0xB0, (byte)'C'], TextEncoding.Ascii));
    }

    [Fact]
    public void An_unfinished_line_waits_then_shows_and_a_long_one_is_cut()
    {
        var a = new LineAssembler();
        Assert.Empty(a.Add(C("> ")));
        Assert.Null(a.Flush(TimeSpan.FromMilliseconds(400), new DateTime(2026, 10, 31, 12, 0, 0, 100)));
        var prompt = a.Flush(TimeSpan.FromMilliseconds(400), new DateTime(2026, 10, 31, 12, 0, 1));
        Assert.Equal("> ", prompt!.Value.Text);
        Assert.False(prompt.Value.Complete);

        var cut = new LineAssembler { MaxLine = 10 };
        var lines = cut.Add(C(new string('x', 25))).ToList();
        Assert.Equal(2, lines.Count);
        Assert.All(lines, l => Assert.Equal(10, l.Text.Length));
    }

    [Fact]
    public void Hex_rows_line_up_and_typed_hex_is_read_in_any_usual_form()
    {
        var row = Hex.Row("Hello\r\n"u8, 16);
        Assert.StartsWith("0010  48 65 6C 6C 6F 0D 0A ", row);
        Assert.EndsWith(" |Hello..|", row);
        // A short row lines its characters up with a full one.
        Assert.Equal(Hex.Row(new byte[16], 0).IndexOf('|'), row.IndexOf('|'));
        var rows = Hex.Rows(new byte[20], 0).ToList();
        Assert.Equal(2, rows.Count);
        Assert.StartsWith("0010  00 00 00 00", rows[1]);
        Assert.Equal([0x48, 0x65, 0x6C, 0x6C, 0x6F, 0x0D], Hex.Parse("48 65 0x6C,6C6F 0d"));
        Assert.Contains("isn't hex", Assert.Throws<FormatException>(() => Hex.Parse("48 6")).Message);
        Assert.Throws<FormatException>(() => Hex.Parse("zz"));
    }

    [Fact]
    public void What_is_sent_gets_its_line_ending_and_escapes()
    {
        Assert.Equal("ping\r\n"u8.ToArray(), Outgoing.Encode("ping", false, LineEnding.CRLF));
        Assert.Equal("ping\n"u8.ToArray(), Outgoing.Encode("ping", false, LineEnding.LF));
        Assert.Equal("ping"u8.ToArray(), Outgoing.Encode("ping", false, LineEnding.None));
        Assert.Equal(new byte[] { (byte)'a', 9, 0, 0x1B, (byte)'\\', 13 }, Outgoing.Encode("a\\t\\0\\x1B\\\\", false, LineEnding.CR));
        Assert.Equal("\\q"u8.ToArray(), Outgoing.Encode("\\q", false, LineEnding.None));
        Assert.Equal(new byte[] { 1, 2 }, Outgoing.Encode("01 02", true, LineEnding.CRLF));
        Assert.Equal("a\\tb"u8.ToArray(), Outgoing.Encode("a\\tb", false, LineEnding.None, escapes: false));
    }
}
