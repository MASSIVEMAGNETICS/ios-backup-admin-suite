using System.Text;
using ForgeRecover.Core;
using Microsoft.Data.Sqlite;

namespace ForgeRecover.Core.Tests;

public sealed class ForensicPipelineTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ForgeRecoverTests", Guid.NewGuid().ToString("N"));

    public ForensicPipelineTests()
    {
        Directory.CreateDirectory(_root);
    }

    [Fact]
    public async Task Sha256MatchesKnownVector()
    {
        var path = Path.Combine(_root, "abc.txt");
        await File.WriteAllTextAsync(path, "abc", new UTF8Encoding(false));

        var actual = await EvidenceHashService.ComputeSha256Async(path);

        Assert.Equal("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", actual);
    }

    [Fact]
    public async Task CaseVaultCopiesHashesAndDetectsTampering()
    {
        var source = Path.Combine(_root, "source");
        var cases = Path.Combine(_root, "cases");
        Directory.CreateDirectory(Path.Combine(source, "nested"));
        await File.WriteAllTextAsync(Path.Combine(source, "nested", "evidence.txt"), "original evidence");

        var service = new CaseVaultService();
        var caseRoot = await service.CreateAsync(source, cases, "Integrity Test", "Examiner");
        var initial = await service.VerifyAsync(caseRoot);

        Assert.True(initial.IsValid);
        Assert.Equal(1, initial.FilesChecked);

        var copiedEvidence = Path.Combine(caseRoot, "evidence", "original", "nested", "evidence.txt");
        await File.AppendAllTextAsync(copiedEvidence, " tampered");
        var afterTamper = await service.VerifyAsync(caseRoot);

        Assert.False(afterTamper.IsValid);
        Assert.Single(afterTamper.Issues);
        Assert.Equal("nested/evidence.txt", afterTamper.Issues[0].RelativePath.Replace('\\', '/'));
    }

    [Fact]
    public async Task ManifestResolverAndMessagesExtractorRoundTripFixture()
    {
        var backupRoot = Path.Combine(_root, "backup");
        Directory.CreateDirectory(backupRoot);
        const string fileId = "0123456789abcdef0123456789abcdef01234567";
        var shard = Path.Combine(backupRoot, fileId[..2]);
        Directory.CreateDirectory(shard);
        var payloadPath = Path.Combine(shard, fileId);

        CreateMessagesDatabase(payloadPath);
        CreateManifestDatabase(Path.Combine(backupRoot, "Manifest.db"), fileId);

        using (var resolver = new ManifestDbResolver(backupRoot))
        {
            var resolved = resolver.Resolve("HomeDomain", "Library/SMS/sms.db");
            Assert.NotNull(resolved);
            Assert.Equal(payloadPath, resolved.PhysicalPath);
        }

        var extractor = new MessagesArtifactExtractor();
        var result = await extractor.ExtractAsync(
            new ExtractionContext(backupRoot, Path.Combine(_root, "working")));

        var artifact = Assert.Single(result.Artifacts);
        Assert.Equal("messages", result.ExtractorName);
        Assert.Equal("Hello from the fixture", artifact.Body);
        Assert.Equal("+14405551212", artifact.Primary);
        Assert.Equal("incoming", artifact.Direction);
        Assert.Equal("iMessage", artifact.Secondary);
        Assert.Equal("present_in_backup_database", artifact.Metadata["recovery_status"]);
    }

    [Fact]
    public async Task ExportEscapesCsvAndHtml()
    {
        var artifact = new ArtifactRecord(
            "id",
            ArtifactKind.Message,
            DateTimeOffset.Parse("2026-01-01T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture),
            "+14405551212",
            "iMessage",
            "hello, \"world\" <script>",
            "incoming",
            "Library/SMS/sms.db",
            1,
            new Dictionary<string, string?> { ["status"] = "present" });
        var result = new ExtractionResult("messages", "Library/SMS/sms.db", 1, Array.Empty<string>(), new[] { artifact });
        var output = Path.Combine(_root, "exports");

        var summary = await new ArtifactExportService().ExportAsync(output, new[] { result });

        Assert.Equal(1, summary.ArtifactCount);
        var csv = await File.ReadAllTextAsync(Path.Combine(output, "messages.csv"));
        var html = await File.ReadAllTextAsync(Path.Combine(output, "messages.html"));
        Assert.Contains("\"hello, \"\"world\"\" <script>\"", csv, StringComparison.Ordinal);
        Assert.DoesNotContain("<script>", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("&lt;script&gt;", html, StringComparison.OrdinalIgnoreCase);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
            // Windows may briefly retain a SQLite file handle after a failed test.
        }
        catch (UnauthorizedAccessException)
        {
            // Best-effort test cleanup only.
        }
    }

    private static void CreateManifestDatabase(string path, string fileId)
    {
        using var connection = OpenWritable(path);
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE Files (
                fileID TEXT PRIMARY KEY,
                domain TEXT NOT NULL,
                relativePath TEXT NOT NULL,
                flags INTEGER
            );
            INSERT INTO Files(fileID, domain, relativePath, flags)
            VALUES ($fileId, 'HomeDomain', 'Library/SMS/sms.db', 1);
            """;
        command.Parameters.AddWithValue("$fileId", fileId);
        command.ExecuteNonQuery();
    }

    private static void CreateMessagesDatabase(string path)
    {
        using var connection = OpenWritable(path);
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE handle (
                ROWID INTEGER PRIMARY KEY,
                id TEXT,
                service TEXT
            );
            CREATE TABLE message (
                ROWID INTEGER PRIMARY KEY,
                guid TEXT,
                text TEXT,
                date INTEGER,
                handle_id INTEGER,
                service TEXT,
                is_from_me INTEGER,
                is_read INTEGER
            );
            INSERT INTO handle(ROWID, id, service)
            VALUES (7, '+14405551212', 'iMessage');
            INSERT INTO message(ROWID, guid, text, date, handle_id, service, is_from_me, is_read)
            VALUES (42, 'fixture-guid', 'Hello from the fixture', 788918400000000000, 7, 'iMessage', 0, 1);
            """;
        command.ExecuteNonQuery();
    }

    private static SqliteConnection OpenWritable(string path)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false
        }.ToString());
        connection.Open();
        return connection;
    }
}
