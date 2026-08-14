using System.Globalization;
using System.Text.Json;

namespace ForgeRecover.Core;

public sealed record SQLiteRowCorpusColumnExpectation(
    string Name,
    string? Text = null,
    long? Integer = null,
    double? Real = null,
    string? BlobSha256 = null,
    bool? IsNull = null);

public sealed record SQLiteRowCorpusExpectation(
    string Table,
    long? RowId,
    string? RecoveryStatus,
    double MinimumConfidence,
    IReadOnlyList<SQLiteRowCorpusColumnExpectation> Columns);

public sealed record SQLiteRowCorpusCaseManifest(
    string Id,
    string Database,
    string? Wal,
    IReadOnlyList<SQLiteRowCorpusExpectation> ExpectedRows,
    IReadOnlyList<SQLiteRowCorpusExpectation> ExpectedAbsentRows,
    string? DeviceModel = null,
    string? IosVersion = null,
    string? AcquisitionSha256 = null,
    string? Notes = null);

public sealed record SQLiteRowCorpusCaseResult(
    string Id,
    bool Passed,
    string DatabaseSha256,
    string? WalSha256,
    int ReconstructedRows,
    IReadOnlyList<string> Failures,
    IReadOnlyList<string> Warnings,
    string? DeviceModel,
    string? IosVersion,
    string? AcquisitionSha256);

public sealed record SQLiteRowCorpusReport(
    DateTimeOffset GeneratedUtc,
    int Cases,
    int Passed,
    int Failed,
    IReadOnlyDictionary<string, int> IosVersions,
    IReadOnlyDictionary<string, int> DeviceModels,
    IReadOnlyList<SQLiteRowCorpusCaseResult> Results)
{
    public bool IsValid => Cases > 0 && Failed == 0;
}

public sealed class SQLiteRowRecoveryCorpusRunner
{
    public async Task<SQLiteRowCorpusReport> RunAsync(
        string corpusRoot,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(corpusRoot);
        var root = Path.GetFullPath(corpusRoot);
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException($"Corpus root does not exist: {root}");

        var manifests = Directory
            .EnumerateFiles(root, "case.forge-row-recovery.json", SearchOption.AllDirectories)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (manifests.Length == 0)
            throw new InvalidDataException("No case.forge-row-recovery.json manifests were found.");

        var results = new List<SQLiteRowCorpusCaseResult>(manifests.Length);
        foreach (var manifestPath in manifests)
        {
            cancellationToken.ThrowIfCancellationRequested();
            results.Add(await RunCaseAsync(manifestPath, cancellationToken).ConfigureAwait(false));
        }

        var iosVersions = results
            .Where(result => !string.IsNullOrWhiteSpace(result.IosVersion))
            .GroupBy(result => result.IosVersion!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);
        var deviceModels = results
            .Where(result => !string.IsNullOrWhiteSpace(result.DeviceModel))
            .GroupBy(result => result.DeviceModel!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);
        return new SQLiteRowCorpusReport(
            DateTimeOffset.UtcNow,
            results.Count,
            results.Count(result => result.Passed),
            results.Count(result => !result.Passed),
            iosVersions,
            deviceModels,
            results);
    }

    public async Task SaveReportAsync(
        SQLiteRowCorpusReport report,
        string outputPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        var output = Path.GetFullPath(outputPath);
        Directory.CreateDirectory(Path.GetDirectoryName(output) ?? Environment.CurrentDirectory);
        var temporary = output + ".tmp." + Guid.NewGuid().ToString("N");
        try
        {
            await File.WriteAllTextAsync(
                temporary,
                JsonSerializer.Serialize(report, ForgeJson.Options),
                cancellationToken).ConfigureAwait(false);
            File.Move(temporary, output, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static async Task<SQLiteRowCorpusCaseResult> RunCaseAsync(
        string manifestPath,
        CancellationToken cancellationToken)
    {
        var json = await File.ReadAllTextAsync(manifestPath, cancellationToken).ConfigureAwait(false);
        var manifest = JsonSerializer.Deserialize<SQLiteRowCorpusCaseManifest>(json, ForgeJson.Options)
            ?? throw new InvalidDataException($"Invalid row corpus manifest: {manifestPath}");
        ValidateManifest(manifest, manifestPath);

        var caseRoot = Path.GetDirectoryName(manifestPath)!;
        var database = ResolveUnder(caseRoot, manifest.Database);
        var wal = string.IsNullOrWhiteSpace(manifest.Wal) ? null : ResolveUnder(caseRoot, manifest.Wal);
        var databaseSha = await EvidenceHashService.ComputeSha256Async(database, cancellationToken).ConfigureAwait(false);
        var walSha = wal is null ? null : await EvidenceHashService.ComputeSha256Async(wal, cancellationToken).ConfigureAwait(false);
        var report = await new SQLiteRowRecoveryEngine()
            .RecoverAsync(database, wal, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        var failures = new List<string>();
        foreach (var expectation in manifest.ExpectedRows)
        {
            if (!report.Rows.Any(row => Matches(row, expectation)))
                failures.Add("Missing expected row: " + Describe(expectation));
        }
        foreach (var expectation in manifest.ExpectedAbsentRows)
        {
            if (report.Rows.Any(row => Matches(row, expectation)))
                failures.Add("Unexpected row matched negative expectation: " + Describe(expectation));
        }

        return new SQLiteRowCorpusCaseResult(
            manifest.Id,
            failures.Count == 0,
            databaseSha,
            walSha,
            report.Rows.Count,
            failures,
            report.Warnings,
            manifest.DeviceModel,
            manifest.IosVersion,
            manifest.AcquisitionSha256);
    }

    private static bool Matches(SQLiteRecoveredRow row, SQLiteRowCorpusExpectation expectation)
    {
        if (!string.Equals(row.TableName, expectation.Table, StringComparison.OrdinalIgnoreCase)) return false;
        if (expectation.RowId is not null && row.RowId != expectation.RowId.Value) return false;
        if (!string.IsNullOrWhiteSpace(expectation.RecoveryStatus)
            && !string.Equals(row.RecoveryStatus, expectation.RecoveryStatus, StringComparison.OrdinalIgnoreCase))
            return false;
        if (row.Confidence < expectation.MinimumConfidence) return false;

        var columns = row.Columns
            .Where(column => !string.IsNullOrWhiteSpace(column.Name))
            .GroupBy(column => column.Name!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First().Value, StringComparer.OrdinalIgnoreCase);
        foreach (var expected in expectation.Columns)
        {
            if (!columns.TryGetValue(expected.Name, out var actual)) return false;
            if (expected.IsNull is true && actual.StorageClass != SQLiteDecodedStorageClass.Null) return false;
            if (expected.IsNull is false && actual.StorageClass == SQLiteDecodedStorageClass.Null) return false;
            if (expected.Text is not null && !string.Equals(actual.TextValue, expected.Text, StringComparison.Ordinal)) return false;
            if (expected.Integer is not null && actual.IntegerValue != expected.Integer) return false;
            if (expected.Real is not null
                && (actual.RealValue is null || Math.Abs(actual.RealValue.Value - expected.Real.Value) > 0.0000001d)) return false;
            if (expected.BlobSha256 is not null
                && !string.Equals(actual.BlobSha256, expected.BlobSha256, StringComparison.OrdinalIgnoreCase)) return false;
        }
        return true;
    }

    private static string Describe(SQLiteRowCorpusExpectation expectation) =>
        $"table={expectation.Table}, rowid={expectation.RowId?.ToString(CultureInfo.InvariantCulture) ?? "*"}, status={expectation.RecoveryStatus ?? "*"}";

    private static void ValidateManifest(SQLiteRowCorpusCaseManifest manifest, string path)
    {
        if (string.IsNullOrWhiteSpace(manifest.Id)) throw new InvalidDataException($"Corpus case has no id: {path}");
        if (string.IsNullOrWhiteSpace(manifest.Database)) throw new InvalidDataException($"Corpus case has no database path: {path}");
        if ((manifest.ExpectedRows?.Count ?? 0) == 0 && (manifest.ExpectedAbsentRows?.Count ?? 0) == 0)
            throw new InvalidDataException($"Corpus case has no positive or negative row assertions: {path}");
        foreach (var expectation in (manifest.ExpectedRows ?? Array.Empty<SQLiteRowCorpusExpectation>())
                     .Concat(manifest.ExpectedAbsentRows ?? Array.Empty<SQLiteRowCorpusExpectation>()))
        {
            if (string.IsNullOrWhiteSpace(expectation.Table)) throw new InvalidDataException($"Corpus expectation has no table: {path}");
            if (expectation.MinimumConfidence is < 0 or > 1) throw new InvalidDataException($"Corpus expectation confidence is outside 0..1: {path}");
        }
    }

    private static string ResolveUnder(string root, string relativePath)
    {
        if (Path.IsPathRooted(relativePath)) throw new InvalidDataException("Corpus paths must be relative to their case directory.");
        var rootFull = Path.GetFullPath(root);
        var resolved = Path.GetFullPath(Path.Combine(rootFull, relativePath));
        var rootPrefix = rootFull.EndsWith(Path.DirectorySeparatorChar)
            ? rootFull
            : rootFull + Path.DirectorySeparatorChar;
        if (!resolved.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Corpus path escapes its case directory: {relativePath}");
        if (!File.Exists(resolved)) throw new FileNotFoundException("Corpus evidence file was not found.", resolved);
        return resolved;
    }
}
