using System.Text.Json;
using ForgeRecover.Core;
using Microsoft.Data.Sqlite;

namespace ForgeRecover.Core.Tests;

public sealed class SQLiteRowCorpusTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ForgeRecoverRowCorpus", Guid.NewGuid().ToString("N"));

    public SQLiteRowCorpusTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task ValidatesTypedPositiveAndNegativeRowAssertions()
    {
        var caseRoot = Path.Combine(_root, "ios18-controlled-delete");
        Directory.CreateDirectory(caseRoot);
        var database = Path.Combine(caseRoot, "sms.db");
        using var connection = OpenWalDatabase(database);
        Execute(connection, "CREATE TABLE message(text TEXT, handle_id INTEGER, is_from_me INTEGER);");
        Execute(connection, "PRAGMA wal_checkpoint(TRUNCATE);");
        Execute(connection, "INSERT INTO message(ROWID, text, handle_id, is_from_me) VALUES (440, 'CORPUS_DELETED_440', 9, 0);");
        Execute(connection, "DELETE FROM message WHERE ROWID = 440;");

        var manifest = new SQLiteRowCorpusCaseManifest(
            "ios18-controlled-delete",
            "sms.db",
            "sms.db-wal",
            new[]
            {
                new SQLiteRowCorpusExpectation(
                    "message",
                    440,
                    "historical_row_absent_current",
                    .9,
                    new[]
                    {
                        new SQLiteRowCorpusColumnExpectation("text", Text: "CORPUS_DELETED_440"),
                        new SQLiteRowCorpusColumnExpectation("handle_id", Integer: 9),
                        new SQLiteRowCorpusColumnExpectation("is_from_me", Integer: 0)
                    })
            },
            new[]
            {
                new SQLiteRowCorpusExpectation(
                    "message",
                    440,
                    "historical_row_absent_current",
                    .9,
                    new[] { new SQLiteRowCorpusColumnExpectation("text", Text: "SHOULD_NOT_EXIST") })
            },
            DeviceModel: "FixturePhone16,1",
            IosVersion: "18.fixture",
            AcquisitionSha256: "fixture-acquisition-hash");
        await File.WriteAllTextAsync(
            Path.Combine(caseRoot, "case.forge-row-recovery.json"),
            JsonSerializer.Serialize(manifest, ForgeJson.Options));

        var report = await new SQLiteRowRecoveryCorpusRunner().RunAsync(_root);

        Assert.True(report.IsValid);
        Assert.Equal(1, report.Cases);
        Assert.Equal(1, report.Passed);
        Assert.Equal(0, report.Failed);
        Assert.Equal(1, report.IosVersions["18.fixture"]);
        Assert.Equal(1, report.DeviceModels["FixturePhone16,1"]);
        Assert.Empty(report.Results[0].Failures);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static SqliteConnection OpenWalDatabase(string path)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false
        }.ToString());
        connection.Open();
        Execute(connection, "PRAGMA page_size=512;");
        Execute(connection, "PRAGMA journal_mode=WAL;");
        Execute(connection, "PRAGMA wal_autocheckpoint=0;");
        return connection;
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
