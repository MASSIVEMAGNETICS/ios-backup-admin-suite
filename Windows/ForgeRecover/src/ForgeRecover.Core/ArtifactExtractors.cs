using System.Globalization;
using Microsoft.Data.Sqlite;

namespace ForgeRecover.Core;

public sealed record ExtractionContext(string BackupRoot, string WorkingRoot);

public interface IArtifactExtractor
{
    string Name { get; }
    ArtifactKind Kind { get; }
    Task<ExtractionResult> ExtractAsync(ExtractionContext context, CancellationToken cancellationToken = default);
}

public sealed class ArtifactExtractorRegistry
{
    private readonly IReadOnlyList<IArtifactExtractor> _extractors;

    public ArtifactExtractorRegistry(IEnumerable<IArtifactExtractor>? extractors = null)
    {
        _extractors = (extractors ?? CreateDefaultExtractors()).ToArray();
    }

    public IReadOnlyList<string> Names => _extractors.Select(extractor => extractor.Name).ToArray();

    public async Task<IReadOnlyList<ExtractionResult>> ExtractAsync(
        ExtractionContext context,
        IEnumerable<string>? selectedNames = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        Directory.CreateDirectory(context.WorkingRoot);

        var selected = selectedNames is null
            ? null
            : new HashSet<string>(selectedNames, StringComparer.OrdinalIgnoreCase);
        var results = new List<ExtractionResult>();

        foreach (var extractor in _extractors)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (selected is not null && !selected.Contains(extractor.Name))
            {
                continue;
            }

            try
            {
                results.Add(await extractor.ExtractAsync(context, cancellationToken).ConfigureAwait(false));
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                results.Add(new ExtractionResult(
                    extractor.Name,
                    string.Empty,
                    0,
                    new[] { $"Extractor failed: {exception.Message}" },
                    Array.Empty<ArtifactRecord>()));
            }
        }

        return results;
    }

    private static IEnumerable<IArtifactExtractor> CreateDefaultExtractors()
    {
        yield return new MessagesArtifactExtractor();
        yield return new CallHistoryArtifactExtractor();
        yield return new ContactsArtifactExtractor();
        yield return new CameraRollArtifactExtractor();
    }
}

internal static class SqliteArtifactHelpers
{
    private static readonly DateTimeOffset AppleEpoch = new(2001, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public static bool TableExists(SqliteConnection connection, string tableName)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = $name LIMIT 1;";
        command.Parameters.AddWithValue("$name", tableName);
        return command.ExecuteScalar() is not null;
    }

    public static HashSet<string> Columns(SqliteConnection connection, string tableName)
    {
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info({QuoteIdentifier(tableName)});";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            columns.Add(reader.GetString(reader.GetOrdinal("name")));
        }

        return columns;
    }

    public static string QuoteIdentifier(string identifier)
    {
        if (string.IsNullOrWhiteSpace(identifier) || identifier.Any(character => !(char.IsLetterOrDigit(character) || character == '_')))
        {
            throw new ArgumentException("Unsafe SQLite identifier.", nameof(identifier));
        }

        return '"' + identifier + '"';
    }

    public static string? ReadString(SqliteDataReader reader, string column)
    {
        var ordinal = TryGetOrdinal(reader, column);
        if (ordinal is null || reader.IsDBNull(ordinal.Value))
        {
            return null;
        }

        var value = reader.GetValue(ordinal.Value);
        return value switch
        {
            byte[] => null,
            _ => Convert.ToString(value, CultureInfo.InvariantCulture)
        };
    }

    public static long? ReadInt64(SqliteDataReader reader, string column)
    {
        var ordinal = TryGetOrdinal(reader, column);
        if (ordinal is null || reader.IsDBNull(ordinal.Value))
        {
            return null;
        }

        try
        {
            return Convert.ToInt64(reader.GetValue(ordinal.Value), CultureInfo.InvariantCulture);
        }
        catch (FormatException)
        {
            return null;
        }
        catch (InvalidCastException)
        {
            return null;
        }
        catch (OverflowException)
        {
            return null;
        }
    }

    public static double? ReadDouble(SqliteDataReader reader, string column)
    {
        var ordinal = TryGetOrdinal(reader, column);
        if (ordinal is null || reader.IsDBNull(ordinal.Value))
        {
            return null;
        }

        try
        {
            return Convert.ToDouble(reader.GetValue(ordinal.Value), CultureInfo.InvariantCulture);
        }
        catch (FormatException)
        {
            return null;
        }
        catch (InvalidCastException)
        {
            return null;
        }
        catch (OverflowException)
        {
            return null;
        }
    }

    public static DateTimeOffset? AppleTimestamp(long? value)
    {
        if (value is null)
        {
            return null;
        }

        var seconds = Math.Abs(value.Value) > 1_000_000_000_000L
            ? value.Value / 1_000_000_000d
            : value.Value;
        try
        {
            return AppleEpoch.AddSeconds(seconds);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    public static DateTimeOffset? AppleTimestamp(double? value)
    {
        if (value is null || double.IsNaN(value.Value) || double.IsInfinity(value.Value))
        {
            return null;
        }

        var seconds = Math.Abs(value.Value) > 1_000_000_000_000d
            ? value.Value / 1_000_000_000d
            : value.Value;
        try
        {
            return AppleEpoch.AddSeconds(seconds);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    public static string StableArtifactId(string extractor, string source, long? rowId, string? discriminator = null)
    {
        var material = $"{extractor}\n{source}\n{rowId?.ToString(CultureInfo.InvariantCulture)}\n{discriminator}";
        var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(material));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static int? TryGetOrdinal(SqliteDataReader reader, string column)
    {
        try
        {
            return reader.GetOrdinal(column);
        }
        catch (IndexOutOfRangeException)
        {
            return null;
        }
    }
}

public sealed class MessagesArtifactExtractor : IArtifactExtractor
{
    public string Name => "messages";
    public ArtifactKind Kind => ArtifactKind.Message;

    public async Task<ExtractionResult> ExtractAsync(ExtractionContext context, CancellationToken cancellationToken = default)
    {
        using var resolver = new ManifestDbResolver(context.BackupRoot);
        var source = resolver.ResolveFirst(KnownBackupArtifacts.Messages);
        if (source is null)
        {
            return Empty("Messages database is not present in this backup.");
        }

        var working = Path.Combine(context.WorkingRoot, Name);
        var databasePath = await new BackupWorkingCopyService()
            .PrepareSqliteSetAsync(resolver, source, working, cancellationToken)
            .ConfigureAwait(false);

        using var connection = ManifestDbResolver.OpenReadOnly(databasePath);
        if (!SqliteArtifactHelpers.TableExists(connection, "message"))
        {
            return Empty("Messages database has no message table.", source.RelativePath);
        }

        var messageColumns = SqliteArtifactHelpers.Columns(connection, "message");
        var hasHandle = SqliteArtifactHelpers.TableExists(connection, "handle");
        var handleColumns = hasHandle
            ? SqliteArtifactHelpers.Columns(connection, "handle")
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var select = new List<string> { "m.ROWID AS row_id" };
        AddColumn(select, messageColumns, "guid", "m", "guid_value");
        AddColumn(select, messageColumns, "date", "m", "date_value");
        AddColumn(select, messageColumns, "date_read", "m", "date_read_value");
        AddColumn(select, messageColumns, "date_delivered", "m", "date_delivered_value");
        AddColumn(select, messageColumns, "text", "m", "text_value");
        AddColumn(select, messageColumns, "subject", "m", "subject_value");
        AddColumn(select, messageColumns, "service", "m", "service_value");
        AddColumn(select, messageColumns, "is_from_me", "m", "is_from_me_value");
        AddColumn(select, messageColumns, "is_read", "m", "is_read_value");
        AddColumn(select, messageColumns, "cache_roomnames", "m", "room_value");
        AddColumn(select, messageColumns, "associated_message_type", "m", "associated_type_value");

        var join = string.Empty;
        if (hasHandle && messageColumns.Contains("handle_id") && handleColumns.Contains("id"))
        {
            select.Add("h.id AS handle_value");
            if (handleColumns.Contains("service"))
            {
                select.Add("h.service AS handle_service_value");
            }

            join = "LEFT JOIN handle h ON h.ROWID = m.handle_id";
        }

        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {string.Join(", ", select)} FROM message m {join} ORDER BY m.ROWID;";
        using var reader = command.ExecuteReader();
        var artifacts = new List<ArtifactRecord>();
        while (reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var rowId = SqliteArtifactHelpers.ReadInt64(reader, "row_id");
            var guid = SqliteArtifactHelpers.ReadString(reader, "guid_value");
            var timestamp = SqliteArtifactHelpers.AppleTimestamp(SqliteArtifactHelpers.ReadInt64(reader, "date_value"));
            var handle = SqliteArtifactHelpers.ReadString(reader, "handle_value");
            var service = SqliteArtifactHelpers.ReadString(reader, "service_value")
                          ?? SqliteArtifactHelpers.ReadString(reader, "handle_service_value");
            var isFromMe = SqliteArtifactHelpers.ReadInt64(reader, "is_from_me_value");
            var direction = isFromMe switch
            {
                1 => "outgoing",
                0 => "incoming",
                _ => "unknown"
            };
            var body = SqliteArtifactHelpers.ReadString(reader, "text_value");
            var subject = SqliteArtifactHelpers.ReadString(reader, "subject_value");

            artifacts.Add(new ArtifactRecord(
                SqliteArtifactHelpers.StableArtifactId(Name, source.RelativePath, rowId, guid),
                Kind,
                timestamp,
                handle,
                service,
                body,
                direction,
                source.RelativePath,
                rowId,
                new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
                {
                    ["guid"] = guid,
                    ["subject"] = subject,
                    ["room"] = SqliteArtifactHelpers.ReadString(reader, "room_value"),
                    ["is_read"] = SqliteArtifactHelpers.ReadString(reader, "is_read_value"),
                    ["date_read_raw"] = SqliteArtifactHelpers.ReadString(reader, "date_read_value"),
                    ["date_delivered_raw"] = SqliteArtifactHelpers.ReadString(reader, "date_delivered_value"),
                    ["associated_message_type"] = SqliteArtifactHelpers.ReadString(reader, "associated_type_value"),
                    ["recovery_status"] = "present_in_backup_database"
                }));
        }

        return new ExtractionResult(Name, source.RelativePath, artifacts.Count, Array.Empty<string>(), artifacts);
    }

    private ExtractionResult Empty(string warning, string source = "") =>
        new(Name, source, 0, new[] { warning }, Array.Empty<ArtifactRecord>());

    private static void AddColumn(ICollection<string> select, ISet<string> columns, string column, string alias, string outputAlias)
    {
        if (columns.Contains(column))
        {
            select.Add($"{alias}.{SqliteArtifactHelpers.QuoteIdentifier(column)} AS {SqliteArtifactHelpers.QuoteIdentifier(outputAlias)}");
        }
    }
}

public sealed class CallHistoryArtifactExtractor : IArtifactExtractor
{
    public string Name => "calls";
    public ArtifactKind Kind => ArtifactKind.Call;

    public async Task<ExtractionResult> ExtractAsync(ExtractionContext context, CancellationToken cancellationToken = default)
    {
        using var resolver = new ManifestDbResolver(context.BackupRoot);
        var source = resolver.ResolveFirst(KnownBackupArtifacts.CallHistory);
        if (source is null)
        {
            return Empty("Call history database is not present in this backup.");
        }

        var databasePath = await new BackupWorkingCopyService()
            .PrepareSqliteSetAsync(resolver, source, Path.Combine(context.WorkingRoot, Name), cancellationToken)
            .ConfigureAwait(false);
        using var connection = ManifestDbResolver.OpenReadOnly(databasePath);

        if (SqliteArtifactHelpers.TableExists(connection, "ZCALLRECORD"))
        {
            return ExtractCoreData(connection, source, cancellationToken);
        }

        return Empty("Unsupported call history schema. Expected ZCALLRECORD.", source.RelativePath);
    }

    private ExtractionResult ExtractCoreData(SqliteConnection connection, ResolvedBackupFile source, CancellationToken cancellationToken)
    {
        var columns = SqliteArtifactHelpers.Columns(connection, "ZCALLRECORD");
        var select = new List<string> { "ROWID AS row_id" };
        Add(select, columns, "ZDATE", "date_value");
        Add(select, columns, "ZADDRESS", "address_value");
        Add(select, columns, "ZDURATION", "duration_value");
        Add(select, columns, "ZORIGINATED", "originated_value");
        Add(select, columns, "ZANSWERED", "answered_value");
        Add(select, columns, "ZNAME", "name_value");
        Add(select, columns, "ZCALLTYPE", "type_value");
        Add(select, columns, "ZUNIQUE_ID", "unique_id_value");

        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {string.Join(", ", select)} FROM ZCALLRECORD ORDER BY ROWID;";
        using var reader = command.ExecuteReader();
        var artifacts = new List<ArtifactRecord>();
        while (reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var rowId = SqliteArtifactHelpers.ReadInt64(reader, "row_id");
            var address = SqliteArtifactHelpers.ReadString(reader, "address_value");
            var originated = SqliteArtifactHelpers.ReadInt64(reader, "originated_value");
            var duration = SqliteArtifactHelpers.ReadDouble(reader, "duration_value");
            var uniqueId = SqliteArtifactHelpers.ReadString(reader, "unique_id_value");

            artifacts.Add(new ArtifactRecord(
                SqliteArtifactHelpers.StableArtifactId(Name, source.RelativePath, rowId, uniqueId),
                Kind,
                SqliteArtifactHelpers.AppleTimestamp(SqliteArtifactHelpers.ReadDouble(reader, "date_value")),
                address,
                SqliteArtifactHelpers.ReadString(reader, "name_value"),
                duration?.ToString("0.###", CultureInfo.InvariantCulture),
                originated switch { 1 => "outgoing", 0 => "incoming", _ => "unknown" },
                source.RelativePath,
                rowId,
                new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
                {
                    ["duration_seconds"] = duration?.ToString("0.###", CultureInfo.InvariantCulture),
                    ["answered"] = SqliteArtifactHelpers.ReadString(reader, "answered_value"),
                    ["call_type"] = SqliteArtifactHelpers.ReadString(reader, "type_value"),
                    ["unique_id"] = uniqueId,
                    ["recovery_status"] = "present_in_backup_database"
                }));
        }

        return new ExtractionResult(Name, source.RelativePath, artifacts.Count, Array.Empty<string>(), artifacts);
    }

    private ExtractionResult Empty(string warning, string source = "") =>
        new(Name, source, 0, new[] { warning }, Array.Empty<ArtifactRecord>());

    private static void Add(ICollection<string> select, ISet<string> columns, string column, string alias)
    {
        if (columns.Contains(column))
        {
            select.Add($"{SqliteArtifactHelpers.QuoteIdentifier(column)} AS {SqliteArtifactHelpers.QuoteIdentifier(alias)}");
        }
    }
}

public sealed class ContactsArtifactExtractor : IArtifactExtractor
{
    public string Name => "contacts";
    public ArtifactKind Kind => ArtifactKind.Contact;

    public async Task<ExtractionResult> ExtractAsync(ExtractionContext context, CancellationToken cancellationToken = default)
    {
        using var resolver = new ManifestDbResolver(context.BackupRoot);
        var source = resolver.ResolveFirst(KnownBackupArtifacts.Contacts);
        if (source is null)
        {
            return Empty("Contacts database is not present in this backup.");
        }

        var databasePath = await new BackupWorkingCopyService()
            .PrepareSqliteSetAsync(resolver, source, Path.Combine(context.WorkingRoot, Name), cancellationToken)
            .ConfigureAwait(false);
        using var connection = ManifestDbResolver.OpenReadOnly(databasePath);
        if (!SqliteArtifactHelpers.TableExists(connection, "ABPerson"))
        {
            return Empty("Unsupported contacts schema. Expected ABPerson.", source.RelativePath);
        }

        var values = ReadMultiValues(connection);
        var columns = SqliteArtifactHelpers.Columns(connection, "ABPerson");
        var select = new List<string> { "ROWID AS row_id" };
        Add(select, columns, "First", "first_value");
        Add(select, columns, "Last", "last_value");
        Add(select, columns, "Middle", "middle_value");
        Add(select, columns, "Organization", "organization_value");
        Add(select, columns, "Nickname", "nickname_value");
        Add(select, columns, "Note", "note_value");
        Add(select, columns, "CreationDate", "creation_value");
        Add(select, columns, "ModificationDate", "modification_value");

        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {string.Join(", ", select)} FROM ABPerson ORDER BY ROWID;";
        using var reader = command.ExecuteReader();
        var artifacts = new List<ArtifactRecord>();
        while (reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var rowId = SqliteArtifactHelpers.ReadInt64(reader, "row_id");
            var first = SqliteArtifactHelpers.ReadString(reader, "first_value");
            var middle = SqliteArtifactHelpers.ReadString(reader, "middle_value");
            var last = SqliteArtifactHelpers.ReadString(reader, "last_value");
            var name = string.Join(' ', new[] { first, middle, last }.Where(part => !string.IsNullOrWhiteSpace(part)));
            var contactValues = rowId is not null && values.TryGetValue(rowId.Value, out var matches)
                ? matches
                : Array.Empty<string>();

            artifacts.Add(new ArtifactRecord(
                SqliteArtifactHelpers.StableArtifactId(Name, source.RelativePath, rowId, name),
                Kind,
                SqliteArtifactHelpers.AppleTimestamp(SqliteArtifactHelpers.ReadDouble(reader, "creation_value")),
                string.IsNullOrWhiteSpace(name) ? null : name,
                SqliteArtifactHelpers.ReadString(reader, "organization_value"),
                SqliteArtifactHelpers.ReadString(reader, "note_value"),
                null,
                source.RelativePath,
                rowId,
                new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
                {
                    ["nickname"] = SqliteArtifactHelpers.ReadString(reader, "nickname_value"),
                    ["values"] = string.Join(" | ", contactValues),
                    ["modified_utc"] = SqliteArtifactHelpers.AppleTimestamp(SqliteArtifactHelpers.ReadDouble(reader, "modification_value"))?.ToString("O", CultureInfo.InvariantCulture),
                    ["recovery_status"] = "present_in_backup_database"
                }));
        }

        return new ExtractionResult(Name, source.RelativePath, artifacts.Count, Array.Empty<string>(), artifacts);
    }

    private static Dictionary<long, string[]> ReadMultiValues(SqliteConnection connection)
    {
        var result = new Dictionary<long, List<string>>();
        if (!SqliteArtifactHelpers.TableExists(connection, "ABMultiValue"))
        {
            return new Dictionary<long, string[]>();
        }

        var columns = SqliteArtifactHelpers.Columns(connection, "ABMultiValue");
        if (!columns.Contains("record_id") || !columns.Contains("value"))
        {
            return new Dictionary<long, string[]>();
        }

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT record_id, value FROM ABMultiValue WHERE value IS NOT NULL ORDER BY record_id, ROWID;";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var recordId = Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture);
            var value = Convert.ToString(reader.GetValue(1), CultureInfo.InvariantCulture);
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            if (!result.TryGetValue(recordId, out var list))
            {
                list = new List<string>();
                result[recordId] = list;
            }

            list.Add(value);
        }

        return result.ToDictionary(pair => pair.Key, pair => pair.Value.Distinct(StringComparer.OrdinalIgnoreCase).ToArray());
    }

    private ExtractionResult Empty(string warning, string source = "") =>
        new(Name, source, 0, new[] { warning }, Array.Empty<ArtifactRecord>());

    private static void Add(ICollection<string> select, ISet<string> columns, string column, string alias)
    {
        if (columns.Contains(column))
        {
            select.Add($"{SqliteArtifactHelpers.QuoteIdentifier(column)} AS {SqliteArtifactHelpers.QuoteIdentifier(alias)}");
        }
    }
}

public sealed class CameraRollArtifactExtractor : IArtifactExtractor
{
    private static readonly HashSet<string> PhotoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".heic", ".heif", ".png", ".gif", ".tif", ".tiff", ".dng", ".raw"
    };

    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mov", ".mp4", ".m4v", ".3gp"
    };

    public string Name => "media";
    public ArtifactKind Kind => ArtifactKind.Photo;

    public Task<ExtractionResult> ExtractAsync(ExtractionContext context, CancellationToken cancellationToken = default)
    {
        using var resolver = new ManifestDbResolver(context.BackupRoot);
        var files = new List<ResolvedBackupFile>();
        files.AddRange(resolver.Enumerate("CameraRollDomain", "Media/DCIM/"));
        files.AddRange(resolver.Enumerate("MediaDomain", "Media/DCIM/"));

        var artifacts = new List<ArtifactRecord>();
        var warnings = new List<string>();
        foreach (var file in files.GroupBy(item => item.FileId, StringComparer.OrdinalIgnoreCase).Select(group => group.First()))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var extension = Path.GetExtension(file.RelativePath);
            var kind = PhotoExtensions.Contains(extension)
                ? ArtifactKind.Photo
                : VideoExtensions.Contains(extension)
                    ? ArtifactKind.Video
                    : ArtifactKind.Unknown;
            if (kind == ArtifactKind.Unknown)
            {
                continue;
            }

            try
            {
                var info = new FileInfo(file.PhysicalPath);
                artifacts.Add(new ArtifactRecord(
                    SqliteArtifactHelpers.StableArtifactId(Name, file.RelativePath, null, file.FileId),
                    kind,
                    new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero),
                    Path.GetFileName(file.RelativePath),
                    file.Domain,
                    file.RelativePath,
                    null,
                    file.RelativePath,
                    null,
                    new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["file_id"] = file.FileId,
                        ["bytes"] = info.Length.ToString(CultureInfo.InvariantCulture),
                        ["sha256"] = EvidenceHashService.ComputeSha256Async(file.PhysicalPath, cancellationToken).GetAwaiter().GetResult(),
                        ["timestamp_basis"] = "backup_payload_last_write_time",
                        ["recovery_status"] = "present_in_backup_payload"
                    }));
            }
            catch (IOException exception)
            {
                warnings.Add($"Could not read {file.RelativePath}: {exception.Message}");
            }
        }

        return Task.FromResult(new ExtractionResult(Name, "CameraRollDomain/MediaDomain", artifacts.Count, warnings, artifacts));
    }
}
