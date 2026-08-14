using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.Win32.SafeHandles;

namespace ForgeRecover.Core;

public enum SQLiteRowRecoverySourceKind
{
    WalHistoricalRow,
    WalCurrentRow,
    FreelistLeafRowCandidate,
    BTreeFreeblockRowCandidate,
    BTreeUnallocatedRowCandidate
}

public sealed record SQLiteRecoveredColumn(
    int Index,
    string? Name,
    string? DeclaredType,
    SQLiteDecodedValue Value);

public sealed record SQLiteRecoveredRow(
    string RecoveryId,
    SQLiteRowRecoverySourceKind SourceKind,
    string SourceFile,
    long SourceOffset,
    uint PageNumber,
    int? WalFrameIndex,
    int? WalTransactionIndex,
    string? TableName,
    IReadOnlyList<string> SchemaCandidates,
    string SchemaMapping,
    long RowId,
    long PayloadLength,
    int LocalPayloadLength,
    int OverflowPagesRead,
    string RecordSha256,
    double Confidence,
    string RecoveryStatus,
    IReadOnlyList<SQLiteRecoveredColumn> Columns,
    IReadOnlyDictionary<string, string?> Metadata);

public sealed record SQLiteRowRecoveryReport(
    string DatabasePath,
    string? WalPath,
    int PageSize,
    int UsablePageSize,
    long DatabasePages,
    int WalFramesValidated,
    int WalTransactionsCommitted,
    int SchemaTablesLoaded,
    int CurrentRowsIndexed,
    bool Truncated,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<SQLiteRecoveredRow> Rows);

public sealed class SQLiteRowRecoveryOptions
{
    public int MaximumRows { get; init; } = 20_000;
    public int MaximumWalFrames { get; init; } = 250_000;
    public int MaximumOverflowPagesPerRecord { get; init; } = 16_384;
    public int MaximumTextCharactersPerValue { get; init; } = 1_000_000;
    public int MaximumFreelistPagesToTraverse { get; init; } = 1_000_000;
    public bool IncludeCurrentWalRows { get; init; }
    public bool IncludeHistoricalRowsStillCurrent { get; init; }
    public bool IncludeFreelistCandidates { get; init; } = true;
    public bool IncludeBTreeFreeSpaceCandidates { get; init; } = true;
    public bool ScanUnstructuredFreelistPages { get; init; } = true;

    internal void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(MaximumRows, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaximumWalFrames, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaximumOverflowPagesPerRecord, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaximumTextCharactersPerValue, 16);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaximumFreelistPagesToTraverse, 1);
    }
}

public sealed class SQLiteRowRecoveryEngine
{
    private static readonly byte[] SqliteMagic = "SQLite format 3\0"u8.ToArray();
    private const uint WalLittle = 0x377F0682;
    private const uint WalBig = 0x377F0683;

    public async Task<SQLiteRowRecoveryReport> RecoverAsync(
        string databasePath,
        string? walPath = null,
        SQLiteRowRecoveryOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        options ??= new SQLiteRowRecoveryOptions();
        options.Validate();

        var dbPath = Path.GetFullPath(databasePath);
        if (!File.Exists(dbPath)) throw new FileNotFoundException("SQLite database was not found.", dbPath);
        var warnings = new List<string>();
        var rows = new List<SQLiteRecoveredRow>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        var header = new byte[100];
        await using (var headerStream = OpenReadStream(dbPath))
        {
            if (await ReadExactAsync(headerStream, 0, header, cancellationToken).ConfigureAwait(false) != header.Length
                || !header.AsSpan(0, SqliteMagic.Length).SequenceEqual(SqliteMagic))
                throw new InvalidDataException("Input does not contain a valid SQLite 3 database header.");
        }

        var pageSize = ReadPageSize(header);
        var reservedBytes = header[20];
        if (reservedBytes >= pageSize || pageSize - reservedBytes < 480)
            throw new InvalidDataException("SQLite usable page size is invalid.");
        var usablePageSize = pageSize - reservedBytes;
        var databasePages = new FileInfo(dbPath).Length / pageSize;
        if (databasePages < 1) throw new InvalidDataException("SQLite database is shorter than one page.");
        var textEncoding = ReadTextEncoding(header, warnings);
        var codec = new SQLiteRecordCodec(
            usablePageSize,
            textEncoding,
            options.MaximumOverflowPagesPerRecord,
            options.MaximumTextCharactersPerValue);

        var resolvedWal = ResolveWalPath(dbPath, walPath);
        var wal = resolvedWal is null
            ? WalJournal.Empty
            : await ReadWalAsync(resolvedWal, pageSize, options.MaximumWalFrames, warnings, cancellationToken).ConfigureAwait(false);

        using var dbReader = new RandomPageReader(dbPath, pageSize);
        using var walReader = resolvedWal is null ? null : new RandomPageReader(resolvedWal, pageSize, walPageDataOffsetMode: true);
        var latestFrames = wal.Transactions
            .SelectMany(transaction => transaction.FinalFrames)
            .GroupBy(frame => frame.PageNumber)
            .ToDictionary(group => group.Key, group => group.OrderBy(frame => frame.FrameIndex).Last());

        ReadOnlyMemory<byte>? CurrentPage(uint pageNumber)
        {
            if (latestFrames.TryGetValue(pageNumber, out var frame) && walReader is not null)
                return walReader.ReadWalFramePage(frame.DataOffset);
            return dbReader.ReadDatabasePage(pageNumber);
        }

        var schema = LoadSchema(dbPath, warnings);
        var ownerMap = BuildCurrentPageOwnership(schema, CurrentPage, usablePageSize, warnings);
        var currentRows = BuildCurrentRowIndex(
            schema,
            ownerMap,
            CurrentPage,
            codec,
            usablePageSize,
            warnings);
        var currentRowCount = currentRows.Values.Sum(table => table.Count);

        if (wal.Transactions.Count > 0)
        {
            var historicalState = new Dictionary<uint, WalFrameRef>();
            foreach (var transaction in wal.Transactions)
            {
                cancellationToken.ThrowIfCancellationRequested();
                foreach (var frame in transaction.FinalFrames) historicalState[frame.PageNumber] = frame;

                ReadOnlyMemory<byte>? HistoricalPage(uint pageNumber)
                {
                    if (historicalState.TryGetValue(pageNumber, out var stateFrame) && walReader is not null)
                        return walReader.ReadWalFramePage(stateFrame.DataOffset);
                    return dbReader.ReadDatabasePage(pageNumber);
                }

                foreach (var frame in transaction.FinalFrames)
                {
                    if (rows.Count >= options.MaximumRows) break;
                    var isCurrent = latestFrames.TryGetValue(frame.PageNumber, out var latest)
                        && latest.FrameIndex == frame.FrameIndex;
                    if (isCurrent && !options.IncludeCurrentWalRows) continue;

                    var page = walReader?.ReadWalFramePage(frame.DataOffset);
                    if (page is null) continue;
                    var kind = isCurrent ? SQLiteRowRecoverySourceKind.WalCurrentRow : SQLiteRowRecoverySourceKind.WalHistoricalRow;
                    var baseConfidence = isCurrent ? .99 : .94;
                    DecodeLeafPage(
                        page.Value,
                        frame.PageNumber,
                        resolvedWal!,
                        frame.DataOffset,
                        frame.FrameIndex,
                        transaction.Index,
                        kind,
                        baseConfidence,
                        ownerMap,
                        schema,
                        currentRows,
                        HistoricalPage,
                        codec,
                        usablePageSize,
                        options,
                        rows,
                        seen,
                        warnings);
                }
                if (rows.Count >= options.MaximumRows) break;
            }
        }

        if (options.IncludeFreelistCandidates && rows.Count < options.MaximumRows)
        {
            var firstTrunk = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(32, 4));
            ScanFreelistCandidates(
                firstTrunk,
                dbReader,
                schema,
                currentRows,
                codec,
                usablePageSize,
                pageSize,
                options,
                rows,
                seen,
                warnings,
                cancellationToken);
        }

        if (options.IncludeBTreeFreeSpaceCandidates && rows.Count < options.MaximumRows)
        {
            ScanCurrentBTreeFreeSpace(
                dbPath,
                ownerMap,
                schema,
                currentRows,
                CurrentPage,
                codec,
                usablePageSize,
                pageSize,
                options,
                rows,
                seen,
                warnings,
                cancellationToken);
        }

        var truncated = rows.Count >= options.MaximumRows;
        if (truncated) warnings.Add($"Row recovery reached the configured cap of {options.MaximumRows} records.");
        return new SQLiteRowRecoveryReport(
            dbPath,
            resolvedWal,
            pageSize,
            usablePageSize,
            databasePages,
            wal.ValidatedFrames,
            wal.Transactions.Count,
            schema.Count,
            currentRowCount,
            truncated,
            warnings,
            rows);
    }

    private static List<TableSchema> LoadSchema(string databasePath, List<string> warnings)
    {
        var tables = new List<TableSchema>();
        try
        {
            using var connection = ManifestDbResolver.OpenReadOnly(databasePath);
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT name, rootpage, sql
                FROM sqlite_schema
                WHERE type = 'table' AND rootpage > 0 AND name NOT LIKE 'sqlite_%'
                ORDER BY rootpage;
                """;
            using var reader = command.ExecuteReader();
            var raw = new List<(string Name, int RootPage, string Sql)>();
            while (reader.Read())
                raw.Add((reader.GetString(0), reader.GetInt32(1), reader.IsDBNull(2) ? string.Empty : reader.GetString(2)));
            reader.Close();

            foreach (var item in raw)
            {
                if (item.Sql.StartsWith("CREATE VIRTUAL TABLE", StringComparison.OrdinalIgnoreCase))
                {
                    warnings.Add($"Virtual table {item.Name} is excluded from raw row reconstruction.");
                    continue;
                }
                if (item.Sql.Contains("WITHOUT ROWID", StringComparison.OrdinalIgnoreCase))
                {
                    warnings.Add($"WITHOUT ROWID table {item.Name} is not yet supported by table-leaf reconstruction.");
                    continue;
                }

                var columns = new List<ColumnSchema>();
                using var tableCommand = connection.CreateCommand();
                tableCommand.CommandText = $"PRAGMA table_xinfo({QuoteIdentifier(item.Name)});";
                using var columnReader = tableCommand.ExecuteReader();
                while (columnReader.Read())
                {
                    var hidden = columnReader.FieldCount > 6 && !columnReader.IsDBNull(6) ? columnReader.GetInt32(6) : 0;
                    if (hidden == 2) continue;
                    columns.Add(new ColumnSchema(
                        columnReader.GetInt32(0),
                        columnReader.GetString(1),
                        columnReader.IsDBNull(2) ? string.Empty : columnReader.GetString(2),
                        columnReader.IsDBNull(5) ? 0 : columnReader.GetInt32(5),
                        hidden));
                }
                tables.Add(new TableSchema(item.Name, item.RootPage, item.Sql, columns));
            }
        }
        catch (SqliteException exception)
        {
            warnings.Add($"Schema catalog could not be loaded through SQLite: {exception.Message}");
        }
        return tables;
    }

    private static Dictionary<uint, TableSchema> BuildCurrentPageOwnership(
        IReadOnlyList<TableSchema> schema,
        Func<uint, ReadOnlyMemory<byte>?> pageResolver,
        int usablePageSize,
        List<string> warnings)
    {
        var owners = new Dictionary<uint, TableSchema>();
        foreach (var table in schema)
        {
            var visited = new HashSet<uint>();
            var pending = new Stack<uint>();
            pending.Push(checked((uint)table.RootPage));
            while (pending.Count > 0)
            {
                var pageNumber = pending.Pop();
                if (!visited.Add(pageNumber)) continue;
                var page = pageResolver(pageNumber);
                if (page is null || page.Value.Length < usablePageSize) continue;
                owners.TryAdd(pageNumber, table);
                var span = page.Value.Span;
                var headerOffset = pageNumber == 1 ? 100 : 0;
                if (headerOffset >= usablePageSize) continue;
                var pageType = span[headerOffset];
                if (pageType == 0x0d) continue;
                if (pageType != 0x05 || headerOffset + 12 > usablePageSize) continue;

                var cellCount = BinaryPrimitives.ReadUInt16BigEndian(span.Slice(headerOffset + 3, 2));
                var pointerBase = headerOffset + 12;
                if (pointerBase + cellCount * 2 > usablePageSize)
                {
                    warnings.Add($"Table {table.Name} page {pageNumber} has an invalid interior cell-pointer array.");
                    continue;
                }
                for (var index = 0; index < cellCount; index++)
                {
                    var cellOffset = BinaryPrimitives.ReadUInt16BigEndian(span.Slice(pointerBase + index * 2, 2));
                    if (cellOffset == 0 || cellOffset + 4 > usablePageSize) continue;
                    var child = BinaryPrimitives.ReadUInt32BigEndian(span.Slice(cellOffset, 4));
                    if (child != 0) pending.Push(child);
                }
                var rightMost = BinaryPrimitives.ReadUInt32BigEndian(span.Slice(headerOffset + 8, 4));
                if (rightMost != 0) pending.Push(rightMost);
            }
        }
        return owners;
    }

    private static Dictionary<string, Dictionary<long, string>> BuildCurrentRowIndex(
        IReadOnlyList<TableSchema> schema,
        IReadOnlyDictionary<uint, TableSchema> ownerMap,
        Func<uint, ReadOnlyMemory<byte>?> pageResolver,
        SQLiteRecordCodec codec,
        int usablePageSize,
        List<string> warnings)
    {
        var index = new Dictionary<string, Dictionary<long, string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var table in schema) index[table.Name] = new Dictionary<long, string>();
        foreach (var owned in ownerMap)
        {
            var page = pageResolver(owned.Key);
            if (page is null || !IsTableLeaf(page.Value.Span, owned.Key, usablePageSize)) continue;
            foreach (var cell in EnumerateLeafCells(page.Value, owned.Key, usablePageSize, warnings))
            {
                if (!codec.TryDecodeTableLeafCell(page.Value, cell.Offset, pageResolver, out var record, out _, out _)
                    || record is null || record.Values.Count != owned.Value.Columns.Count) continue;
                index[owned.Value.Name][record.RowId] = record.CanonicalSha256;
            }
        }
        return index;
    }

    private static void DecodeLeafPage(
        ReadOnlyMemory<byte> page,
        uint pageNumber,
        string sourceFile,
        long pageSourceOffset,
        int? walFrameIndex,
        int? walTransactionIndex,
        SQLiteRowRecoverySourceKind sourceKind,
        double baseConfidence,
        IReadOnlyDictionary<uint, TableSchema> ownerMap,
        IReadOnlyList<TableSchema> schema,
        IReadOnlyDictionary<string, Dictionary<long, string>> currentRows,
        Func<uint, ReadOnlyMemory<byte>?> pageResolver,
        SQLiteRecordCodec codec,
        int usablePageSize,
        SQLiteRowRecoveryOptions options,
        List<SQLiteRecoveredRow> output,
        HashSet<string> seen,
        List<string> warnings)
    {
        if (!IsTableLeaf(page.Span, pageNumber, usablePageSize)) return;
        foreach (var cell in EnumerateLeafCells(page, pageNumber, usablePageSize, warnings))
        {
            if (output.Count >= options.MaximumRows) return;
            if (!codec.TryDecodeTableLeafCell(page, cell.Offset, pageResolver, out var record, out _, out var warning) || record is null)
                continue;
            if (!string.IsNullOrWhiteSpace(warning)) warnings.Add($"Page {pageNumber}, cell {cell.Offset}: {warning}");

            var mapping = MapSchema(pageNumber, record, ownerMap, schema);
            if (mapping.Table is not null && record.Values.Count != mapping.Table.Columns.Count) continue;
            var status = ClassifyHistoricalStatus(mapping.Table, record, currentRows, sourceKind);
            if (status == "historical_row_still_current" && !options.IncludeHistoricalRowsStillCurrent) continue;

            AddRecoveredRow(
                record,
                mapping,
                sourceKind,
                sourceFile,
                checked(pageSourceOffset + cell.Offset),
                pageNumber,
                walFrameIndex,
                walTransactionIndex,
                baseConfidence * mapping.ConfidenceMultiplier,
                status,
                output,
                seen);
        }
    }

    private static void ScanFreelistCandidates(
        uint firstTrunk,
        RandomPageReader dbReader,
        IReadOnlyList<TableSchema> schema,
        IReadOnlyDictionary<string, Dictionary<long, string>> currentRows,
        SQLiteRecordCodec codec,
        int usablePageSize,
        int pageSize,
        SQLiteRowRecoveryOptions options,
        List<SQLiteRecoveredRow> output,
        HashSet<string> seen,
        List<string> warnings,
        CancellationToken cancellationToken)
    {
        if (firstTrunk == 0) return;
        var visitedTrunks = new HashSet<uint>();
        var traversed = 0;
        var trunk = firstTrunk;
        while (trunk != 0 && output.Count < options.MaximumRows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!visitedTrunks.Add(trunk))
            {
                warnings.Add($"Freelist trunk cycle detected at page {trunk}.");
                break;
            }
            if (++traversed > options.MaximumFreelistPagesToTraverse)
            {
                warnings.Add("Freelist traversal reached the configured safety limit.");
                break;
            }
            var trunkPage = dbReader.ReadDatabasePage(trunk);
            if (trunkPage is null || trunkPage.Value.Length < usablePageSize) break;
            var span = trunkPage.Value.Span;
            var nextTrunk = BinaryPrimitives.ReadUInt32BigEndian(span.Slice(0, 4));
            var leafCount = BinaryPrimitives.ReadUInt32BigEndian(span.Slice(4, 4));
            var maxLeaves = (uint)Math.Max(0, usablePageSize / 4 - 2);
            if (leafCount > maxLeaves)
            {
                warnings.Add($"Freelist trunk {trunk} declares an invalid leaf count of {leafCount}.");
                break;
            }

            for (var index = 0; index < leafCount && output.Count < options.MaximumRows; index++)
            {
                var leaf = BinaryPrimitives.ReadUInt32BigEndian(span.Slice(8 + index * 4, 4));
                var leafPage = dbReader.ReadDatabasePage(leaf);
                if (leafPage is null || leafPage.Value.Length < usablePageSize) continue;
                if (IsTableLeaf(leafPage.Value.Span, leaf, usablePageSize))
                {
                    DecodeUnownedCandidateLeafPage(
                        leafPage.Value,
                        leaf,
                        dbReader.Path,
                        dbReader.ReadDatabasePage,
                        schema,
                        currentRows,
                        codec,
                        usablePageSize,
                        pageSize,
                        options,
                        output,
                        seen,
                        warnings);
                }
                else if (options.ScanUnstructuredFreelistPages)
                {
                    ScanCandidateCellRegion(
                        leafPage.Value,
                        0,
                        usablePageSize,
                        leaf,
                        dbReader.ReadDatabasePage,
                        schema,
                        null,
                        currentRows,
                        codec,
                        SQLiteRowRecoverySourceKind.FreelistLeafRowCandidate,
                        .48,
                        "freelist_unstructured_row_candidate",
                        dbReader.Path,
                        checked(((long)leaf - 1) * pageSize),
                        null,
                        null,
                        options,
                        output,
                        seen);
                }
            }
            trunk = nextTrunk;
        }
    }

    private static void DecodeUnownedCandidateLeafPage(
        ReadOnlyMemory<byte> page,
        uint pageNumber,
        string sourceFile,
        Func<uint, ReadOnlyMemory<byte>?> pageResolver,
        IReadOnlyList<TableSchema> schema,
        IReadOnlyDictionary<string, Dictionary<long, string>> currentRows,
        SQLiteRecordCodec codec,
        int usablePageSize,
        int pageSize,
        SQLiteRowRecoveryOptions options,
        List<SQLiteRecoveredRow> output,
        HashSet<string> seen,
        List<string> warnings)
    {
        foreach (var cell in EnumerateLeafCells(page, pageNumber, usablePageSize, warnings))
        {
            if (output.Count >= options.MaximumRows) return;
            if (!codec.TryDecodeTableLeafCell(page, cell.Offset, pageResolver, out var record, out _, out _) || record is null) continue;
            var mapping = MapSchema(pageNumber, record, new Dictionary<uint, TableSchema>(), schema);
            if (mapping.Table is null && mapping.Candidates.Count == 0) continue;
            var status = ClassifyCandidateStatus(mapping.Table, record, currentRows, "freelist_leaf_row_candidate");
            AddRecoveredRow(
                record,
                mapping,
                SQLiteRowRecoverySourceKind.FreelistLeafRowCandidate,
                sourceFile,
                checked(((long)pageNumber - 1) * pageSize + cell.Offset),
                pageNumber,
                null,
                null,
                .80 * mapping.ConfidenceMultiplier,
                status,
                output,
                seen);
        }
    }

    private static void ScanCurrentBTreeFreeSpace(
        string databasePath,
        IReadOnlyDictionary<uint, TableSchema> ownerMap,
        IReadOnlyList<TableSchema> schema,
        IReadOnlyDictionary<string, Dictionary<long, string>> currentRows,
        Func<uint, ReadOnlyMemory<byte>?> pageResolver,
        SQLiteRecordCodec codec,
        int usablePageSize,
        int pageSize,
        SQLiteRowRecoveryOptions options,
        List<SQLiteRecoveredRow> output,
        HashSet<string> seen,
        List<string> warnings,
        CancellationToken cancellationToken)
    {
        foreach (var owned in ownerMap)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (output.Count >= options.MaximumRows) break;
            var page = pageResolver(owned.Key);
            if (page is null || !IsTableLeaf(page.Value.Span, owned.Key, usablePageSize)) continue;
            var span = page.Value.Span;
            var headerOffset = owned.Key == 1 ? 100 : 0;
            var cellCount = BinaryPrimitives.ReadUInt16BigEndian(span.Slice(headerOffset + 3, 2));
            var rawContent = BinaryPrimitives.ReadUInt16BigEndian(span.Slice(headerOffset + 5, 2));
            var contentStart = rawContent == 0 ? 65_536 : rawContent;
            var pointerEnd = headerOffset + 8 + cellCount * 2;
            if (contentStart > pointerEnd && contentStart <= usablePageSize)
            {
                ScanCandidateCellRegion(
                    page.Value,
                    pointerEnd,
                    contentStart,
                    owned.Key,
                    pageResolver,
                    schema,
                    owned.Value,
                    currentRows,
                    codec,
                    SQLiteRowRecoverySourceKind.BTreeUnallocatedRowCandidate,
                    .60,
                    "btree_unallocated_row_candidate",
                    databasePath,
                    checked(((long)owned.Key - 1) * pageSize),
                    null,
                    null,
                    options,
                    output,
                    seen);
            }

            var freeblock = BinaryPrimitives.ReadUInt16BigEndian(span.Slice(headerOffset + 1, 2));
            var visited = new HashSet<ushort>();
            while (freeblock != 0 && output.Count < options.MaximumRows)
            {
                if (!visited.Add(freeblock) || freeblock + 4 > usablePageSize)
                {
                    warnings.Add($"Invalid/cyclic freeblock chain on page {owned.Key}.");
                    break;
                }
                var next = BinaryPrimitives.ReadUInt16BigEndian(span.Slice(freeblock, 2));
                var size = BinaryPrimitives.ReadUInt16BigEndian(span.Slice(freeblock + 2, 2));
                if (size < 5 || freeblock + size > usablePageSize)
                {
                    warnings.Add($"Invalid freeblock size on page {owned.Key}.");
                    break;
                }
                ScanCandidateCellRegion(
                    page.Value,
                    freeblock + 4,
                    freeblock + size,
                    owned.Key,
                    pageResolver,
                    schema,
                    owned.Value,
                    currentRows,
                    codec,
                    SQLiteRowRecoverySourceKind.BTreeFreeblockRowCandidate,
                    .42,
                    "btree_freeblock_row_candidate",
                    databasePath,
                    checked(((long)owned.Key - 1) * pageSize),
                    null,
                    null,
                    options,
                    output,
                    seen);
                freeblock = next;
            }
        }
    }

    private static void ScanCandidateCellRegion(
        ReadOnlyMemory<byte> page,
        int start,
        int end,
        uint pageNumber,
        Func<uint, ReadOnlyMemory<byte>?> pageResolver,
        IReadOnlyList<TableSchema> schema,
        TableSchema? knownTable,
        IReadOnlyDictionary<string, Dictionary<long, string>> currentRows,
        SQLiteRecordCodec codec,
        SQLiteRowRecoverySourceKind sourceKind,
        double baseConfidence,
        string defaultStatus,
        string sourceFile,
        long pageSourceOffset,
        int? walFrameIndex,
        int? walTransactionIndex,
        SQLiteRowRecoveryOptions options,
        List<SQLiteRecoveredRow> output,
        HashSet<string> seen)
    {
        start = Math.Clamp(start, 0, page.Length);
        end = Math.Clamp(end, start, page.Length);
        for (var offset = start; offset < end && output.Count < options.MaximumRows; offset++)
        {
            if (!codec.TryDecodeTableLeafCell(page, offset, pageResolver, out var record, out var consumed, out _) || record is null)
                continue;
            if (consumed < 4 || offset > end - consumed) continue;
            var mapping = knownTable is not null
                ? record.Values.Count == knownTable.Columns.Count
                    ? new SchemaMapping(knownTable, new[] { knownTable.Name }, "current_btree_page_owner", 1.0)
                    : SchemaMapping.None
                : MapSchema(pageNumber, record, new Dictionary<uint, TableSchema>(), schema);
            if (mapping.Table is null && mapping.Candidates.Count == 0) continue;
            if (!PlausibleCandidate(record)) continue;

            var status = ClassifyCandidateStatus(mapping.Table, record, currentRows, defaultStatus);
            AddRecoveredRow(
                record,
                mapping,
                sourceKind,
                sourceFile,
                checked(pageSourceOffset + offset),
                pageNumber,
                walFrameIndex,
                walTransactionIndex,
                baseConfidence * mapping.ConfidenceMultiplier,
                status,
                output,
                seen);
            offset += Math.Max(0, Math.Min(consumed - 1, end - offset - 1));
        }
    }

    private static bool PlausibleCandidate(SQLiteDecodedRecord record)
    {
        if (record.Values.Count is < 1 or > 1024 || record.PayloadLength < 2) return false;
        var meaningful = 0;
        foreach (var value in record.Values)
        {
            if (value.StorageClass == SQLiteDecodedStorageClass.Text)
            {
                if (string.IsNullOrWhiteSpace(value.TextValue)) continue;
                if (value.TextValue.Any(character => char.IsControl(character) && character is not ('\r' or '\n' or '\t'))) return false;
                meaningful++;
            }
            else if (value.StorageClass is SQLiteDecodedStorageClass.Integer or SQLiteDecodedStorageClass.Real or SQLiteDecodedStorageClass.Blob)
            {
                meaningful++;
            }
        }
        return meaningful > 0;
    }

    private static SchemaMapping MapSchema(
        uint pageNumber,
        SQLiteDecodedRecord record,
        IReadOnlyDictionary<uint, TableSchema> ownerMap,
        IReadOnlyList<TableSchema> schema)
    {
        if (ownerMap.TryGetValue(pageNumber, out var owner))
        {
            return record.Values.Count == owner.Columns.Count
                ? new SchemaMapping(owner, new[] { owner.Name }, "current_btree_page_owner", 1.0)
                : SchemaMapping.None;
        }

        var root = schema.FirstOrDefault(table => table.RootPage == pageNumber && table.Columns.Count == record.Values.Count);
        if (root is not null) return new SchemaMapping(root, new[] { root.Name }, "schema_root_page", 1.0);

        var candidates = schema
            .Where(table => table.Columns.Count == record.Values.Count)
            .Select(table => table.Name)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (candidates.Length == 1)
        {
            var table = schema.First(item => item.Name.Equals(candidates[0], StringComparison.OrdinalIgnoreCase));
            return new SchemaMapping(table, candidates, "unique_column_count_match", .72);
        }
        return candidates.Length == 0
            ? SchemaMapping.None
            : new SchemaMapping(null, candidates, "ambiguous_column_count_match", .45);
    }

    private static string ClassifyHistoricalStatus(
        TableSchema? table,
        SQLiteDecodedRecord record,
        IReadOnlyDictionary<string, Dictionary<long, string>> currentRows,
        SQLiteRowRecoverySourceKind sourceKind)
    {
        if (sourceKind == SQLiteRowRecoverySourceKind.WalCurrentRow) return "wal_current_row";
        if (table is null) return "historical_row_candidate_unmapped";
        if (!currentRows.TryGetValue(table.Name, out var tableRows) || !tableRows.TryGetValue(record.RowId, out var currentHash))
            return "historical_row_absent_current";
        return string.Equals(currentHash, record.CanonicalSha256, StringComparison.Ordinal)
            ? "historical_row_still_current"
            : "historical_row_version";
    }

    private static string ClassifyCandidateStatus(
        TableSchema? table,
        SQLiteDecodedRecord record,
        IReadOnlyDictionary<string, Dictionary<long, string>> currentRows,
        string fallback)
    {
        if (table is null) return fallback + "_unmapped";
        if (!currentRows.TryGetValue(table.Name, out var tableRows) || !tableRows.TryGetValue(record.RowId, out var currentHash))
            return fallback + "_absent_current";
        return string.Equals(currentHash, record.CanonicalSha256, StringComparison.Ordinal)
            ? fallback + "_duplicates_current"
            : fallback + "_historical_version";
    }

    private static void AddRecoveredRow(
        SQLiteDecodedRecord record,
        SchemaMapping mapping,
        SQLiteRowRecoverySourceKind sourceKind,
        string sourceFile,
        long sourceOffset,
        uint pageNumber,
        int? walFrameIndex,
        int? walTransactionIndex,
        double confidence,
        string status,
        List<SQLiteRecoveredRow> output,
        HashSet<string> seen)
    {
        var tableName = mapping.Table?.Name;
        var logicalKey = $"{sourceKind}|{tableName}|{record.RowId}|{record.CanonicalSha256}";
        if (!seen.Add(logicalKey)) return;
        var columns = new List<SQLiteRecoveredColumn>(record.Values.Count);
        for (var index = 0; index < record.Values.Count; index++)
        {
            var schemaColumn = mapping.Table is not null && index < mapping.Table.Columns.Count ? mapping.Table.Columns[index] : null;
            columns.Add(new SQLiteRecoveredColumn(index, schemaColumn?.Name, schemaColumn?.DeclaredType, record.Values[index]));
        }
        var idMaterial = $"{Path.GetFileName(sourceFile)}|{sourceKind}|{sourceOffset}|{tableName}|{record.RowId}|{record.CanonicalSha256}";
        var recoveryId = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(idMaterial))).ToLowerInvariant();
        output.Add(new SQLiteRecoveredRow(
            recoveryId,
            sourceKind,
            sourceFile,
            sourceOffset,
            pageNumber,
            walFrameIndex,
            walTransactionIndex,
            tableName,
            mapping.Candidates,
            mapping.MappingMethod,
            record.RowId,
            record.PayloadLength,
            record.LocalPayloadLength,
            record.OverflowPagesRead,
            record.CanonicalSha256,
            Math.Clamp(confidence, 0, 1),
            status,
            columns,
            new Dictionary<string, string?>
            {
                ["evidence_semantics"] = "schema_aware_sqlite_row_candidate",
                ["mapping_method"] = mapping.MappingMethod,
                ["certainty"] = confidence >= .9 ? "high" : confidence >= .7 ? "medium" : "low",
                ["deleted_claim"] = "not_asserted_without_artifact_correlation"
            }));
    }

    private static List<CellPointer> EnumerateLeafCells(
        ReadOnlyMemory<byte> page,
        uint pageNumber,
        int usablePageSize,
        List<string> warnings)
    {
        var result = new List<CellPointer>();
        var span = page.Span;
        var headerOffset = pageNumber == 1 ? 100 : 0;
        if (headerOffset + 8 > usablePageSize || span[headerOffset] != 0x0d) return result;
        var count = BinaryPrimitives.ReadUInt16BigEndian(span.Slice(headerOffset + 3, 2));
        var pointerBase = headerOffset + 8;
        if (pointerBase + count * 2 > usablePageSize)
        {
            warnings.Add($"Table leaf page {pageNumber} has an invalid cell-pointer array.");
            return result;
        }
        for (var index = 0; index < count; index++)
        {
            var offset = BinaryPrimitives.ReadUInt16BigEndian(span.Slice(pointerBase + index * 2, 2));
            if (offset == 0 || offset >= usablePageSize) continue;
            result.Add(new CellPointer(index, offset));
        }
        return result;
    }

    private static bool IsTableLeaf(ReadOnlySpan<byte> page, uint pageNumber, int usablePageSize)
    {
        var headerOffset = pageNumber == 1 ? 100 : 0;
        return headerOffset < usablePageSize && headerOffset < page.Length && page[headerOffset] == 0x0d;
    }

    private static async Task<WalJournal> ReadWalAsync(
        string walPath,
        int databasePageSize,
        int maximumFrames,
        List<string> warnings,
        CancellationToken cancellationToken)
    {
        await using var wal = OpenReadStream(walPath);
        var header = new byte[32];
        if (await ReadExactAsync(wal, 0, header, cancellationToken).ConfigureAwait(false) != header.Length)
        {
            warnings.Add("WAL header is truncated; row-history reconstruction skipped.");
            return WalJournal.Empty;
        }
        var magic = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(0, 4));
        if (magic is not WalLittle and not WalBig)
        {
            warnings.Add("WAL magic is invalid; row-history reconstruction skipped.");
            return WalJournal.Empty;
        }
        var rawPageSize = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(8, 4));
        var walPageSize = rawPageSize == 1 ? 65_536 : checked((int)rawPageSize);
        if (walPageSize != databasePageSize)
        {
            warnings.Add("WAL/database page sizes differ; row-history reconstruction skipped.");
            return WalJournal.Empty;
        }
        var salt1 = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(16, 4));
        var salt2 = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(20, 4));
        var checksumBigEndian = magic == WalBig;
        var checksum = WalChecksum(header.AsSpan(0, 24), checksumBigEndian, 0, 0);
        if (checksum.S0 != BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(24, 4))
            || checksum.S1 != BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(28, 4)))
        {
            warnings.Add("WAL header checksum is invalid; row-history reconstruction skipped.");
            return WalJournal.Empty;
        }

        var frameHeader = new byte[24];
        var page = new byte[databasePageSize];
        var pending = new List<WalFrameRef>();
        var transactions = new List<WalTransaction>();
        long position = 32;
        var frameIndex = 0;
        var validatedFrames = 0;
        while (position + frameHeader.Length + databasePageSize <= wal.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (frameIndex >= maximumFrames)
            {
                warnings.Add($"WAL frame processing reached the configured cap of {maximumFrames} frames.");
                break;
            }
            if (await ReadExactAsync(wal, position, frameHeader, cancellationToken).ConfigureAwait(false) != frameHeader.Length
                || await ReadExactAsync(wal, position + frameHeader.Length, page, cancellationToken).ConfigureAwait(false) != page.Length)
                break;
            var pageNumber = BinaryPrimitives.ReadUInt32BigEndian(frameHeader.AsSpan(0, 4));
            var databaseSize = BinaryPrimitives.ReadUInt32BigEndian(frameHeader.AsSpan(4, 4));
            if (BinaryPrimitives.ReadUInt32BigEndian(frameHeader.AsSpan(8, 4)) != salt1
                || BinaryPrimitives.ReadUInt32BigEndian(frameHeader.AsSpan(12, 4)) != salt2)
                break;
            checksum = WalChecksum(frameHeader.AsSpan(0, 8), checksumBigEndian, checksum.S0, checksum.S1);
            checksum = WalChecksum(page, checksumBigEndian, checksum.S0, checksum.S1);
            if (checksum.S0 != BinaryPrimitives.ReadUInt32BigEndian(frameHeader.AsSpan(16, 4))
                || checksum.S1 != BinaryPrimitives.ReadUInt32BigEndian(frameHeader.AsSpan(20, 4)))
            {
                warnings.Add($"WAL frame {frameIndex} failed checksum validation; subsequent frames were ignored.");
                break;
            }
            if (pageNumber == 0) break;
            validatedFrames++;
            pending.Add(new WalFrameRef(frameIndex, pageNumber, position + 24));
            if (databaseSize != 0)
            {
                var finalFrames = pending
                    .GroupBy(frame => frame.PageNumber)
                    .Select(group => group.OrderBy(frame => frame.FrameIndex).Last())
                    .OrderBy(frame => frame.FrameIndex)
                    .ToArray();
                transactions.Add(new WalTransaction(transactions.Count, databaseSize, frameIndex, finalFrames));
                pending.Clear();
            }
            frameIndex++;
            position += 24 + databasePageSize;
        }
        return new WalJournal(validatedFrames, transactions);
    }

    private static (uint S0, uint S1) WalChecksum(ReadOnlySpan<byte> input, bool bigEndian, uint s0, uint s1)
    {
        if (input.Length % 8 != 0) throw new InvalidDataException("WAL checksum input must be divisible by 8 bytes.");
        unchecked
        {
            for (var offset = 0; offset < input.Length; offset += 8)
            {
                var first = bigEndian
                    ? BinaryPrimitives.ReadUInt32BigEndian(input.Slice(offset, 4))
                    : BinaryPrimitives.ReadUInt32LittleEndian(input.Slice(offset, 4));
                var second = bigEndian
                    ? BinaryPrimitives.ReadUInt32BigEndian(input.Slice(offset + 4, 4))
                    : BinaryPrimitives.ReadUInt32LittleEndian(input.Slice(offset + 4, 4));
                s0 += first + s1;
                s1 += second + s0;
            }
        }
        return (s0, s1);
    }

    private static string? ResolveWalPath(string databasePath, string? walPath)
    {
        if (!string.IsNullOrWhiteSpace(walPath))
        {
            var explicitPath = Path.GetFullPath(walPath);
            if (!File.Exists(explicitPath)) throw new FileNotFoundException("SQLite WAL file was not found.", explicitPath);
            return explicitPath;
        }
        var sibling = databasePath + "-wal";
        return File.Exists(sibling) ? sibling : null;
    }

    private static SQLiteTextEncoding ReadTextEncoding(ReadOnlySpan<byte> header, List<string> warnings)
    {
        var value = BinaryPrimitives.ReadUInt32BigEndian(header.Slice(56, 4));
        return value switch
        {
            1 => SQLiteTextEncoding.Utf8,
            2 => SQLiteTextEncoding.Utf16LittleEndian,
            3 => SQLiteTextEncoding.Utf16BigEndian,
            0 => SQLiteTextEncoding.Utf8,
            _ => WarnAndDefault()
        };

        SQLiteTextEncoding WarnAndDefault()
        {
            warnings.Add($"SQLite database declares unknown text encoding {value}; UTF-8 was assumed.");
            return SQLiteTextEncoding.Utf8;
        }
    }

    private static int ReadPageSize(ReadOnlySpan<byte> header)
    {
        var raw = BinaryPrimitives.ReadUInt16BigEndian(header.Slice(16, 2));
        var pageSize = raw == 1 ? 65_536 : raw;
        if (pageSize < 512 || pageSize > 65_536 || (pageSize & (pageSize - 1)) != 0)
            throw new InvalidDataException($"Invalid SQLite page size {pageSize}.");
        return pageSize;
    }

    private static string QuoteIdentifier(string value) => '"' + value.Replace("\"", "\"\"", StringComparison.Ordinal) + '"';

    private static FileStream OpenReadStream(string path) => new(path, new FileStreamOptions
    {
        Access = FileAccess.Read,
        Mode = FileMode.Open,
        Share = FileShare.ReadWrite | FileShare.Delete,
        Options = FileOptions.Asynchronous | FileOptions.RandomAccess,
        BufferSize = 64 * 1024
    });

    private static async Task<int> ReadExactAsync(FileStream stream, long offset, Memory<byte> destination, CancellationToken cancellationToken)
    {
        stream.Position = offset;
        var read = 0;
        while (read < destination.Length)
        {
            var current = await stream.ReadAsync(destination[read..], cancellationToken).ConfigureAwait(false);
            if (current == 0) break;
            read += current;
        }
        return read;
    }

    private sealed class RandomPageReader : IDisposable
    {
        private readonly SafeFileHandle _handle;
        private readonly int _pageSize;
        private readonly long _length;
        private readonly bool _walPageDataOffsetMode;
        public string Path { get; }

        public RandomPageReader(string path, int pageSize, bool walPageDataOffsetMode = false)
        {
            Path = System.IO.Path.GetFullPath(path);
            _pageSize = pageSize;
            _walPageDataOffsetMode = walPageDataOffsetMode;
            _length = new FileInfo(Path).Length;
            _handle = File.OpenHandle(Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, FileOptions.RandomAccess);
        }

        public ReadOnlyMemory<byte>? ReadDatabasePage(uint pageNumber)
        {
            if (_walPageDataOffsetMode || pageNumber == 0) return null;
            var offset = checked(((long)pageNumber - 1) * _pageSize);
            return ReadAt(offset, _pageSize);
        }

        public ReadOnlyMemory<byte>? ReadWalFramePage(long dataOffset)
        {
            if (!_walPageDataOffsetMode) return null;
            return ReadAt(dataOffset, _pageSize);
        }

        private ReadOnlyMemory<byte>? ReadAt(long offset, int count)
        {
            if (offset < 0 || offset > _length - count) return null;
            var buffer = new byte[count];
            var total = 0;
            while (total < count)
            {
                var read = RandomAccess.Read(_handle, buffer.AsSpan(total), offset + total);
                if (read == 0) return null;
                total += read;
            }
            return buffer;
        }

        public void Dispose() => _handle.Dispose();
    }

    private sealed record TableSchema(string Name, int RootPage, string Sql, IReadOnlyList<ColumnSchema> Columns);
    private sealed record ColumnSchema(int Cid, string Name, string DeclaredType, int PrimaryKeyOrdinal, int Hidden);
    private sealed record CellPointer(int Index, int Offset);
    private sealed record WalFrameRef(int FrameIndex, uint PageNumber, long DataOffset);
    private sealed record WalTransaction(int Index, uint DatabaseSizePages, int CommitFrameIndex, IReadOnlyList<WalFrameRef> FinalFrames);
    private sealed record WalJournal(int ValidatedFrames, IReadOnlyList<WalTransaction> Transactions)
    {
        public static WalJournal Empty { get; } = new(0, Array.Empty<WalTransaction>());
    }
    private sealed record SchemaMapping(TableSchema? Table, IReadOnlyList<string> Candidates, string MappingMethod, double ConfidenceMultiplier)
    {
        public static SchemaMapping None { get; } = new(null, Array.Empty<string>(), "none", 0);
    }
}
