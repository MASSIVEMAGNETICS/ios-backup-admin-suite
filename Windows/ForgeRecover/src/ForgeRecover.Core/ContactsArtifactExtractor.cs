using System.Globalization;
using Microsoft.Data.Sqlite;

namespace ForgeRecover.Core;

public sealed class ContactsArtifactExtractor : IArtifactExtractor
{
    public string Name => "contacts";
    public ArtifactKind Kind => ArtifactKind.Contact;

    public async Task<ExtractionResult> ExtractAsync(
        ExtractionContext context,
        CancellationToken cancellationToken = default)
    {
        using var resolver = new ManifestDbResolver(context.BackupRoot);
        var source = resolver.ResolveFirst(KnownBackupArtifacts.Contacts);
        if (source is null)
        {
            return Empty("Contacts database is not present in this backup.");
        }

        var databasePath = await new BackupWorkingCopyService()
            .PrepareSqliteSetAsync(
                resolver,
                source,
                Path.Combine(context.WorkingRoot, Name),
                cancellationToken)
            .ConfigureAwait(false);
        using var connection = ManifestDbResolver.OpenReadOnly(databasePath);
        if (!SqliteArtifactHelpers.TableExists(connection, "ABPerson"))
        {
            return Empty("Unsupported contacts schema. Expected ABPerson.", source.RelativePath);
        }

        var values = ReadMultiValues(connection);
        var columns = SqliteArtifactHelpers.Columns(connection, "ABPerson");
        var select = new List<string> { "ROWID AS row_id" };
        SqliteArtifactHelpers.AddOptionalColumn(select, columns, string.Empty, "First", "first_value");
        SqliteArtifactHelpers.AddOptionalColumn(select, columns, string.Empty, "Last", "last_value");
        SqliteArtifactHelpers.AddOptionalColumn(select, columns, string.Empty, "Middle", "middle_value");
        SqliteArtifactHelpers.AddOptionalColumn(select, columns, string.Empty, "Organization", "organization_value");
        SqliteArtifactHelpers.AddOptionalColumn(select, columns, string.Empty, "Nickname", "nickname_value");
        SqliteArtifactHelpers.AddOptionalColumn(select, columns, string.Empty, "Note", "note_value");
        SqliteArtifactHelpers.AddOptionalColumn(select, columns, string.Empty, "CreationDate", "creation_value");
        SqliteArtifactHelpers.AddOptionalColumn(select, columns, string.Empty, "ModificationDate", "modification_value");

        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {string.Join(", ", select)} FROM ABPerson ORDER BY ROWID;";
        using var reader = command.ExecuteReader();
        var artifacts = new List<ArtifactRecord>();
        while (reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var rowId = SqliteArtifactHelpers.ReadInt64(reader, "row_id");
            var name = string.Join(
                ' ',
                new[]
                {
                    SqliteArtifactHelpers.ReadString(reader, "first_value"),
                    SqliteArtifactHelpers.ReadString(reader, "middle_value"),
                    SqliteArtifactHelpers.ReadString(reader, "last_value")
                }.Where(part => !string.IsNullOrWhiteSpace(part)));
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
                    ["modified_utc"] = SqliteArtifactHelpers.AppleTimestamp(
                        SqliteArtifactHelpers.ReadDouble(reader, "modification_value"))?.ToString("O", CultureInfo.InvariantCulture),
                    ["recovery_status"] = "present_in_backup_database"
                }));
        }

        return new ExtractionResult(Name, source.RelativePath, artifacts.Count, Array.Empty<string>(), artifacts);
    }

    private static Dictionary<long, string[]> ReadMultiValues(SqliteConnection connection)
    {
        var grouped = new Dictionary<long, List<string>>();
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

            if (!grouped.TryGetValue(recordId, out var list))
            {
                list = new List<string>();
                grouped[recordId] = list;
            }

            list.Add(value);
        }

        return grouped.ToDictionary(
            pair => pair.Key,
            pair => pair.Value.Distinct(StringComparer.OrdinalIgnoreCase).ToArray());
    }

    private ExtractionResult Empty(string warning, string source = "") =>
        new(Name, source, 0, new[] { warning }, Array.Empty<ArtifactRecord>());
}
