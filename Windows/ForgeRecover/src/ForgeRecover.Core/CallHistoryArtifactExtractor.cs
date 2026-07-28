using System.Globalization;
using Microsoft.Data.Sqlite;

namespace ForgeRecover.Core;

public sealed class CallHistoryArtifactExtractor : IArtifactExtractor
{
    public string Name => "calls";
    public ArtifactKind Kind => ArtifactKind.Call;

    public async Task<ExtractionResult> ExtractAsync(
        ExtractionContext context,
        CancellationToken cancellationToken = default)
    {
        using var resolver = new ManifestDbResolver(context.BackupRoot);
        var source = resolver.ResolveFirst(KnownBackupArtifacts.CallHistory);
        if (source is null)
        {
            return Empty("Call history database is not present in this backup.");
        }

        var databasePath = await new BackupWorkingCopyService()
            .PrepareSqliteSetAsync(
                resolver,
                source,
                Path.Combine(context.WorkingRoot, Name),
                cancellationToken)
            .ConfigureAwait(false);
        using var connection = ManifestDbResolver.OpenReadOnly(databasePath);
        return SqliteArtifactHelpers.TableExists(connection, "ZCALLRECORD")
            ? ExtractCoreData(connection, source, cancellationToken)
            : Empty("Unsupported call history schema. Expected ZCALLRECORD.", source.RelativePath);
    }

    private ExtractionResult ExtractCoreData(
        SqliteConnection connection,
        ResolvedBackupFile source,
        CancellationToken cancellationToken)
    {
        var columns = SqliteArtifactHelpers.Columns(connection, "ZCALLRECORD");
        var select = new List<string> { "ROWID AS row_id" };
        SqliteArtifactHelpers.AddOptionalColumn(select, columns, string.Empty, "ZDATE", "date_value");
        SqliteArtifactHelpers.AddOptionalColumn(select, columns, string.Empty, "ZADDRESS", "address_value");
        SqliteArtifactHelpers.AddOptionalColumn(select, columns, string.Empty, "ZDURATION", "duration_value");
        SqliteArtifactHelpers.AddOptionalColumn(select, columns, string.Empty, "ZORIGINATED", "originated_value");
        SqliteArtifactHelpers.AddOptionalColumn(select, columns, string.Empty, "ZANSWERED", "answered_value");
        SqliteArtifactHelpers.AddOptionalColumn(select, columns, string.Empty, "ZNAME", "name_value");
        SqliteArtifactHelpers.AddOptionalColumn(select, columns, string.Empty, "ZCALLTYPE", "type_value");
        SqliteArtifactHelpers.AddOptionalColumn(select, columns, string.Empty, "ZUNIQUE_ID", "unique_id_value");

        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {string.Join(", ", select)} FROM ZCALLRECORD ORDER BY ROWID;";
        using var reader = command.ExecuteReader();
        var artifacts = new List<ArtifactRecord>();
        while (reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var rowId = SqliteArtifactHelpers.ReadInt64(reader, "row_id");
            var duration = SqliteArtifactHelpers.ReadDouble(reader, "duration_value");
            var uniqueId = SqliteArtifactHelpers.ReadString(reader, "unique_id_value");
            var originated = SqliteArtifactHelpers.ReadInt64(reader, "originated_value");

            artifacts.Add(new ArtifactRecord(
                SqliteArtifactHelpers.StableArtifactId(Name, source.RelativePath, rowId, uniqueId),
                Kind,
                SqliteArtifactHelpers.AppleTimestamp(SqliteArtifactHelpers.ReadDouble(reader, "date_value")),
                SqliteArtifactHelpers.ReadString(reader, "address_value"),
                SqliteArtifactHelpers.ReadString(reader, "name_value"),
                duration?.ToString("0.###", CultureInfo.InvariantCulture),
                originated switch
                {
                    1 => "outgoing",
                    0 => "incoming",
                    _ => "unknown"
                },
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
}
