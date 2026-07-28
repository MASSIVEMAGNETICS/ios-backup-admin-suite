using System.Globalization;
using Microsoft.Data.Sqlite;

namespace ForgeRecover.Core;

public sealed class ManifestDbResolver : IDisposable
{
    private readonly string _backupRoot;
    private readonly SqliteConnection _connection;

    public ManifestDbResolver(string backupRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(backupRoot);
        _backupRoot = Path.GetFullPath(backupRoot);
        var manifestPath = Path.Combine(_backupRoot, "Manifest.db");
        if (!File.Exists(manifestPath))
        {
            throw new FileNotFoundException(
                "Manifest.db was not found. Legacy Manifest.mbdb backups are not yet supported.",
                manifestPath);
        }

        EnsureSqliteHeader(manifestPath);
        _connection = OpenReadOnly(manifestPath);
        if (!TableExists(_connection, "Files"))
        {
            throw new InvalidDataException("Manifest.db does not contain the expected Files table.");
        }
    }

    public ResolvedBackupFile? Resolve(string domain, string relativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(domain);
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);

        using var command = _connection.CreateCommand();
        command.CommandText = """
            SELECT fileID, domain, relativePath, flags
            FROM Files
            WHERE domain = $domain AND relativePath = $relativePath
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$domain", domain);
        command.Parameters.AddWithValue("$relativePath", relativePath);

        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadResolvedFile(reader) : null;
    }

    public ResolvedBackupFile? ResolveFirst(IEnumerable<(string Domain, string RelativePath)> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        foreach (var candidate in candidates)
        {
            var result = Resolve(candidate.Domain, candidate.RelativePath);
            if (result is not null)
            {
                return result;
            }
        }

        return null;
    }

    public IReadOnlyList<ResolvedBackupFile> Enumerate(string domain, string? relativePathPrefix = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(domain);
        using var command = _connection.CreateCommand();
        if (string.IsNullOrWhiteSpace(relativePathPrefix))
        {
            command.CommandText = """
                SELECT fileID, domain, relativePath, flags
                FROM Files
                WHERE domain = $domain
                ORDER BY relativePath;
                """;
        }
        else
        {
            command.CommandText = """
                SELECT fileID, domain, relativePath, flags
                FROM Files
                WHERE domain = $domain AND relativePath LIKE $prefix ESCAPE '\'
                ORDER BY relativePath;
                """;
            command.Parameters.AddWithValue("$prefix", EscapeLike(relativePathPrefix) + "%");
        }

        command.Parameters.AddWithValue("$domain", domain);
        using var reader = command.ExecuteReader();
        var results = new List<ResolvedBackupFile>();
        while (reader.Read())
        {
            results.Add(ReadResolvedFile(reader));
        }

        return results;
    }

    public string ResolvePhysicalPath(string fileId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileId);
        if (!string.Equals(fileId, Path.GetFileName(fileId), StringComparison.Ordinal) || fileId.Length < 2)
        {
            throw new InvalidDataException($"Unsafe fileID in Manifest.db: {fileId}");
        }

        var direct = Path.Combine(_backupRoot, fileId);
        if (File.Exists(direct))
        {
            return direct;
        }

        var sharded = Path.Combine(_backupRoot, fileId[..2], fileId);
        if (File.Exists(sharded))
        {
            return sharded;
        }

        throw new FileNotFoundException($"Backup payload is missing for fileID {fileId}.", sharded);
    }

    public static SqliteConnection OpenReadOnly(string path)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = Path.GetFullPath(path),
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Private,
            Pooling = false
        };
        var connection = new SqliteConnection(builder.ToString());
        connection.Open();
        return connection;
    }

    public static void EnsureSqliteHeader(string path)
    {
        Span<byte> header = stackalloc byte[16];
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (stream.Read(header) != header.Length || !header.SequenceEqual("SQLite format 3\0"u8))
        {
            throw new InvalidDataException(
                $"File is not a readable SQLite database: {path}. It may be encrypted or damaged.");
        }
    }

    public void Dispose() => _connection.Dispose();

    private ResolvedBackupFile ReadResolvedFile(SqliteDataReader reader)
    {
        var fileId = reader.GetString(reader.GetOrdinal("fileID"));
        var domain = reader.GetString(reader.GetOrdinal("domain"));
        var relativePath = reader.GetString(reader.GetOrdinal("relativePath"));
        var flagsOrdinal = reader.GetOrdinal("flags");
        long? flags = reader.IsDBNull(flagsOrdinal)
            ? null
            : Convert.ToInt64(reader.GetValue(flagsOrdinal), CultureInfo.InvariantCulture);

        return new ResolvedBackupFile(fileId, domain, relativePath, ResolvePhysicalPath(fileId), flags);
    }

    private static bool TableExists(SqliteConnection connection, string tableName)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = $name LIMIT 1;";
        command.Parameters.AddWithValue("$name", tableName);
        return command.ExecuteScalar() is not null;
    }

    private static string EscapeLike(string value) => value
        .Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("%", "\\%", StringComparison.Ordinal)
        .Replace("_", "\\_", StringComparison.Ordinal);
}

public sealed class BackupWorkingCopyService
{
    private static readonly string[] SqliteSidecarSuffixes = { "-wal", "-shm" };

    public async Task<string> PrepareSqliteSetAsync(
        ManifestDbResolver resolver,
        ResolvedBackupFile database,
        string workingDirectory,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        ArgumentNullException.ThrowIfNull(database);
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);

        Directory.CreateDirectory(workingDirectory);
        var databaseName = Path.GetFileName(database.RelativePath);
        if (string.IsNullOrWhiteSpace(databaseName))
        {
            databaseName = database.FileId + ".db";
        }

        var destination = Path.Combine(workingDirectory, databaseName);
        await CopyFileAsync(database.PhysicalPath, destination, cancellationToken).ConfigureAwait(false);
        ManifestDbResolver.EnsureSqliteHeader(destination);

        foreach (var suffix in SqliteSidecarSuffixes)
        {
            var sidecar = resolver.Resolve(database.Domain, database.RelativePath + suffix);
            if (sidecar is not null)
            {
                await CopyFileAsync(sidecar.PhysicalPath, destination + suffix, cancellationToken).ConfigureAwait(false);
            }
        }

        return destination;
    }

    private static async Task CopyFileAsync(string source, string destination, CancellationToken cancellationToken)
    {
        var temporaryPath = destination + ".tmp." + Guid.NewGuid().ToString("N");
        await using (var input = new FileStream(source, new FileStreamOptions
                     {
                         Access = FileAccess.Read,
                         Mode = FileMode.Open,
                         Share = FileShare.ReadWrite | FileShare.Delete,
                         Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
                         BufferSize = 1024 * 1024
                     }))
        await using (var output = new FileStream(temporaryPath, new FileStreamOptions
                     {
                         Access = FileAccess.Write,
                         Mode = FileMode.CreateNew,
                         Share = FileShare.None,
                         Options = FileOptions.Asynchronous | FileOptions.SequentialScan | FileOptions.WriteThrough,
                         BufferSize = 1024 * 1024
                     }))
        {
            await input.CopyToAsync(output, 1024 * 1024, cancellationToken).ConfigureAwait(false);
            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            output.Flush(flushToDisk: true);
        }

        File.Move(temporaryPath, destination, overwrite: true);
    }
}

public static class KnownBackupArtifacts
{
    public static readonly (string Domain, string RelativePath)[] Messages =
    {
        ("HomeDomain", "Library/SMS/sms.db")
    };

    public static readonly (string Domain, string RelativePath)[] CallHistory =
    {
        ("HomeDomain", "Library/CallHistoryDB/CallHistory.storedata"),
        ("WirelessDomain", "Library/CallHistory/call_history.db")
    };

    public static readonly (string Domain, string RelativePath)[] Contacts =
    {
        ("HomeDomain", "Library/AddressBook/AddressBook.sqlitedb")
    };
}
