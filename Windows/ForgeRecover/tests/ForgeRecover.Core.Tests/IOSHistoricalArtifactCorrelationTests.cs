using ForgeRecover.Core;
using Microsoft.Data.Sqlite;

namespace ForgeRecover.Core.Tests;

public sealed class IOSHistoricalArtifactCorrelationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ForgeRecoverCorrelation", Guid.NewGuid().ToString("N"));

    public IOSHistoricalArtifactCorrelationTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task CorrelatesHistoricalMessageRowWithoutClaimingDeletion()
    {
        var database = Path.Combine(_root, "sms.db");
        using var connection = OpenWalDatabase(database);
        Execute(connection, """
            CREATE TABLE handle (id TEXT, service TEXT);
            CREATE TABLE message (
                guid TEXT,
                text TEXT,
                date INTEGER,
                handle_id INTEGER,
                service TEXT,
                is_from_me INTEGER,
                subject TEXT,
                attributedBody BLOB
            );
            INSERT INTO handle(ROWID, id, service) VALUES (7, '+14405551212', 'iMessage');
            """);
        Execute(connection, "PRAGMA wal_checkpoint(TRUNCATE);");
        Execute(connection, """
            INSERT INTO message(ROWID, guid, text, date, handle_id, service, is_from_me, subject, attributedBody)
            VALUES (42, 'guid-440', 'HISTORICAL_MESSAGE_440', 808000000000000000, 7, 'iMessage', 0, 'subject-440', NULL);
            """);
        Execute(connection, "DELETE FROM message WHERE ROWID = 42;");

        var rows = await new SQLiteRowRecoveryEngine().RecoverAsync(database, database + "-wal");
        var correlated = new IOSHistoricalArtifactCorrelator().Correlate(database, rows);
        var artifact = Assert.Single(correlated.Artifacts, item =>
            item.Kind == ArtifactKind.Message && item.SourceRowId == 42);

        Assert.Equal("HISTORICAL_MESSAGE_440", artifact.Body);
        Assert.Equal("+14405551212", artifact.Primary);
        Assert.Equal("iMessage", artifact.Secondary);
        Assert.Equal("incoming", artifact.Direction);
        Assert.Equal("not_asserted", artifact.Metadata["deleted_claim"]);
        Assert.Equal("not_asserted", artifact.Metadata["deleted_message_claim"]);
        Assert.Contains("historical_message_candidate:", artifact.Metadata["message_candidate_status"], StringComparison.Ordinal);
        Assert.Equal("guid-440", artifact.Metadata["guid"]);
    }

    [Fact]
    public async Task CorrelatesHistoricalCallRowWithTypedDuration()
    {
        var database = Path.Combine(_root, "CallHistory.storedata");
        using var connection = OpenWalDatabase(database);
        Execute(connection, """
            CREATE TABLE ZCALLRECORD (
                ZDATE REAL,
                ZADDRESS TEXT,
                ZDURATION REAL,
                ZORIGINATED INTEGER,
                ZANSWERED INTEGER,
                ZNAME TEXT,
                ZCALLTYPE INTEGER,
                ZUNIQUE_ID TEXT
            );
            """);
        Execute(connection, "PRAGMA wal_checkpoint(TRUNCATE);");
        Execute(connection, """
            INSERT INTO ZCALLRECORD(ROWID, ZDATE, ZADDRESS, ZDURATION, ZORIGINATED, ZANSWERED, ZNAME, ZCALLTYPE, ZUNIQUE_ID)
            VALUES (11, 808000000.5, '+14405559876', 33.25, 1, 1, 'Caller 440', 1, 'call-guid-440');
            """);
        Execute(connection, "DELETE FROM ZCALLRECORD WHERE ROWID = 11;");

        var rows = await new SQLiteRowRecoveryEngine().RecoverAsync(database, database + "-wal");
        var correlated = new IOSHistoricalArtifactCorrelator().Correlate(database, rows);
        var artifact = Assert.Single(correlated.Artifacts, item =>
            item.Kind == ArtifactKind.Call && item.SourceRowId == 11);

        Assert.Equal("+14405559876", artifact.Primary);
        Assert.Equal("Caller 440", artifact.Secondary);
        Assert.Equal("33.25", artifact.Body);
        Assert.Equal("outgoing", artifact.Direction);
        Assert.Equal("not_asserted", artifact.Metadata["deleted_call_claim"]);
        Assert.Equal("call-guid-440", artifact.Metadata["unique_id"]);
    }

    [Fact]
    public async Task CorrelatesHistoricalContactAndCurrentMultiValue()
    {
        var database = Path.Combine(_root, "AddressBook.sqlitedb");
        using var connection = OpenWalDatabase(database);
        Execute(connection, """
            CREATE TABLE ABPerson (
                First TEXT,
                Last TEXT,
                Middle TEXT,
                Organization TEXT,
                Nickname TEXT,
                Note TEXT,
                CreationDate REAL,
                ModificationDate REAL
            );
            CREATE TABLE ABMultiValue (record_id INTEGER, value TEXT);
            """);
        Execute(connection, "PRAGMA wal_checkpoint(TRUNCATE);");
        Execute(connection, """
            INSERT INTO ABPerson(ROWID, First, Last, Middle, Organization, Nickname, Note, CreationDate, ModificationDate)
            VALUES (9, 'Recovered', 'Contact', NULL, 'Massive Magnetics', 'RC', 'historical note', 808000000.0, 808000010.0);
            INSERT INTO ABMultiValue(record_id, value) VALUES (9, '+14405550009');
            """);
        Execute(connection, "DELETE FROM ABPerson WHERE ROWID = 9;");

        var rows = await new SQLiteRowRecoveryEngine().RecoverAsync(database, database + "-wal");
        var correlated = new IOSHistoricalArtifactCorrelator().Correlate(database, rows);
        var artifact = Assert.Single(correlated.Artifacts, item =>
            item.Kind == ArtifactKind.Contact && item.SourceRowId == 9);

        Assert.Equal("Recovered Contact", artifact.Primary);
        Assert.Equal("Massive Magnetics", artifact.Secondary);
        Assert.Equal("historical note", artifact.Body);
        Assert.Contains("+14405550009", artifact.Metadata["values"], StringComparison.Ordinal);
        Assert.Equal("not_asserted", artifact.Metadata["deleted_contact_claim"]);
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
