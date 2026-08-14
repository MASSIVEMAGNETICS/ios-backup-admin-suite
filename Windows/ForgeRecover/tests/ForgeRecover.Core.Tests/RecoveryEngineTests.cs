using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using ForgeRecover.Core;

namespace ForgeRecover.Core.Tests;

public sealed class RecoveryEngineTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ForgeRecoverRecoveryTests", Guid.NewGuid().ToString("N"));

    public RecoveryEngineTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task RecoversMarkerFromFreelistLeafPage()
    {
        var db = Path.Combine(_root, "freelist.db");
        CreateFreelistDatabase(db, "FREELIST_DELETED_MESSAGE_440");
        var report = await new SQLiteRecoveryEngine().RecoverAsync(db);
        Assert.Contains(report.Fragments, fragment =>
            fragment.SourceKind == SQLiteRecoverySourceKind.FreelistLeafPage
            && fragment.Text.Contains("FREELIST_DELETED_MESSAGE_440", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RecoversMarkerFromBTreeFreeblock()
    {
        var db = Path.Combine(_root, "freeblock.db");
        CreateBTreeFreeblockDatabase(db, "FREEBLOCK_OLD_TEXT_440");
        var report = await new SQLiteRecoveryEngine().RecoverAsync(db);
        Assert.Contains(report.Fragments, fragment =>
            fragment.SourceKind == SQLiteRecoverySourceKind.BTreeFreeblock
            && fragment.Text.Contains("FREEBLOCK_OLD_TEXT_440", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RecoversOnlyHistoricalCommittedWalByDefault()
    {
        var db = Path.Combine(_root, "messages.db");
        var wal = db + "-wal";
        CreateSinglePageDatabase(db, "CURRENT_DATABASE_TEXT_440");
        CreateTwoCommitWal(wal, "WAL_HISTORICAL_DELETED_TEXT_440", "WAL_CURRENT_LOGICAL_TEXT_440");
        var report = await new SQLiteRecoveryEngine().RecoverAsync(db, wal);
        Assert.Equal(2, report.WalFramesValidated);
        Assert.Equal(2, report.WalTransactionsCommitted);
        Assert.Contains(report.Fragments, fragment =>
            fragment.SourceKind == SQLiteRecoverySourceKind.WalCommittedHistoricalPage
            && fragment.Text.Contains("WAL_HISTORICAL_DELETED_TEXT_440", StringComparison.Ordinal));
        Assert.DoesNotContain(report.Fragments, fragment =>
            fragment.Text.Contains("WAL_CURRENT_LOGICAL_TEXT_440", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CorruptWalChecksumIsRejected()
    {
        var db = Path.Combine(_root, "corrupt.db");
        var wal = db + "-wal";
        CreateSinglePageDatabase(db, "CURRENT_DB_440");
        CreateTwoCommitWal(wal, "OLD_WAL_440", "CURRENT_WAL_440");
        var bytes = await File.ReadAllBytesAsync(wal);
        bytes[48] ^= 0x01;
        await File.WriteAllBytesAsync(wal, bytes);
        var report = await new SQLiteRecoveryEngine().RecoverAsync(db, wal);
        Assert.Equal(0, report.WalFramesValidated);
        Assert.Contains(report.Warnings, warning => warning.Contains("checksum", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(report.Fragments, fragment =>
            fragment.SourceKind is SQLiteRecoverySourceKind.WalCommittedHistoricalPage
                or SQLiteRecoverySourceKind.WalCurrentCommittedPage
                or SQLiteRecoverySourceKind.WalUncommittedHistoricalPage);
    }

    [Fact]
    public async Task CorpusRunnerRequiresAndValidatesKnownMarkers()
    {
        var caseRoot = Path.Combine(_root, "corpus", "ios18-sms-delete-001");
        Directory.CreateDirectory(caseRoot);
        var db = Path.Combine(caseRoot, "sms.db");
        CreateFreelistDatabase(db, "KNOWN_DELETED_MARKER_440");
        var manifest = new
        {
            id = "ios18-sms-delete-001",
            database = "sms.db",
            wal = (string?)null,
            expected_contains = new[] { "KNOWN_DELETED_MARKER_440" },
            expected_absent = new[] { "NEVER_EXISTED_MARKER_440" },
            minimum_text_characters = 8
        };
        await File.WriteAllTextAsync(Path.Combine(caseRoot, "case.forge-recovery.json"), JsonSerializer.Serialize(manifest));
        var report = await new SQLiteRecoveryCorpusRunner().RunAsync(Path.Combine(_root, "corpus"));
        Assert.True(report.IsValid);
        Assert.Equal(1, report.Cases);
        Assert.Equal(1, report.Passed);
        Assert.Equal(0, report.Failed);
    }

    [Fact]
    public void PasswordBufferCanBeExplicitlyCleared()
    {
        var password = "authorized-backup-password".ToCharArray();
        EncryptedBackupUnlockService.ClearPassword(password);
        Assert.All(password, character => Assert.Equal('\0', character));
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static void CreateFreelistDatabase(string path, string marker)
    {
        const int pageSize = 512;
        var bytes = new byte[pageSize * 3];
        WriteDatabaseHeader(bytes.AsSpan(0, pageSize), pageSize, 2, 2);
        var trunk = bytes.AsSpan(pageSize, pageSize);
        BinaryPrimitives.WriteUInt32BigEndian(trunk.Slice(0, 4), 0);
        BinaryPrimitives.WriteUInt32BigEndian(trunk.Slice(4, 4), 1);
        BinaryPrimitives.WriteUInt32BigEndian(trunk.Slice(8, 4), 3);
        Encoding.UTF8.GetBytes(marker).CopyTo(bytes.AsSpan(pageSize * 2 + 40));
        File.WriteAllBytes(path, bytes);
    }

    private static void CreateBTreeFreeblockDatabase(string path, string marker)
    {
        const int pageSize = 512;
        var page = new byte[pageSize];
        WriteDatabaseHeader(page, pageSize, 0, 0);
        const int header = 100;
        page[header] = 0x0D;
        BinaryPrimitives.WriteUInt16BigEndian(page.AsSpan(header + 1, 2), 200);
        BinaryPrimitives.WriteUInt16BigEndian(page.AsSpan(header + 3, 2), 0);
        BinaryPrimitives.WriteUInt16BigEndian(page.AsSpan(header + 5, 2), 180);
        BinaryPrimitives.WriteUInt16BigEndian(page.AsSpan(200, 2), 0);
        BinaryPrimitives.WriteUInt16BigEndian(page.AsSpan(202, 2), 80);
        Encoding.UTF8.GetBytes(marker).CopyTo(page.AsSpan(204));
        File.WriteAllBytes(path, page);
    }

    private static void CreateSinglePageDatabase(string path, string marker)
    {
        const int pageSize = 512;
        var page = new byte[pageSize];
        WriteDatabaseHeader(page, pageSize, 0, 0);
        Encoding.UTF8.GetBytes(marker).CopyTo(page.AsSpan(300));
        File.WriteAllBytes(path, page);
    }

    private static void WriteDatabaseHeader(Span<byte> page, int pageSize, uint trunk, uint freelistPages)
    {
        "SQLite format 3\0"u8.CopyTo(page);
        BinaryPrimitives.WriteUInt16BigEndian(page.Slice(16, 2), (ushort)pageSize);
        page[18] = 2; page[19] = 2; page[20] = 0; page[21] = 64; page[22] = 32; page[23] = 32;
        BinaryPrimitives.WriteUInt32BigEndian(page.Slice(28, 4), 1);
        BinaryPrimitives.WriteUInt32BigEndian(page.Slice(32, 4), trunk);
        BinaryPrimitives.WriteUInt32BigEndian(page.Slice(36, 4), freelistPages);
        BinaryPrimitives.WriteUInt32BigEndian(page.Slice(56, 4), 1);
    }

    private static void CreateTwoCommitWal(string path, string historicalMarker, string currentMarker)
    {
        const int pageSize = 512;
        const uint magic = 0x377F0682;
        const uint salt1 = 0x10203040;
        const uint salt2 = 0x50607080;
        var header = new byte[32];
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(0, 4), magic);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4, 4), 3_007_000);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(8, 4), pageSize);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(12, 4), 1);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(16, 4), salt1);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(20, 4), salt2);
        var checksum = WalChecksum(header.AsSpan(0, 24), false, 0, 0);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(24, 4), checksum.S0);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(28, 4), checksum.S1);
        using var output = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        output.Write(header);
        WriteWalFrame(output, historicalMarker, salt1, salt2, ref checksum);
        WriteWalFrame(output, currentMarker, salt1, salt2, ref checksum);
    }

    private static void WriteWalFrame(Stream output, string marker, uint salt1, uint salt2, ref (uint S0, uint S1) checksum)
    {
        var frameHeader = new byte[24];
        var page = new byte[512];
        Encoding.UTF8.GetBytes(marker).CopyTo(page.AsSpan(220));
        BinaryPrimitives.WriteUInt32BigEndian(frameHeader.AsSpan(0, 4), 1);
        BinaryPrimitives.WriteUInt32BigEndian(frameHeader.AsSpan(4, 4), 1);
        BinaryPrimitives.WriteUInt32BigEndian(frameHeader.AsSpan(8, 4), salt1);
        BinaryPrimitives.WriteUInt32BigEndian(frameHeader.AsSpan(12, 4), salt2);
        checksum = WalChecksum(frameHeader.AsSpan(0, 8), false, checksum.S0, checksum.S1);
        checksum = WalChecksum(page, false, checksum.S0, checksum.S1);
        BinaryPrimitives.WriteUInt32BigEndian(frameHeader.AsSpan(16, 4), checksum.S0);
        BinaryPrimitives.WriteUInt32BigEndian(frameHeader.AsSpan(20, 4), checksum.S1);
        output.Write(frameHeader); output.Write(page);
    }

    private static (uint S0, uint S1) WalChecksum(ReadOnlySpan<byte> input, bool big, uint s0, uint s1)
    {
        unchecked
        {
            for (var offset = 0; offset < input.Length; offset += 8)
            {
                var x0 = big ? BinaryPrimitives.ReadUInt32BigEndian(input.Slice(offset, 4)) : BinaryPrimitives.ReadUInt32LittleEndian(input.Slice(offset, 4));
                var x1 = big ? BinaryPrimitives.ReadUInt32BigEndian(input.Slice(offset + 4, 4)) : BinaryPrimitives.ReadUInt32LittleEndian(input.Slice(offset + 4, 4));
                s0 += x0 + s1; s1 += x1 + s0;
            }
        }
        return (s0, s1);
    }
}
