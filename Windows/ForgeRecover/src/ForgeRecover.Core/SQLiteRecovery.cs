using System.Buffers;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace ForgeRecover.Core;

public enum SQLiteRecoverySourceKind
{
    FreelistLeafPage,
    FreelistTrunkUnused,
    BTreeFreeblock,
    BTreeUnallocatedRegion,
    WalCommittedHistoricalPage,
    WalCurrentCommittedPage,
    WalUncommittedHistoricalPage
}

public sealed record SQLiteRecoveryFragment(
    string FragmentId,
    SQLiteRecoverySourceKind SourceKind,
    string SourceFile,
    long SourceOffset,
    uint? PageNumber,
    int? WalFrameIndex,
    bool? WalFrameCommitted,
    string Text,
    string TextEncoding,
    int ByteLength,
    double Confidence,
    string Sha256,
    IReadOnlyDictionary<string, string?> Metadata);

public sealed record SQLiteRecoveryReport(
    string DatabasePath,
    string? WalPath,
    int PageSize,
    long DatabasePages,
    long FreelistPagesDeclared,
    int WalFramesValidated,
    int WalTransactionsCommitted,
    bool Truncated,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<SQLiteRecoveryFragment> Fragments);

public sealed class SQLiteRecoveryOptions
{
    public int MinimumTextCharacters { get; init; } = 8;
    public int MaximumFragments { get; init; } = 20_000;
    public bool IncludeUtf16LittleEndian { get; init; } = true;
    public bool IncludeUncommittedWal { get; init; }
    public bool IncludeCurrentWalPages { get; init; }
    public bool IncludeWalContentStillPresentInCurrentPage { get; init; }
    public int MaximumFreelistPagesToTraverse { get; init; } = 1_000_000;

    internal void Validate()
    {
        if (MinimumTextCharacters is < 4 or > 1024) throw new ArgumentOutOfRangeException(nameof(MinimumTextCharacters));
        if (MaximumFragments < 1) throw new ArgumentOutOfRangeException(nameof(MaximumFragments));
        if (MaximumFreelistPagesToTraverse < 1) throw new ArgumentOutOfRangeException(nameof(MaximumFreelistPagesToTraverse));
    }
}

public sealed class SQLiteRecoveryEngine
{
    private static readonly byte[] Magic = "SQLite format 3\0"u8.ToArray();
    private const uint WalLittle = 0x377F0682;
    private const uint WalBig = 0x377F0683;

    public async Task<SQLiteRecoveryReport> RecoverAsync(
        string databasePath,
        string? walPath = null,
        SQLiteRecoveryOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        options ??= new SQLiteRecoveryOptions();
        options.Validate();
        var dbPath = Path.GetFullPath(databasePath);
        if (!File.Exists(dbPath)) throw new FileNotFoundException("SQLite database was not found.", dbPath);

        var warnings = new List<string>();
        var fragments = new List<SQLiteRecoveryFragment>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        await using var db = OpenRead(dbPath);
        var header = new byte[100];
        if (await ReadExactAsync(db, 0, header, cancellationToken).ConfigureAwait(false) != 100
            || !header.AsSpan(0, 16).SequenceEqual(Magic))
            throw new InvalidDataException("Input does not contain a valid SQLite 3 header.");

        var pageSize = PageSize(header);
        var reserved = header[20];
        if (reserved >= pageSize || pageSize - reserved < 480) throw new InvalidDataException("Invalid SQLite usable page size.");
        var usable = pageSize - reserved;
        var pages = db.Length / pageSize;
        if (pages < 1) throw new InvalidDataException("SQLite database is shorter than one complete page.");
        if (db.Length % pageSize != 0) warnings.Add("Database length is not an exact multiple of page size.");
        var firstTrunk = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(32, 4));
        var freelistDeclared = (long)BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(36, 4));
        var ctx = new Ctx(dbPath, pageSize, usable, pages, options, warnings, fragments, seen);

        if (firstTrunk != 0) await ScanFreelistAsync(db, firstTrunk, ctx, cancellationToken).ConfigureAwait(false);
        if (!ctx.Full) await ScanBtreesAsync(db, ctx, cancellationToken).ConfigureAwait(false);

        var resolvedWal = string.IsNullOrWhiteSpace(walPath) ? dbPath + "-wal" : Path.GetFullPath(walPath);
        var walStats = default(WalStats);
        if (File.Exists(resolvedWal) && !ctx.Full)
            walStats = await ScanWalAsync(db, resolvedWal, ctx, cancellationToken).ConfigureAwait(false);
        else if (string.IsNullOrWhiteSpace(walPath))
            resolvedWal = null;

        if (ctx.Full) warnings.Add($"Recovery output reached the configured cap of {options.MaximumFragments} fragments.");
        return new SQLiteRecoveryReport(dbPath, resolvedWal, pageSize, pages, freelistDeclared,
            walStats.Frames, walStats.Transactions, ctx.Full, warnings, fragments);
    }

    private static async Task ScanFreelistAsync(FileStream db, uint trunk, Ctx ctx, CancellationToken ct)
    {
        var visited = new HashSet<uint>();
        var page = new byte[ctx.PageSize];
        var count = 0;
        while (trunk != 0 && !ctx.Full)
        {
            if (!ValidPage(trunk, ctx.Pages) || !visited.Add(trunk)) { ctx.Warnings.Add($"Invalid/cyclic freelist trunk {trunk}."); break; }
            if (++count > ctx.Options.MaximumFreelistPagesToTraverse) { ctx.Warnings.Add("Freelist safety limit reached."); break; }
            if (!await ReadPageAsync(db, trunk, page, ctx.PageSize, ct).ConfigureAwait(false)) break;
            var next = BinaryPrimitives.ReadUInt32BigEndian(page.AsSpan(0, 4));
            var leaves = BinaryPrimitives.ReadUInt32BigEndian(page.AsSpan(4, 4));
            var max = (uint)Math.Max(0, (ctx.Usable / 4) - 2);
            if (leaves > max) { ctx.Warnings.Add($"Freelist trunk {trunk} has invalid leaf count {leaves}."); break; }

            var unused = 8 + checked((int)leaves * 4);
            if (unused < ctx.Usable)
                Add(page.AsSpan(unused, ctx.Usable - unused), SQLiteRecoverySourceKind.FreelistTrunkUnused,
                    ctx.DbPath, Offset(trunk, ctx.PageSize) + unused, trunk, null, null, .68,
                    "freelist_trunk_unused_fragment", ctx);

            for (var i = 0; i < leaves && !ctx.Full; i++)
            {
                var leaf = BinaryPrimitives.ReadUInt32BigEndian(page.AsSpan(8 + checked((int)i * 4), 4));
                if (!ValidPage(leaf, ctx.Pages)) { ctx.Warnings.Add($"Invalid freelist leaf {leaf}."); continue; }
                var leafPage = new byte[ctx.PageSize];
                if (await ReadPageAsync(db, leaf, leafPage, ctx.PageSize, ct).ConfigureAwait(false))
                    Add(leafPage.AsSpan(0, ctx.Usable), SQLiteRecoverySourceKind.FreelistLeafPage,
                        ctx.DbPath, Offset(leaf, ctx.PageSize), leaf, null, null, .82,
                        "freelist_page_fragment", ctx);
            }
            trunk = next;
        }
    }

    private static async Task ScanBtreesAsync(FileStream db, Ctx ctx, CancellationToken ct)
    {
        var page = new byte[ctx.PageSize];
        for (uint p = 1; p <= ctx.Pages && !ctx.Full; p++)
        {
            if (!await ReadPageAsync(db, p, page, ctx.PageSize, ct).ConfigureAwait(false)) continue;
            var h = p == 1 ? 100 : 0;
            if (h >= ctx.Usable) continue;
            var type = page[h];
            var interior = type is 0x02 or 0x05;
            if (!interior && type is not (0x0A or 0x0D)) continue;
            var hs = interior ? 12 : 8;
            if (h + hs > ctx.Usable) continue;
            var free = BinaryPrimitives.ReadUInt16BigEndian(page.AsSpan(h + 1, 2));
            var cells = BinaryPrimitives.ReadUInt16BigEndian(page.AsSpan(h + 3, 2));
            var rawContent = BinaryPrimitives.ReadUInt16BigEndian(page.AsSpan(h + 5, 2));
            var content = rawContent == 0 ? 65_536 : rawContent;
            var pointersEnd = h + hs + cells * 2;
            if (pointersEnd <= content && content <= ctx.Usable && content > pointersEnd)
                Add(page.AsSpan(pointersEnd, content - pointersEnd), SQLiteRecoverySourceKind.BTreeUnallocatedRegion,
                    ctx.DbPath, Offset(p, ctx.PageSize) + pointersEnd, p, null, null, .56,
                    "btree_unallocated_fragment", ctx);

            var visited = new HashSet<ushort>();
            while (free != 0 && !ctx.Full)
            {
                if (!visited.Add(free) || free + 4 > ctx.Usable) { ctx.Warnings.Add($"Invalid/cyclic freeblock on page {p}."); break; }
                var next = BinaryPrimitives.ReadUInt16BigEndian(page.AsSpan(free, 2));
                var size = BinaryPrimitives.ReadUInt16BigEndian(page.AsSpan(free + 2, 2));
                if (size < 4 || free + size > ctx.Usable) { ctx.Warnings.Add($"Invalid freeblock size on page {p}."); break; }
                Add(page.AsSpan(free + 4, size - 4), SQLiteRecoverySourceKind.BTreeFreeblock,
                    ctx.DbPath, Offset(p, ctx.PageSize) + free + 4, p, null, null, .74,
                    "btree_freeblock_fragment", ctx);
                free = next;
            }
        }
    }

    private static async Task<WalStats> ScanWalAsync(FileStream db, string walPath, Ctx ctx, CancellationToken ct)
    {
        await using var wal = OpenRead(walPath);
        var header = new byte[32];
        if (await ReadExactAsync(wal, 0, header, ct).ConfigureAwait(false) != 32) { ctx.Warnings.Add("WAL header is truncated."); return default; }
        var magic = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(0, 4));
        if (magic is not WalLittle and not WalBig) { ctx.Warnings.Add("WAL magic is invalid."); return default; }
        var psRaw = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(8, 4));
        var ps = psRaw == 1 ? 65_536 : checked((int)psRaw);
        if (ps != ctx.PageSize) { ctx.Warnings.Add("WAL/database page sizes differ."); return default; }
        var salt1 = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(16, 4));
        var salt2 = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(20, 4));
        var big = magic == WalBig;
        var sum = Checksum(header.AsSpan(0, 24), big, 0, 0);
        if (sum.S0 != BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(24, 4))
            || sum.S1 != BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(28, 4)))
        { ctx.Warnings.Add("WAL header checksum validation failed."); return default; }

        var fh = new byte[24];
        var page = new byte[ctx.PageSize];
        var pending = new List<Frame>();
        var committed = new List<Frame>();
        var frameIndex = 0;
        var frames = 0;
        var txns = 0;
        long pos = 32;
        while (pos + 24 + ctx.PageSize <= wal.Length)
        {
            if (await ReadExactAsync(wal, pos, fh, ct).ConfigureAwait(false) != 24
                || await ReadExactAsync(wal, pos + 24, page, ct).ConfigureAwait(false) != ctx.PageSize) break;
            var pg = BinaryPrimitives.ReadUInt32BigEndian(fh.AsSpan(0, 4));
            var commitSize = BinaryPrimitives.ReadUInt32BigEndian(fh.AsSpan(4, 4));
            if (BinaryPrimitives.ReadUInt32BigEndian(fh.AsSpan(8, 4)) != salt1
                || BinaryPrimitives.ReadUInt32BigEndian(fh.AsSpan(12, 4)) != salt2) break;
            sum = Checksum(fh.AsSpan(0, 8), big, sum.S0, sum.S1);
            sum = Checksum(page, big, sum.S0, sum.S1);
            if (sum.S0 != BinaryPrimitives.ReadUInt32BigEndian(fh.AsSpan(16, 4))
                || sum.S1 != BinaryPrimitives.ReadUInt32BigEndian(fh.AsSpan(20, 4)))
            { ctx.Warnings.Add($"WAL frame {frameIndex} checksum validation failed; remaining frames ignored."); break; }
            if (pg == 0) break;
            frames++;
            pending.Add(new Frame(frameIndex, pg, pos + 24, page.ToArray()));
            if (commitSize != 0)
            {
                txns++;
                committed.AddRange(pending.GroupBy(x => x.Page).Select(g => g.OrderBy(x => x.Index).Last()));
                pending.Clear();
            }
            frameIndex++;
            pos += 24 + ctx.PageSize;
        }

        var latest = committed.GroupBy(x => x.Page).ToDictionary(g => g.Key, g => g.Max(x => x.Index));
        foreach (var frame in committed)
        {
            var current = latest[frame.Page] == frame.Index;
            if (current && !ctx.Options.IncludeCurrentWalPages) continue;
            await ScanFrameAsync(db, walPath, frame, true, current, ctx, ct).ConfigureAwait(false);
            if (ctx.Full) break;
        }
        if (ctx.Options.IncludeUncommittedWal && !ctx.Full)
            foreach (var frame in pending)
            {
                await ScanFrameAsync(db, walPath, frame, false, false, ctx, ct).ConfigureAwait(false);
                if (ctx.Full) break;
            }
        return new WalStats(frames, txns);
    }

    private static async Task ScanFrameAsync(FileStream db, string walPath, Frame frame, bool committed, bool current, Ctx ctx, CancellationToken ct)
    {
        byte[]? dbPage = null;
        if (ValidPage(frame.Page, ctx.Pages))
        {
            dbPage = new byte[ctx.PageSize];
            if (!await ReadPageAsync(db, frame.Page, dbPage, ctx.PageSize, ct).ConfigureAwait(false)) dbPage = null;
        }
        var kind = !committed ? SQLiteRecoverySourceKind.WalUncommittedHistoricalPage
            : current ? SQLiteRecoverySourceKind.WalCurrentCommittedPage
            : SQLiteRecoverySourceKind.WalCommittedHistoricalPage;
        var confidence = !committed ? .38 : current ? .95 : .70;
        Add(frame.Bytes, kind, walPath, frame.Offset, frame.Page, frame.Index, committed, confidence,
            !committed ? "wal_uncommitted_historical_fragment" : current ? "wal_current_committed_fragment" : "wal_committed_historical_fragment",
            ctx, ctx.Options.IncludeWalContentStillPresentInCurrentPage ? null : dbPage);
    }

    private static void Add(ReadOnlySpan<byte> bytes, SQLiteRecoverySourceKind kind, string file, long baseOffset,
        uint? page, int? frame, bool? committed, double confidence, string status, Ctx ctx, byte[]? exclude = null)
    {
        foreach (var c in Utf8(bytes, ctx.Options.MinimumTextCharacters))
        {
            if (ctx.Full) return;
            if (exclude is not null && exclude.AsSpan().IndexOf(c.Bytes) >= 0) continue;
            AddOne(c, "utf-8", kind, file, baseOffset, page, frame, committed, confidence, status, ctx);
        }
        if (!ctx.Options.IncludeUtf16LittleEndian) return;
        foreach (var c in Utf16(bytes, ctx.Options.MinimumTextCharacters))
        {
            if (ctx.Full) return;
            if (exclude is not null && exclude.AsSpan().IndexOf(c.Bytes) >= 0) continue;
            AddOne(c, "utf-16le", kind, file, baseOffset, page, frame, committed, confidence - .03, status, ctx);
        }
    }

    private static void AddOne(Candidate c, string encoding, SQLiteRecoverySourceKind kind, string file, long baseOffset,
        uint? page, int? frame, bool? committed, double confidence, string status, Ctx ctx)
    {
        var text = c.Text.Trim();
        if (!Useful(text, ctx.Options.MinimumTextCharacters)) return;
        var sha = Convert.ToHexString(SHA256.HashData(c.Bytes)).ToLowerInvariant();
        if (!ctx.Seen.Add($"{kind}|{page}|{frame}|{sha}")) return;
        var offset = checked(baseOffset + c.Offset);
        var id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{Path.GetFileName(file)}|{kind}|{offset}|{sha}"))).ToLowerInvariant();
        ctx.Fragments.Add(new SQLiteRecoveryFragment(id, kind, file, offset, page, frame, committed, text, encoding,
            c.Bytes.Length, Math.Clamp(confidence, 0, 1), sha, new Dictionary<string, string?>
            {
                ["recovery_status"] = status,
                ["classification"] = kind.ToString(),
                ["certainty"] = confidence >= .8 ? "high" : confidence >= .6 ? "medium" : "low"
            }));
    }

    private static IReadOnlyList<Candidate> Utf8(ReadOnlySpan<byte> data, int min)
    {
        var result = new List<Candidate>();
        for (var i = 0; i < data.Length;)
        {
            var start = i; var cur = i; var chars = 0; var sb = new StringBuilder();
            while (cur < data.Length)
            {
                var st = Rune.DecodeFromUtf8(data[cur..], out var rune, out var used);
                if (st != OperationStatus.Done || used <= 0 || !Printable(rune)) break;
                sb.Append(rune); chars++; cur += used;
            }
            if (chars >= min && cur > start) result.Add(new Candidate(start, data[start..cur].ToArray(), sb.ToString()));
            i = cur > start ? Math.Min(data.Length, cur + 1) : start + 1;
        }
        return result;
    }

    private static IReadOnlyList<Candidate> Utf16(ReadOnlySpan<byte> data, int min)
    {
        var result = new List<Candidate>();
        for (var align = 0; align < 2; align++)
            for (var i = align; i + 1 < data.Length;)
            {
                var start = i; var cur = i; var chars = 0; var sb = new StringBuilder();
                while (cur + 1 < data.Length)
                {
                    var ch = (char)BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(cur, 2));
                    if (ch == '\0' || char.IsSurrogate(ch) || !(char.IsLetterOrDigit(ch) || char.IsPunctuation(ch) || char.IsSymbol(ch) || char.IsWhiteSpace(ch))) break;
                    sb.Append(ch); chars++; cur += 2;
                }
                if (chars >= min && cur > start) result.Add(new Candidate(start, data[start..cur].ToArray(), sb.ToString()));
                i = cur > start ? Math.Min(data.Length, cur + 2) : start + 2;
            }
        return result;
    }

    private static bool Useful(string text, int min)
    {
        if (text.Length < min || text.Contains("SQLite format 3", StringComparison.Ordinal)
            || text.StartsWith("CREATE TABLE", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("CREATE INDEX", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("sqlite_", StringComparison.OrdinalIgnoreCase)) return false;
        return text.All(ch => !char.IsControl(ch) || ch is '\r' or '\n' or '\t');
    }

    private static bool Printable(Rune r) => r.Value != 0 && (Rune.IsLetterOrDigit(r) || Rune.IsPunctuation(r) || Rune.IsSymbol(r) || Rune.IsWhiteSpace(r));
    private static int PageSize(ReadOnlySpan<byte> h)
    {
        var raw = BinaryPrimitives.ReadUInt16BigEndian(h.Slice(16, 2));
        var size = raw == 1 ? 65_536 : raw;
        if (size < 512 || size > 65_536 || (size & (size - 1)) != 0) throw new InvalidDataException($"Invalid SQLite page size {size}.");
        return size;
    }
    private static FileStream OpenRead(string path) => new(path, new FileStreamOptions { Access = FileAccess.Read, Mode = FileMode.Open,
        Share = FileShare.ReadWrite | FileShare.Delete, Options = FileOptions.Asynchronous | FileOptions.RandomAccess, BufferSize = 64 * 1024 });
    private static long Offset(uint page, int size) => checked(((long)page - 1) * size);
    private static bool ValidPage(uint page, long pages) => page >= 1 && page <= pages;
    private static async Task<bool> ReadPageAsync(FileStream s, uint p, byte[] b, int size, CancellationToken ct) =>
        await ReadExactAsync(s, Offset(p, size), b, ct).ConfigureAwait(false) == size;
    private static async Task<int> ReadExactAsync(FileStream s, long offset, Memory<byte> b, CancellationToken ct)
    {
        s.Position = offset; var n = 0;
        while (n < b.Length) { var r = await s.ReadAsync(b[n..], ct).ConfigureAwait(false); if (r == 0) break; n += r; }
        return n;
    }
    private static (uint S0, uint S1) Checksum(ReadOnlySpan<byte> input, bool big, uint s0, uint s1)
    {
        if (input.Length % 8 != 0) throw new InvalidDataException("WAL checksum input must be a multiple of 8 bytes.");
        unchecked
        {
            for (var i = 0; i < input.Length; i += 8)
            {
                var x0 = big ? BinaryPrimitives.ReadUInt32BigEndian(input.Slice(i, 4)) : BinaryPrimitives.ReadUInt32LittleEndian(input.Slice(i, 4));
                var x1 = big ? BinaryPrimitives.ReadUInt32BigEndian(input.Slice(i + 4, 4)) : BinaryPrimitives.ReadUInt32LittleEndian(input.Slice(i + 4, 4));
                s0 += x0 + s1; s1 += x1 + s0;
            }
        }
        return (s0, s1);
    }

    private sealed record Candidate(int Offset, byte[] Bytes, string Text);
    private sealed record Frame(int Index, uint Page, long Offset, byte[] Bytes);
    private readonly record struct WalStats(int Frames, int Transactions);
    private sealed class Ctx(string dbPath, int pageSize, int usable, long pages, SQLiteRecoveryOptions options,
        List<string> warnings, List<SQLiteRecoveryFragment> fragments, HashSet<string> seen)
    {
        public string DbPath { get; } = dbPath;
        public int PageSize { get; } = pageSize;
        public int Usable { get; } = usable;
        public long Pages { get; } = pages;
        public SQLiteRecoveryOptions Options { get; } = options;
        public List<string> Warnings { get; } = warnings;
        public List<SQLiteRecoveryFragment> Fragments { get; } = fragments;
        public HashSet<string> Seen { get; } = seen;
        public bool Full => Fragments.Count >= Options.MaximumFragments;
    }
}
