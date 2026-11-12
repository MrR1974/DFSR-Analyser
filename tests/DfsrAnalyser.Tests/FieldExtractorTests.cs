using DfsrAnalyser.Core.Parsing;

namespace DfsrAnalyser.Tests;

public class FieldExtractorTests
{
    private static readonly Guid Dc01Db = Guid.Parse("2314971A-C581-4D65-A9C1-9C297D1261E2");
    private static readonly Guid Member01Db = Guid.Parse("BFF1148F-18FE-47A1-9C5C-617CAEB61A1E");
    private static readonly Guid DfsFolder = Guid.Parse("B2683E40-9D0D-4BF7-A465-298E6B684DA8");

    private static EventFields ExtractLine(string line, params string[] nested)
    {
        Assert.True(LineParser.TryParseDated(line, 1, out var ev));
        foreach (var n in nested) ev!.Nested.Add(LineParser.NestedText(n));
        return FieldExtractor.Extract(ev!);
    }

    [Fact]
    public void Join_SubmitUpdate_Sent_carries_identity_names_and_connection()
    {
        var f = ExtractLine(Samples.SubmitUpdateSent);
        Assert.Equal(new VersionId(Member01Db, 1786), f.Uid);
        Assert.Equal(new VersionId(Member01Db, 1786), f.Gvsn);
        Assert.Equal("file_0772.txt", f.FileName);
        Assert.Equal("txt", f.Extension);
        Assert.Equal(Guid.Parse("DA74DAD4-33AF-4DE8-AAD9-E945D8C6F0B5"), f.ConnId);
        Assert.Equal(DfsFolder, f.CsId);
        Assert.Equal("DFSFolder", f.CsName);
        Assert.Null(f.TransferType);
    }

    [Fact]
    public void Rawget_gives_transfer_type_and_staged_size()
    {
        var f = ExtractLine(Samples.RawGet);
        Assert.Equal("RAWGET", f.TransferType);
        Assert.Equal(3008767, f.StagedSize);
        Assert.Equal("file_1643.txt", f.FileName);
    }

    [Fact]
    public void Free_text_names_keep_their_spaces()
    {
        var f = ExtractLine(Samples.AddContentSet);
        Assert.Equal("SYSVOL Share", f.CsName);
        Assert.Equal(Guid.Parse("5A27B242-B850-46D7-9BD2-A363911C5190"), f.CsId);
    }

    [Fact]
    public void Meet_install_gives_retries_and_update_type()
    {
        var f = ExtractLine(Samples.MeetInstall);
        Assert.Equal(0, f.Retries);
        Assert.Equal("remote", f.UpdateType);
        Assert.Equal("file_1002.txt", f.FileName);
    }

    [Fact]
    public void Id_record_fields_come_from_nested_lines()
    {
        var f = ExtractLine(Samples.CreateNewRecord, Samples.IdRecord);
        Assert.Equal(new VersionId(Dc01Db, 34), f.Uid);
        Assert.Equal(new VersionId(Dc01Db, 34), f.Gvsn);
        Assert.Equal(new VersionId(DfsFolder, 1), f.Parent);
        Assert.Equal(0x3000000012940, f.Fid);
        Assert.Equal(DfsFolder, f.CsId);
        Assert.Equal("file_0001.txt", f.FileName);
        Assert.Equal(0x20, f.Attributes);
        Assert.False(f.IsFolder);
        Assert.True(f.Present);
        Assert.False(f.MeetInstallUpdate);
    }

    [Fact]
    public void Usn_record_gives_reason_name_and_attributes()
    {
        var f = ExtractLine(Samples.TombstoneOrDelete, Samples.UsnRecord);
        Assert.Equal("Close File Delete", f.UsnReason);
        Assert.Equal("file_0007.txt", f.FileName);
        Assert.Equal(0x20, f.Attributes);
        Assert.Equal(0x20000000204C3, f.Fid);
    }

    [Fact]
    public void Error_stack_gives_outermost_and_innermost_codes()
    {
        var f = ExtractLine(Samples.ErrorLine,
            "+\t[Error:9027(0x2343) A failure was reported by the remote partner]",
            "+\t[Error:1722(0x6ba) The RPC server is unavailable.]");
        Assert.Equal(9027, f.ErrorCode);
        Assert.Equal(1722, f.ErrorCodeInner);

        var single = ExtractLine(Samples.ErrorLine, Samples.ErrorStack);
        Assert.Equal(9551, single.ErrorCode);
        Assert.Equal(9551, single.ErrorCodeInner);
    }

    [Fact]
    public void Event_log_id_is_extracted()
    {
        Assert.Equal(4412, ExtractLine(Samples.EventLogReport).EventLogId);
    }

    [Fact]
    public void Message_tokens_split_on_keys()
    {
        var tokens = FieldExtractor.MessageTokens("Received RAWGET updateName:My File.docx uid:{BFF1148F-18FE-47A1-9C5C-617CAEB61A1E}-v2 stagedSize:10");
        Assert.Equal(
            [("updateName", "My File.docx"), ("uid", "{BFF1148F-18FE-47A1-9C5C-617CAEB61A1E}-v2"), ("stagedSize", "10")],
            tokens);
    }

    [Fact]
    public void Bracketed_key_qualifier_ends_a_file_name()
    {
        // Real line shape (lab sample): the name used to run on into "fileSize(approx):…".
        var tokens = FieldExtractor.MessageTokens(
            "RDC signatureLevels:1, uid:{BFF1148F-18FE-47A1-9C5C-617CAEB61A1E}-v2250 gvsn:{BFF1148F-18FE-47A1-9C5C-617CAEB61A1E}-v2250 fileName:file_1236.txt fileSize(approx):1204224 csId:{B2683E40-9D0D-4BF7-A465-298E6B684DA8} enableSim=1");
        Assert.Contains(("fileName", "file_1236.txt"), tokens);
        Assert.Contains(("fileSize(approx)", "1204224"), tokens);
    }

    [Theory]
    [InlineData("{2314971A-C581-4D65-A9C1-9C297D1261E2}-v3035", 3035)]
    [InlineData("2314971A-C581-4D65-A9C1-9C297D1261E2-v1", 1)]
    [InlineData("{2314971A-C581-4D65-A9C1-9C297D1261E2}-v12 trailing", 12)]
    public void Version_ids_parse_with_or_without_braces(string text, long version)
    {
        Assert.Equal(new VersionId(Dc01Db, version), FieldExtractor.ParseVersionId(text));
    }

    [Theory]
    [InlineData("")]
    [InlineData("{2314971A-C581-4D65-A9C1-9C297D1261E2}")]
    [InlineData("{not-a-guid}-v1")]
    public void Malformed_version_ids_are_null(string text) => Assert.Null(FieldExtractor.ParseVersionId(text));

    [Fact]
    public void Version_id_formats_like_the_log()
    {
        Assert.Equal("{2314971A-C581-4D65-A9C1-9C297D1261E2}-v34", new VersionId(Dc01Db, 34).ToString());
    }
}
