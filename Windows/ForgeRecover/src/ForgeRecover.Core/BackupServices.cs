using System.Diagnostics;
using System.Globalization;
using System.Text;
using Microsoft.Data.Sqlite;

namespace ForgeRecover.Core;

public static class AppleBackupPaths
{
    public static IReadOnlyList<string> GetDefaultWindowsRoots()
    {
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

        AddIfNotEmpty(roots, Path.Combine(userProfile, "Apple", "MobileSync", "Backup"));
        AddIfNotEmpty(roots, Path.Combine(appData, "Apple Computer", "MobileSync", "Backup"));
        return roots.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static void AddIfNotEmpty(ISet<string> roots, string path)
    {
        if (!string.IsNullOrWhiteSpace(path))
        {
            roots.Add(Path.GetFullPath(path));
        }
    }
}

public sealed class BackupDiscoveryService
{
    public IReadOnlyList<BackupDescriptor> Discover(IEnumerable<string>? additionalRoots = null)
    {
        var roots = new HashSet<string>(AppleBackupPaths.GetDefaultWindowsRoots(), StringComparer.OrdinalIgnoreCase);
        if (additionalRoots is not null)
        {
            foreach (var root in additionalRoots.Where(root => !string.IsNullOrWhiteSpace(root)))
            {
                roots.Add(Path.GetFullPath(root));
            }
        }

        var results = new List<BackupDescriptor>();
        foreach (var root in roots.OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            if (!Directory.Exists(root))
            {
                continue;
            }

            foreach (var backupDirectory in Directory.EnumerateDirectories(root).OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
            {
                var manifestDatabase = Path.Combine(backupDirectory, "Manifest.db");
                var legacyManifest = Path.Combine(backupDirectory, "Manifest.mbdb");
                if (!File.Exists(manifestDatabase) && !File.Exists(legacyManifest))
                {
                    continue;
                }

                var info = new DirectoryInfo(backupDirectory);
                var totalBytes = CalculateDirectorySize(backupDirectory);
                var origin = backupDirectory.Contains("Apple Computer", StringComparison.OrdinalIgnoreCase)
                    ? BackupOrigin.ITunesDesktop
                    : BackupOrigin.AppleDevices;

                results.Add(new BackupDescriptor(
                    backupDirectory,
                    info.Name,
                    origin,
                    new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero),
                    totalBytes,
                    File.Exists(manifestDatabase)));
            }
        }

        return results
            .OrderByDescending(item => item.LastModifiedUtc)
            .ThenBy(item => item.RootPath, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static long CalculateDirectorySize(string root)
    {
        long total = 0;
        foreach (var file in Directory.EnumerateFiles(root, "*", new EnumerationOptions
                 {
                     RecurseSubdirectories = true,
                     IgnoreInaccessible = true,
                     AttributesToSkip = FileAttributes.ReparsePoint
                 }))
        {
            try
            {
                total = checked(total + new FileInfo(file).Length);
            }
            catch (IOException)
            {
                // A live backup may rotate a file while discovery is running. Skip it and continue.
            }
            catch (UnauthorizedAccessException)
            {
                // Discovery is best-effort; ingest performs strict access checks.
            }
            catch (OverflowException)
            {
                return long.MaxValue;
            }
        }

        return total;
    }
}

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
            throw new FileNotFoundException("Manifest.db was not found. Legacy Manifest.mbdb backups are not yet supported.", manifestPath);
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
        if (!reader.Read())
        {
            return null;
        }

        return ReadResolvedFile(reader);
    }

    public ResolvedBackupFile? ResolveFirst(IEnumerable<(string Domain, string RelativePath)> candidates)
    {
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

    public void Dispose() => _connection.Dispose();

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
            throw new InvalidDataException($"File is not a readable SQLite database: {path}. It may be encrypted or damaged.");
        }
    }

    private ResolvedBackupFile ReadResolvedFile(SqliteDataReader reader)
    {
        var fileId = reader.GetString(reader.GetOrdinal("fileID"));
        var domain = reader.GetString(reader.GetOrdinal("domain"));
        var relativePath = reader.GetString(reader.GetOrdinal("relativePath"));
        var flagsOrdinal = reader.GetOrdinal("flags");
        var flags = reader.IsDBNull(flagsOrdinal)
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

        foreach (var suffix in new[] { "-wal", "-shm" })
        {
            var sidecar = resolver.Resolve(database.Domain, database.RelativePath + suffix);
            if (sidecar is null)
            {
                continue;
            }

            await CopyFileAsync(sidecar.PhysicalPath, destination + suffix, cancellationToken).ConfigureAwait(false);
        }

        return destination;
    }

    private static async Task CopyFileAsync(string source, string destination, CancellationToken cancellationToken)
    {
        var temp = destination + ".tmp." + Guid.NewGuid().ToString("N");
        await using (var input = new FileStream(source, new FileStreamOptions
                     {
                         Access = FileAccess.Read,
                         Mode = FileMode.Open,
                         Share = FileShare.ReadWrite | FileShare.Delete,
                         Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
                         BufferSize = 1024 * 1024
                     }))
        await using (var output = new FileStream(temp, new FileStreamOptions
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

        File.Move(temp, destination, overwrite: true);
    }
}

public sealed class ExternalToolRunner
{
    public async Task<ToolExecutionResult> RunAsync(
        string executable,
        IEnumerable<string> arguments,
        string? workingDirectory = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executable);
        var argumentList = arguments.ToArray();
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = string.IsNullOrWhiteSpace(workingDirectory)
                ? Environment.CurrentDirectory
                : Path.GetFullPath(workingDirectory)
        };
        foreach (var argument in argumentList)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        try
        {
            if (!process.Start())
            {
                throw new InvalidOperationException($"Failed to start external tool: {executable}");
            }
        }
        catch (System.ComponentModel.Win32Exception exception)
        {
            throw new FileNotFoundException($"External tool was not found: {executable}", executable, exception);
        }

        var standardOutputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var standardErrorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        var standardOutput = await standardOutputTask.ConfigureAwait(false);
        var standardError = await standardErrorTask.ConfigureAwait(false);

        return new ToolExecutionResult(executable, argumentList, process.ExitCode, standardOutput, standardError);
    }
}

public sealed class LibimobiledeviceClient
{
    private readonly string? _toolDirectory;
    private readonly ExternalToolRunner _runner;

    public LibimobiledeviceClient(string? toolDirectory = null, ExternalToolRunner? runner = null)
    {
        _toolDirectory = string.IsNullOrWhiteSpace(toolDirectory) ? null : Path.GetFullPath(toolDirectory);
        _runner = runner ?? new ExternalToolRunner();
    }

    public async Task<IReadOnlyList<string>> ListDeviceIdsAsync(CancellationToken cancellationToken = default)
    {
        var result = await _runner.RunAsync(ToolPath("idevice_id"), new[] { "-l" }, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        EnsureSuccess(result);
        return result.StandardOutput
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public async Task<ToolExecutionResult> ReadDeviceInfoAsync(string deviceId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
        var result = await _runner.RunAsync(
            ToolPath("ideviceinfo"),
            new[] { "-u", deviceId },
            cancellationToken: cancellationToken).ConfigureAwait(false);
        EnsureSuccess(result);
        return result;
    }

    public async Task<ToolExecutionResult> CreateFullBackupAsync(
        string deviceId,
        string destination,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(destination);
        var output = Path.GetFullPath(destination);
        Directory.CreateDirectory(output);

        var result = await _runner.RunAsync(
            ToolPath("idevicebackup2"),
            new[] { "-u", deviceId, "backup", "--full", output },
            cancellationToken: cancellationToken).ConfigureAwait(false);
        EnsureSuccess(result);
        return result;
    }

    private string ToolPath(string baseName)
    {
        var fileName = OperatingSystem.IsWindows() ? baseName + ".exe" : baseName;
        return _toolDirectory is null ? fileName : Path.Combine(_toolDirectory, fileName);
    }

    private static void EnsureSuccess(ToolExecutionResult result)
    {
        if (!result.Succeeded)
        {
            var error = string.IsNullOrWhiteSpace(result.StandardError)
                ? result.StandardOutput
                : result.StandardError;
            throw new InvalidOperationException($"{Path.GetFileName(result.Executable)} failed with exit code {result.ExitCode}: {error.Trim()}");
        }
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
