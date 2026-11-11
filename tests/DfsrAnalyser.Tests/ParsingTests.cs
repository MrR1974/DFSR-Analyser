using System.Text;
using DfsrAnalyser.Core.Parsing;

namespace DfsrAnalyser.Tests;

public class LineParserTests
{
    [Fact]
    public void Parses_a_dated_line_with_variable_padding()
    {
        Assert.True(LineParser.TryParseDated(Samples.SubmitUpdateSent, 7, out var ev));
        Assert.Equal(7, ev!.LineNo);
        Assert.Equal(new DateTime(2026, 11, 11, 13, 14, 7, 482), ev.LocalTime);
        Assert.Equal(4444, ev.Thread);
        Assert.Equal("JOIN", ev.Module);
        Assert.Equal(1253, ev.SourceLine);
        Assert.Equal(Severity.Info, ev.Severity);
        Assert.Equal("Join::SubmitUpdate", ev.ClassMethod);
        Assert.StartsWith("Sent: uid:{BFF1148F", ev.Message);
    }

    [Fact]
    public void Reads_severity_tag_and_a_padded_thread_id()
    {
        Assert.True(LineParser.TryParseDated(Samples.ErrorLine, 1, out var err));
        Assert.Equal(684, err!.Thread);
        Assert.Equal(Severity.Error, err.Severity);
        Assert.Equal("ConfigMonitorTask::Step", err.ClassMethod);
        Assert.StartsWith("Local machine is not joined", err.Message);

        Assert.True(LineParser.TryParseDated(Samples.WarnLine, 1, out var warn));
        Assert.Equal(Severity.Warn, warn!.Severity);
        Assert.Equal("Join::FinalizeJoin", warn.ClassMethod);
    }

    [Fact]
    public void Accepts_a_method_without_a_class()
    {
        Assert.True(LineParser.TryParseDated(Samples.Exit, 1, out var ev));
        Assert.Equal("main", ev!.ClassMethod);
        Assert.Equal("Service Exiting Successfully", ev.Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData("+\tfid  0x1")]
    [InlineData("* DFSR Log Sequence:1")]
    [InlineData("2026111 13:14:07.482 4444 JOIN 1 X::Y")]
    [InlineData("20261311 13:14:07.482 4444 JOIN 1 X::Y")]
    public void Rejects_non_dated_lines(string line) => Assert.False(LineParser.TryParseDated(line, 1, out _));

    [Fact]
    public void Nested_text_drops_plus_and_one_tab() =>
        Assert.Equal("fid                             0x3000000012940", LineParser.NestedText(Samples.IdRecord[0]));
}

public class LogHeaderTests
{
    [Fact]
    public void Parses_both_header_lines()
    {
        var h = new LogHeader().Apply(Samples.Header1).Apply(Samples.Header2);
        Assert.Equal(2, h.Sequence);
        Assert.Equal(5, h.Index);
        Assert.Equal("DFSRDC01", h.Computer);
        Assert.Equal("AUS Eastern Daylight Time", h.TimeZoneName);
        Assert.Equal(TimeSpan.FromHours(11), h.GmtOffset);
        Assert.Equal("AUS Eastern Standard Time", h.TzId);
        Assert.Equal(5, h.LogLevel);
        Assert.Equal(200000, h.MaxEntryCount);
        Assert.Equal(1000, h.MaxFileCount);
        Assert.Equal(@"\\.\C:\Windows\debug\", h.LogPath);
    }
}

public class LocalTimeConverterTests
{
    private static LogHeader Aus => new LogHeader().Apply(Samples.Header1);

    [Fact]
    public void Matches_the_utc_clock_in_the_id_record()
    {
        // CreateNewRecord at 11:47:05.743 local carries "clock 20261111 00:47:05.743 GMT".
        var utc = new LocalTimeConverter(Aus).ToUtc(new DateTime(2026, 11, 11, 11, 47, 5, 743));
        Assert.Equal(new DateTime(2026, 11, 11, 0, 47, 5, 743, DateTimeKind.Utc), utc);
        Assert.Equal(DateTimeKind.Utc, utc.Kind);
    }

    [Fact]
    public void Uses_standard_time_outside_daylight_saving()
    {
        var utc = new LocalTimeConverter(Aus).ToUtc(new DateTime(2026, 7, 1, 10, 0, 0));
        Assert.Equal(new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc), utc);
    }

    [Fact]
    public void Falls_back_to_the_fixed_offset_for_an_unknown_zone()
    {
        var h = Aus with { TzId = "No Such Zone" };
        var conv = new LocalTimeConverter(h);
        Assert.Equal(LocalTimeConverter.Basis.FixedOffset, conv.ConversionBasis);
        Assert.Equal(new DateTime(2026, 7, 1, 0, 0, 0), conv.ToUtc(new DateTime(2026, 7, 1, 11, 0, 0)));
    }

    [Fact]
    public void Does_not_throw_in_the_spring_forward_gap()
    {
        // Sydney skips 02:00-03:00 on 2026-10-04.
        var conv = new LocalTimeConverter(Aus);
        var ex = Record.Exception(() => conv.ToUtc(new DateTime(2026, 10, 4, 2, 30, 0)));
        Assert.Null(ex);
    }
}

public class LogFileReaderTests
{
    private static LogFileReader ReaderFor(string text) => new(new MemoryStream(Encoding.UTF8.GetBytes(text)));

    [Fact]
    public void Handles_mixed_line_endings_blank_lines_and_leading_orphans()
    {
        var text =
            Samples.Header1 + "\r\n" + Samples.Header2 + "\n" +
            "+\t{BFF1148F-18FE-47A1-9C5C-617CAEB61A1E} |-> { 10..1711}\r\n" +
            "+\t\n" +
            "\r\n" +
            Samples.CreateNewRecord + "\r\n" +
            string.Join("\n", Samples.IdRecord) + "\r\n" +
            "\n" +
            Samples.Exit + "\n";

        using var r = ReaderFor(text);
        var events = r.ReadEvents().ToList();

        Assert.Equal(5, r.Header.Index);
        Assert.Equal(2, r.LeadingOrphans.Count);
        Assert.Equal("{BFF1148F-18FE-47A1-9C5C-617CAEB61A1E} |-> { 10..1711}", r.LeadingOrphans[0]);
        Assert.Equal(2, events.Count);
        Assert.Equal(Samples.IdRecord.Length, events[0].Nested.Count);
        Assert.Equal(6, events[0].LineNo);
        Assert.Equal("main", events[1].ClassMethod);
        Assert.Equal(2, r.BlankLines);
        Assert.StartsWith("20261111 11:47:05.743", r.FirstDatedLine);
    }

    [Fact]
    public void Attaches_unrecognised_lines_to_the_previous_event()
    {
        var text = Samples.Header1 + "\n" + Samples.CreateNewRecord + "\n<xml continuation/>\n" + Samples.Exit + "\n";
        using var r = ReaderFor(text);
        var events = r.ReadEvents().ToList();
        Assert.Equal("<xml continuation/>", Assert.Single(events[0].Nested));
        Assert.Equal(1, r.UnrecognisedLines);
    }

    [Fact]
    public void Skips_events_before_the_resume_line()
    {
        var text = Samples.Header1 + "\n" + Samples.SubmitUpdateSent + "\n" + Samples.RawGet + "\n" + Samples.Exit + "\n";
        using var r = ReaderFor(text);
        var events = r.ReadEvents(fromLine: 3).ToList();
        Assert.Equal([3, 4], events.Select(e => e.LineNo));
    }
}
