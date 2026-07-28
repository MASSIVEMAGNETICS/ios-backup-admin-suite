namespace ForgeRecover.Core;

public sealed class MessagesArtifactExtractor : IArtifactExtractor
{
    public string Name => "messages";
    public ArtifactKind Kind => ArtifactKind.Message;

    public async Task<ExtractionResult> ExtractAsync(
        ExtractionContext context,
        CancellationToken cancellationToken = default)
    {
        using var resolver = new ManifestDbResolver(context.BackupRoot);
        var source = resolver.ResolveFirst(KnownBackupArtifacts.Messages);
        if (source is null)
        {
            return Empty("Messages database is not present in this backup.");
        }

        var databasePath = await new BackupWorkingCopyService()
            .PrepareSqliteSetAsync(
                resolver,
                source,
                Path.Combine(context.WorkingRoot, Name),
                cancellationToken)
            .ConfigureAwait(false);
        using var connection = ManifestDbResolver.OpenReadOnly(databasePath);
        if (!SqliteArtifactHelpers.TableExists(connection, "message"))
        {
            return Empty("Messages database has no message table.", source.RelativePath);
        }

        var messageColumns = SqliteArtifactHelpers.Columns(connection, "message");
        var hasHandleTable = SqliteArtifactHelpers.TableExists(connection, "handle");
        var handleColumns = hasHandleTable
            ? SqliteArtifactHelpers.Columns(connection, "handle")
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var select = new List<string> { "m.ROWID AS row_id" };
        SqliteArtifactHelpers.AddOptionalColumn(select, messageColumns, "m", "guid", "guid_value");
        SqliteArtifactHelpers.AddOptionalColumn(select, messageColumns, "m", "date", "date_value");
        SqliteArtifactHelpers.AddOptionalColumn(select, messageColumns, "m", "date_read", "date_read_value");
        SqliteArtifactHelpers.AddOptionalColumn(select, messageColumns, "m", "date_delivered", "date_delivered_value");
        SqliteArtifactHelpers.AddOptionalColumn(select, messageColumns, "m", "text", "text_value");
        SqliteArtifactHelpers.AddOptionalColumn(select, messageColumns, "m", "subject", "subject_value");
        SqliteArtifactHelpers.AddOptionalColumn(select, messageColumns, "m", "service", "service_value");
        SqliteArtifactHelpers.AddOptionalColumn(select, messageColumns, "m", "is_from_me", "is_from_me_value");
        SqliteArtifactHelpers.AddOptionalColumn(select, messageColumns, "m", "is_read", "is_read_value");
        SqliteArtifactHelpers.AddOptionalColumn(select, messageColumns, "m", "cache_roomnames", "room_value");
        SqliteArtifactHelpers.AddOptionalColumn(
            select,
            messageColumns,
            "m",
            "associated_message_type",
            "associated_type_value");

        string join;
        if (hasHandleTable && messageColumns.Contains("handle_id") && handleColumns.Contains("id"))
        {
            SqliteArtifactHelpers.AddOptionalColumn(select, handleColumns, "h", "id", "handle_value");
            SqliteArtifactHelpers.AddOptionalColumn(select, handleColumns, "h", "service", "handle_service_value");
            join = "LEFT JOIN handle h ON h.ROWID = m.handle_id";
        }
        else
        {
            select.Add("NULL AS handle_value");
            select.Add("NULL AS handle_service_value");
            join = string.Empty;
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
            var handle = SqliteArtifactHelpers.ReadString(reader, "handle_value");
            var service = SqliteArtifactHelpers.ReadString(reader, "service_value")
                          ?? SqliteArtifactHelpers.ReadString(reader, "handle_service_value");
            var isFromMe = SqliteArtifactHelpers.ReadInt64(reader, "is_from_me_value");

            artifacts.Add(new ArtifactRecord(
                SqliteArtifactHelpers.StableArtifactId(Name, source.RelativePath, rowId, guid),
                Kind,
                SqliteArtifactHelpers.AppleTimestamp(SqliteArtifactHelpers.ReadInt64(reader, "date_value")),
                handle,
                service,
                SqliteArtifactHelpers.ReadString(reader, "text_value"),
                isFromMe switch
                {
                    1 => "outgoing",
                    0 => "incoming",
                    _ => "unknown"
                },
                source.RelativePath,
                rowId,
                new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
                {
                    ["guid"] = guid,
                    ["subject"] = SqliteArtifactHelpers.ReadString(reader, "subject_value"),
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
}
