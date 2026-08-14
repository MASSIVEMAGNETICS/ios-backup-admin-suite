using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

namespace ForgeRecover.Core;

public sealed record IOSArtifactCorrelationReport(
    string DatabasePath,
    int InputRows,
    int MessageCandidates,
    int CallCandidates,
    int ContactCandidates,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<ArtifactRecord> Artifacts);

public sealed class IOSHistoricalArtifactCorrelator
{
    public IOSArtifactCorrelationReport Correlate(
        string databasePath,
        SQLiteRowRecoveryReport rowReport)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        ArgumentNullException.ThrowIfNull(rowReport);
        var dbPath = Path.GetFullPath(databasePath);
        var warnings = new List<string>();
        var artifacts = new List<ArtifactRecord>();

        var currentHandles = ReadCurrentHandles(dbPath, warnings);
        var historicalHandles = ReadRecoveredHandles(rowReport.Rows);
        var currentMultiValues = ReadCurrentContactMultiValues(dbPath, warnings);
        var historicalMultiValues = ReadRecoveredContactMultiValues(rowReport.Rows);

        foreach (var row in rowReport.Rows)
        {
            if (row.TableName is null) continue;
            if (row.TableName.Equals("message", StringComparison.OrdinalIgnoreCase))
            {
                var artifact = CorrelateMessage(row, currentHandles, historicalHandles);
                if (artifact is not null) artifacts.Add(artifact);
            }
            else if (row.TableName.Equals("ZCALLRECORD", StringComparison.OrdinalIgnoreCase))
            {
                var artifact = CorrelateCall(row);
                if (artifact is not null) artifacts.Add(artifact);
            }
            else if (row.TableName.Equals("ABPerson", StringComparison.OrdinalIgnoreCase))
            {
                var artifact = CorrelateContact(row, currentMultiValues, historicalMultiValues);
                if (artifact is not null) artifacts.Add(artifact);
            }
        }

        var messages = artifacts.Count(artifact => artifact.Kind == ArtifactKind.Message);
        var calls = artifacts.Count(artifact => artifact.Kind == ArtifactKind.Call);
        var contacts = artifacts.Count(artifact => artifact.Kind == ArtifactKind.Contact);
        return new IOSArtifactCorrelationReport(
            dbPath,
            rowReport.Rows.Count,
            messages,
            calls,
            contacts,
            warnings,
            artifacts);
    }

    private static ArtifactRecord? CorrelateMessage(
        SQLiteRecoveredRow row,
        IReadOnlyDictionary<long, HandleInfo> currentHandles,
        IReadOnlyDictionary<long, HandleInfo> historicalHandles)
    {
        var columns = Columns(row);
        if (!HasAny(columns, "guid", "text", "date", "handle_id", "service", "is_from_me", "attributedBody", "attributed_body"))
            return null;

        var guid = Text(columns, "guid");
        var body = Text(columns, "text");
        var subject = Text(columns, "subject");
        var service = Text(columns, "service");
        var handleId = Integer(columns, "handle_id");
        var isFromMe = Integer(columns, "is_from_me");
        var date = Timestamp(columns, "date");

        HandleInfo? handle = null;
        if (handleId is not null)
        {
            if (!historicalHandles.TryGetValue(handleId.Value, out handle))
                currentHandles.TryGetValue(handleId.Value, out handle);
        }
        service ??= handle?.Service;

        var attributed = First(columns, "attributedBody", "attributed_body");
        var attributedBlobSha = attributed?.StorageClass == SQLiteDecodedStorageClass.Blob
            ? attributed.BlobSha256
            : null;
        if (string.IsNullOrWhiteSpace(body)
            && string.IsNullOrWhiteSpace(guid)
            && string.IsNullOrWhiteSpace(subject)
            && string.IsNullOrWhiteSpace(attributedBlobSha)
            && date is null)
            return null;

        var metadata = BaseMetadata(row);
        metadata["correlation"] = "ios_messages_message_table";
        metadata["guid"] = guid;
        metadata["subject"] = subject;
        metadata["handle_id"] = handleId?.ToString(CultureInfo.InvariantCulture);
        metadata["attributed_body_sha256"] = attributedBlobSha;
        metadata["message_candidate_status"] = CandidateStatus("message", row.RecoveryStatus);
        metadata["deleted_message_claim"] = "not_asserted";

        return new ArtifactRecord(
            ArtifactId(row, ArtifactKind.Message),
            ArtifactKind.Message,
            date,
            handle?.Identifier,
            service,
            body,
            isFromMe switch
            {
                1 => "outgoing",
                0 => "incoming",
                _ => "unknown"
            },
            row.SourceFile,
            row.RowId,
            metadata);
    }

    private static ArtifactRecord? CorrelateCall(SQLiteRecoveredRow row)
    {
        var columns = Columns(row);
        if (!HasAny(columns, "ZDATE", "ZADDRESS", "ZDURATION", "ZORIGINATED", "ZUNIQUE_ID", "ZCALLTYPE"))
            return null;

        var address = Text(columns, "ZADDRESS");
        var name = Text(columns, "ZNAME");
        var uniqueId = Text(columns, "ZUNIQUE_ID");
        var duration = Number(columns, "ZDURATION");
        var originated = Integer(columns, "ZORIGINATED");
        var date = Timestamp(columns, "ZDATE");
        if (string.IsNullOrWhiteSpace(address) && string.IsNullOrWhiteSpace(uniqueId) && date is null)
            return null;

        var metadata = BaseMetadata(row);
        metadata["correlation"] = "ios_callhistory_zcallrecord";
        metadata["duration_seconds"] = duration?.ToString("0.###", CultureInfo.InvariantCulture);
        metadata["answered"] = Scalar(columns, "ZANSWERED");
        metadata["call_type"] = Scalar(columns, "ZCALLTYPE");
        metadata["unique_id"] = uniqueId;
        metadata["call_candidate_status"] = CandidateStatus("call", row.RecoveryStatus);
        metadata["deleted_call_claim"] = "not_asserted";

        return new ArtifactRecord(
            ArtifactId(row, ArtifactKind.Call),
            ArtifactKind.Call,
            date,
            address,
            name,
            duration?.ToString("0.###", CultureInfo.InvariantCulture),
            originated switch
            {
                1 => "outgoing",
                0 => "incoming",
                _ => "unknown"
            },
            row.SourceFile,
            row.RowId,
            metadata);
    }

    private static ArtifactRecord? CorrelateContact(
        SQLiteRecoveredRow row,
        IReadOnlyDictionary<long, string[]> currentMultiValues,
        IReadOnlyDictionary<long, string[]> historicalMultiValues)
    {
        var columns = Columns(row);
        if (!HasAny(columns, "First", "Last", "Middle", "Organization", "Nickname", "Note"))
            return null;

        var first = Text(columns, "First");
        var middle = Text(columns, "Middle");
        var last = Text(columns, "Last");
        var organization = Text(columns, "Organization");
        var nickname = Text(columns, "Nickname");
        var note = Text(columns, "Note");
        var name = string.Join(' ', new[] { first, middle, last }.Where(value => !string.IsNullOrWhiteSpace(value)));
        var values = historicalMultiValues.TryGetValue(row.RowId, out var recoveredValues)
            ? recoveredValues
            : currentMultiValues.TryGetValue(row.RowId, out var currentValues)
                ? currentValues
                : Array.Empty<string>();
        if (string.IsNullOrWhiteSpace(name) && string.IsNullOrWhiteSpace(organization) && values.Length == 0)
            return null;

        var metadata = BaseMetadata(row);
        metadata["correlation"] = "ios_addressbook_abperson";
        metadata["nickname"] = nickname;
        metadata["values"] = string.Join(" | ", values);
        metadata["modified_utc"] = Timestamp(columns, "ModificationDate")?.ToString("O", CultureInfo.InvariantCulture);
        metadata["contact_candidate_status"] = CandidateStatus("contact", row.RecoveryStatus);
        metadata["deleted_contact_claim"] = "not_asserted";

        return new ArtifactRecord(
            ArtifactId(row, ArtifactKind.Contact),
            ArtifactKind.Contact,
            Timestamp(columns, "CreationDate"),
            string.IsNullOrWhiteSpace(name) ? null : name,
            organization,
            note,
            null,
            row.SourceFile,
            row.RowId,
            metadata);
    }

    private static Dictionary<long, HandleInfo> ReadCurrentHandles(string databasePath, List<string> warnings)
    {
        var result = new Dictionary<long, HandleInfo>();
        try
        {
            using var connection = ManifestDbResolver.OpenReadOnly(databasePath);
            if (!SqliteArtifactHelpers.TableExists(connection, "handle")) return result;
            var columns = SqliteArtifactHelpers.Columns(connection, "handle");
            if (!columns.Contains("id")) return result;
            using var command = connection.CreateCommand();
            command.CommandText = columns.Contains("service")
                ? "SELECT ROWID, id, service FROM handle WHERE id IS NOT NULL;"
                : "SELECT ROWID, id, NULL AS service FROM handle WHERE id IS NOT NULL;";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var rowId = Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture);
                var identifier = Convert.ToString(reader.GetValue(1), CultureInfo.InvariantCulture);
                var service = reader.IsDBNull(2) ? null : Convert.ToString(reader.GetValue(2), CultureInfo.InvariantCulture);
                if (!string.IsNullOrWhiteSpace(identifier)) result[rowId] = new HandleInfo(identifier, service);
            }
        }
        catch (SqliteException exception)
        {
            warnings.Add($"Current handle table could not be read: {exception.Message}");
        }
        return result;
    }

    private static Dictionary<long, HandleInfo> ReadRecoveredHandles(IEnumerable<SQLiteRecoveredRow> rows)
    {
        var result = new Dictionary<long, HandleInfo>();
        foreach (var row in rows.Where(row => string.Equals(row.TableName, "handle", StringComparison.OrdinalIgnoreCase)))
        {
            var columns = Columns(row);
            var identifier = Text(columns, "id");
            if (string.IsNullOrWhiteSpace(identifier)) continue;
            result[row.RowId] = new HandleInfo(identifier, Text(columns, "service"));
        }
        return result;
    }

    private static Dictionary<long, string[]> ReadCurrentContactMultiValues(string databasePath, List<string> warnings)
    {
        var grouped = new Dictionary<long, List<string>>();
        try
        {
            using var connection = ManifestDbResolver.OpenReadOnly(databasePath);
            if (!SqliteArtifactHelpers.TableExists(connection, "ABMultiValue")) return new Dictionary<long, string[]>();
            var columns = SqliteArtifactHelpers.Columns(connection, "ABMultiValue");
            if (!columns.Contains("record_id") || !columns.Contains("value")) return new Dictionary<long, string[]>();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT record_id, value FROM ABMultiValue WHERE value IS NOT NULL;";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var recordId = Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture);
                var value = Convert.ToString(reader.GetValue(1), CultureInfo.InvariantCulture);
                if (string.IsNullOrWhiteSpace(value)) continue;
                if (!grouped.TryGetValue(recordId, out var list)) grouped[recordId] = list = new List<string>();
                list.Add(value);
            }
        }
        catch (SqliteException exception)
        {
            warnings.Add($"Current ABMultiValue table could not be read: {exception.Message}");
        }
        return grouped.ToDictionary(
            pair => pair.Key,
            pair => pair.Value.Distinct(StringComparer.OrdinalIgnoreCase).ToArray());
    }

    private static Dictionary<long, string[]> ReadRecoveredContactMultiValues(IEnumerable<SQLiteRecoveredRow> rows)
    {
        var grouped = new Dictionary<long, List<string>>();
        foreach (var row in rows.Where(row => string.Equals(row.TableName, "ABMultiValue", StringComparison.OrdinalIgnoreCase)))
        {
            var columns = Columns(row);
            var recordId = Integer(columns, "record_id");
            var value = Text(columns, "value");
            if (recordId is null || string.IsNullOrWhiteSpace(value)) continue;
            if (!grouped.TryGetValue(recordId.Value, out var list)) grouped[recordId.Value] = list = new List<string>();
            list.Add(value);
        }
        return grouped.ToDictionary(
            pair => pair.Key,
            pair => pair.Value.Distinct(StringComparer.OrdinalIgnoreCase).ToArray());
    }

    private static Dictionary<string, SQLiteDecodedValue> Columns(SQLiteRecoveredRow row) =>
        row.Columns
            .Where(column => !string.IsNullOrWhiteSpace(column.Name))
            .GroupBy(column => column.Name!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First().Value, StringComparer.OrdinalIgnoreCase);

    private static bool HasAny(IReadOnlyDictionary<string, SQLiteDecodedValue> columns, params string[] names) =>
        names.Any(columns.ContainsKey);

    private static SQLiteDecodedValue? First(IReadOnlyDictionary<string, SQLiteDecodedValue> columns, params string[] names)
    {
        foreach (var name in names)
            if (columns.TryGetValue(name, out var value)) return value;
        return null;
    }

    private static string? Text(IReadOnlyDictionary<string, SQLiteDecodedValue> columns, string name)
    {
        if (!columns.TryGetValue(name, out var value)) return null;
        return value.StorageClass switch
        {
            SQLiteDecodedStorageClass.Text => value.TextValue,
            SQLiteDecodedStorageClass.Integer => value.IntegerValue?.ToString(CultureInfo.InvariantCulture),
            SQLiteDecodedStorageClass.Real => value.RealValue?.ToString("R", CultureInfo.InvariantCulture),
            _ => null
        };
    }

    private static long? Integer(IReadOnlyDictionary<string, SQLiteDecodedValue> columns, string name)
    {
        if (!columns.TryGetValue(name, out var value)) return null;
        return value.StorageClass switch
        {
            SQLiteDecodedStorageClass.Integer => value.IntegerValue,
            SQLiteDecodedStorageClass.Real when value.RealValue is >= long.MinValue and <= long.MaxValue => (long)value.RealValue.Value,
            _ => null
        };
    }

    private static double? Number(IReadOnlyDictionary<string, SQLiteDecodedValue> columns, string name)
    {
        if (!columns.TryGetValue(name, out var value)) return null;
        return value.StorageClass switch
        {
            SQLiteDecodedStorageClass.Integer => value.IntegerValue,
            SQLiteDecodedStorageClass.Real => value.RealValue,
            _ => null
        };
    }

    private static string? Scalar(IReadOnlyDictionary<string, SQLiteDecodedValue> columns, string name) =>
        Text(columns, name);

    private static DateTimeOffset? Timestamp(IReadOnlyDictionary<string, SQLiteDecodedValue> columns, string name)
    {
        if (!columns.TryGetValue(name, out var value)) return null;
        return value.StorageClass switch
        {
            SQLiteDecodedStorageClass.Integer => SqliteArtifactHelpers.AppleTimestamp(value.IntegerValue),
            SQLiteDecodedStorageClass.Real => SqliteArtifactHelpers.AppleTimestamp(value.RealValue),
            _ => null
        };
    }

    private static Dictionary<string, string?> BaseMetadata(SQLiteRecoveredRow row) =>
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["recovery_status"] = row.RecoveryStatus,
            ["evidence_semantics"] = "historical_ios_artifact_candidate",
            ["deleted_claim"] = "not_asserted",
            ["row_recovery_id"] = row.RecoveryId,
            ["row_source_kind"] = row.SourceKind.ToString(),
            ["page_number"] = row.PageNumber.ToString(CultureInfo.InvariantCulture),
            ["wal_frame_index"] = row.WalFrameIndex?.ToString(CultureInfo.InvariantCulture),
            ["wal_transaction_index"] = row.WalTransactionIndex?.ToString(CultureInfo.InvariantCulture),
            ["record_sha256"] = row.RecordSha256,
            ["schema_mapping"] = row.SchemaMapping,
            ["row_confidence"] = row.Confidence.ToString("0.000", CultureInfo.InvariantCulture)
        };

    private static string CandidateStatus(string artifact, string rowStatus) =>
        $"historical_{artifact}_candidate:{rowStatus}";

    private static string ArtifactId(SQLiteRecoveredRow row, ArtifactKind kind)
    {
        var material = $"ios-recovered-artifact|{kind}|{row.RecoveryId}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material))).ToLowerInvariant();
    }

    private sealed record HandleInfo(string Identifier, string? Service);
}
