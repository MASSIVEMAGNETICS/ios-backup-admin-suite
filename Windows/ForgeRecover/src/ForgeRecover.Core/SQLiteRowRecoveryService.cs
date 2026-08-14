namespace ForgeRecover.Core;

/// <summary>
/// Canonical high-level row recovery service. The raw engine identifies the final
/// committed frame for each WAL page as current state. This service additionally
/// models temporal provenance: when that final page image originated in an earlier
/// committed transaction and later transactions committed elsewhere, it can be
/// emitted as a historical observation that remained current.
/// </summary>
public sealed class SQLiteRowRecoveryService
{
    public async Task<SQLiteRowRecoveryReport> RecoverAsync(
        string databasePath,
        string? walPath = null,
        SQLiteRowRecoveryOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new SQLiteRowRecoveryOptions();

        // The raw engine must expose current WAL rows so this layer can determine
        // which final page images originated before a later committed transaction.
        var rawOptions = new SQLiteRowRecoveryOptions
        {
            MaximumRows = options.MaximumRows,
            MaximumWalFrames = options.MaximumWalFrames,
            MaximumOverflowPagesPerRecord = options.MaximumOverflowPagesPerRecord,
            MaximumTextCharactersPerValue = options.MaximumTextCharactersPerValue,
            MaximumFreelistPagesToTraverse = options.MaximumFreelistPagesToTraverse,
            IncludeCurrentWalRows = options.IncludeCurrentWalRows || options.IncludeHistoricalRowsStillCurrent,
            IncludeHistoricalRowsStillCurrent = false,
            IncludeFreelistCandidates = options.IncludeFreelistCandidates,
            IncludeBTreeFreeSpaceCandidates = options.IncludeBTreeFreeSpaceCandidates,
            ScanUnstructuredFreelistPages = options.ScanUnstructuredFreelistPages
        };

        var raw = await new SQLiteRowRecoveryEngine()
            .RecoverAsync(databasePath, walPath, rawOptions, cancellationToken)
            .ConfigureAwait(false);

        if (!options.IncludeHistoricalRowsStillCurrent)
        {
            if (options.IncludeCurrentWalRows)
                return raw;

            return raw with
            {
                Rows = raw.Rows
                    .Where(row => row.SourceKind != SQLiteRowRecoverySourceKind.WalCurrentRow)
                    .ToArray()
            };
        }

        var normalized = new List<SQLiteRecoveredRow>(raw.Rows.Count * 2);
        foreach (var row in raw.Rows)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (row.SourceKind != SQLiteRowRecoverySourceKind.WalCurrentRow)
            {
                normalized.Add(row);
                continue;
            }

            // Preserve the explicit current-state representation only when requested.
            if (options.IncludeCurrentWalRows)
                normalized.Add(row);

            // If this page's final WAL image was committed before a later transaction,
            // it is simultaneously a valid historical observation and the final current
            // image for that page. Compare-by-hash has already established that the row
            // remains present in the current logical database state.
            if (row.WalTransactionIndex is not null
                && row.WalTransactionIndex.Value < raw.WalTransactionsCommitted - 1)
            {
                var metadata = new Dictionary<string, string?>(row.Metadata, StringComparer.OrdinalIgnoreCase)
                {
                    ["temporal_role"] = "historical_observation_that_remained_current",
                    ["original_source_kind"] = row.SourceKind.ToString(),
                    ["deleted_claim"] = "not_asserted_without_artifact_correlation"
                };

                normalized.Add(row with
                {
                    RecoveryId = TemporalRecoveryId(row),
                    SourceKind = SQLiteRowRecoverySourceKind.WalHistoricalRow,
                    Confidence = Math.Min(row.Confidence, .94),
                    RecoveryStatus = "historical_row_still_current",
                    Metadata = metadata
                });
            }
        }

        return raw with
        {
            Rows = normalized
                .OrderBy(row => row.WalTransactionIndex ?? int.MaxValue)
                .ThenBy(row => row.WalFrameIndex ?? int.MaxValue)
                .ThenBy(row => row.PageNumber)
                .ThenBy(row => row.RowId)
                .Take(options.MaximumRows)
                .ToArray(),
            Truncated = raw.Truncated || normalized.Count > options.MaximumRows
        };
    }

    private static string TemporalRecoveryId(SQLiteRecoveredRow row)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(
            $"{row.RecoveryId}|historical-still-current|{row.WalTransactionIndex}");
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant();
    }
}
