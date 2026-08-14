using Microsoft.Data.Sqlite;
using ForgeRecover.Core;

namespace ForgeRecover.Core.Tests;

public sealed class SQLiteRowRecoveryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ForgeRecoverRowRecovery", Guid.NewGuid().ToString("N"));

    public SQLiteRowRecoveryTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task ReconstructsDeletedHistoricalWalRowWithOverflowAndTypedValues()
    {
        var database = Path.Combine(_root, "sms.db");
        using var connection = OpenWalDatabase(database);
        Execute(connection, """
            CREATE TABLE message (
                body TEXT,
                handle TEXT,
                score INTEGER,
                ratio REAL,
                attachment BLOB,
                optional TEXT
            );
            """);
        Execute(connection, "PRAGMA wal_checkpoint(TRUNCATE);");

        var longBody = "DELETED_OVERFLOW_MESSAGE_440_" + new string('X', 2400);
        using (var insert = connection.CreateCommand())
        {
            insert.CommandText = """
                INSERT INTO message(body, handle, score, ratio, attachment, optional)
                VALUES ($body, $handle, $score, $ratio, $attachment, NULL);
                """;
            insert.Parameters.AddWithValue("$body", longBody);
            insert.Parameters.AddWithValue("$handle", "+14405551212");
            insert.Parameters.AddWithValue("$score", 440L);
            insert.Parameters.AddWithValue("$ratio", 4.4d);
            insert.Parameters.Add("$attachment", SqliteType.Blob).Value = new byte[] { 0xde, 0xad, 0xbe, 0xef, 0x44, 0x00 };
            insert.ExecuteNonQuery();
        }
        Execute(connection, "DELETE FROM message WHERE rowid = 1;");

        var wal = database + "-wal";
        Assert.True(File.Exists(wal));
        var report = await new SQLiteRowRecoveryEngine().RecoverAsync(database, wal);

        var recovered = Assert.Single(report.Rows.Where(row =>
            row.SourceKind == SQLiteRowRecoverySourceKind.WalHistoricalRow
            && row.TableName == "message"
            && row.RowId == 1
            && row.RecoveryStatus == "historical_row_absent_current"));
        Assert.True(recovered.OverflowPagesRead > 0);
        Assert.Equal(6, recovered.Columns.Count);
        Assert.Equal(longBody, recovered.Columns[0].Value.TextValue);
        Assert.Equal("+14405551212", recovered.Columns[1].Value.TextValue);
        Assert.Equal(440L, recovered.Columns[2].Value.IntegerValue);
        Assert.Equal(4.4d, recovered.Columns[3].Value.RealValue);
        Assert.Equal(SQLiteDecodedStorageClass.Blob, recovered.Columns[4].Value.StorageClass);
        Assert.Equal(6, recovered.Columns[4].Value.ByteLength);
        Assert.Equal(SQLiteDecodedStorageClass.Null, recovered.Columns[5].Value.StorageClass);
        Assert.Equal("current_btree_page_owner", recovered.SchemaMapping);
        Assert.True(recovered.Confidence >= .9);
    }

    [Fact]
    public async Task DistinguishesHistoricalVersionFromCurrentRow()
    {
        var database = Path.Combine(_root, "history.db");
        using var connection = OpenWalDatabase(database);
        Execute(connection, "CREATE TABLE message(body TEXT, handle TEXT);");
        Execute(connection, "PRAGMA wal_checkpoint(TRUNCATE);");
        Execute(connection, "INSERT INTO message(body, handle) VALUES ('OLD_VERSION_440', '+14405550000');");
        Execute(connection, "UPDATE message SET body = 'CURRENT_VERSION_440' WHERE rowid = 1;");

        var report = await new SQLiteRowRecoveryEngine().RecoverAsync(database, database + "-wal");
        Assert.Contains(report.Rows, row =>
            row.TableName == "message"
            && row.RowId == 1
            && row.RecoveryStatus == "historical_row_version"
            && row.Columns.Any(column => column.Value.TextValue == "OLD_VERSION_440"));
        Assert.DoesNotContain(report.Rows, row =>
            row.RecoveryStatus == "historical_row_still_current");
    }

    [Fact]
    public async Task CanIncludeHistoricalRowsThatRemainCurrentWhenExplicitlyRequested()
    {
        var database = Path.Combine(_root, "still-current.db");
        using var connection = OpenWalDatabase(database);
        Execute(connection, "CREATE TABLE message(body TEXT);");
        Execute(connection, "PRAGMA wal_checkpoint(TRUNCATE);");
        Execute(connection, "INSERT INTO message(body) VALUES ('UNCHANGED_440');");
        Execute(connection, "CREATE TABLE unrelated(value TEXT);");

        var report = await new SQLiteRowRecoveryEngine().RecoverAsync(
            database,
            database + "-wal",
            new SQLiteRowRecoveryOptions { IncludeHistoricalRowsStillCurrent = true });
        Assert.Contains(report.Rows, row =>
            row.TableName == "message"
            && row.RowId == 1
            && row.RecoveryStatus == "historical_row_still_current"
            && row.Columns.Any(column => column.Value.TextValue == "UNCHANGED_440"));
    }

    [Fact]
    public void LocalPayloadFormulaMatchesSQLiteTableLeafRules()
    {
        Assert.Equal(100, SQLiteRecordCodec.LocalPayloadBytes(100, 4096));
        var local = SQLiteRecordCodec.LocalPayloadBytes(20_000, 4096);
        Assert.InRange(local, 1, 4096 - 35);
        Assert.True(local < 20_000);
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
