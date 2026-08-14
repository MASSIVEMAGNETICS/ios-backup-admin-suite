using System.Text.Json;
using System.Text.Json.Serialization;

namespace ForgeRecover.Core;

public sealed record SQLiteRecoveryCorpusCase(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("database")] string Database,
    [property: JsonPropertyName("wal")] string? Wal,
    [property: JsonPropertyName("expected_contains")] IReadOnlyList<string> ExpectedContains,
    [property: JsonPropertyName("expected_absent")] IReadOnlyList<string> ExpectedAbsent,
    [property: JsonPropertyName("minimum_text_characters")] int MinimumTextCharacters = 8,
    [property: JsonPropertyName("include_uncommitted_wal")] bool IncludeUncommittedWal = false);

public sealed record SQLiteRecoveryCorpusCaseResult(
    string Id,
    string ManifestPath,
    string DatabasePath,
    string? WalPath,
    bool Passed,
    IReadOnlyList<string> MissingExpected,
    IReadOnlyList<string> UnexpectedPresent,
    int FragmentCount,
    IReadOnlyList<string> Warnings);

public sealed record SQLiteRecoveryCorpusReport(
    string CorpusRoot,
    DateTimeOffset StartedUtc,
    DateTimeOffset CompletedUtc,
    int Cases,
    int Passed,
    int Failed,
    IReadOnlyList<SQLiteRecoveryCorpusCaseResult> Results)
{
    public bool IsValid => Failed == 0 && Cases > 0;
}

public sealed class SQLiteRecoveryCorpusRunner
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public async Task<SQLiteRecoveryCorpusReport> RunAsync(
        string corpusRoot,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(corpusRoot);
        var root = Path.GetFullPath(corpusRoot);
        if (!Directory.Exists(root))
        {
            throw new DirectoryNotFoundException($"Recovery corpus directory does not exist: {root}");
        }

        var manifests = Directory
            .EnumerateFiles(root, "*.forge-recovery.json", SearchOption.AllDirectories)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (manifests.Length == 0)
        {
            throw new InvalidDataException(
                "Recovery corpus contains no *.forge-recovery.json case manifests. " +
                "A validation claim requires explicit expected-positive and expected-negative markers.");
        }

        var started = DateTimeOffset.UtcNow;
        var results = new List<SQLiteRecoveryCorpusCaseResult>();
        var engine = new SQLiteRecoveryEngine();

        foreach (var manifestPath in manifests)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SQLiteRecoveryCorpusCase corpusCase;
            await using (var stream = File.OpenRead(manifestPath))
            {
                corpusCase = await JsonSerializer.DeserializeAsync<SQLiteRecoveryCorpusCase>(
                    stream,
                    JsonOptions,
                    cancellationToken).ConfigureAwait(false)
                    ?? throw new InvalidDataException($"Corpus manifest is empty: {manifestPath}");
            }

            ValidateCase(corpusCase, manifestPath);
            var caseRoot = Path.GetDirectoryName(manifestPath)
                ?? throw new InvalidDataException($"Cannot determine corpus case directory: {manifestPath}");
            var databasePath = ResolveInsideCase(caseRoot, corpusCase.Database);
            var walPath = string.IsNullOrWhiteSpace(corpusCase.Wal)
                ? null
                : ResolveInsideCase(caseRoot, corpusCase.Wal!);

            var report = await engine.RecoverAsync(
                databasePath,
                walPath,
                new SQLiteRecoveryOptions
                {
                    MinimumTextCharacters = corpusCase.MinimumTextCharacters,
                    IncludeUncommittedWal = corpusCase.IncludeUncommittedWal,
                    MaximumFragments = 100_000
                },
                cancellationToken).ConfigureAwait(false);

            var recoveredText = report.Fragments.Select(fragment => fragment.Text).ToArray();
            var missing = corpusCase.ExpectedContains
                .Where(marker => !recoveredText.Any(text => text.Contains(marker, StringComparison.Ordinal)))
                .ToArray();
            var unexpected = corpusCase.ExpectedAbsent
                .Where(marker => recoveredText.Any(text => text.Contains(marker, StringComparison.Ordinal)))
                .ToArray();

            results.Add(new SQLiteRecoveryCorpusCaseResult(
                corpusCase.Id,
                Path.GetFullPath(manifestPath),
                databasePath,
                walPath,
                missing.Length == 0 && unexpected.Length == 0,
                missing,
                unexpected,
                report.Fragments.Count,
                report.Warnings));
        }

        var completed = DateTimeOffset.UtcNow;
        var passed = results.Count(result => result.Passed);
        return new SQLiteRecoveryCorpusReport(
            root,
            started,
            completed,
            results.Count,
            passed,
            results.Count - passed,
            results);
    }

    public async Task SaveReportAsync(
        SQLiteRecoveryCorpusReport report,
        string outputPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        var path = Path.GetFullPath(outputPath);
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? Environment.CurrentDirectory);
        await using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        await JsonSerializer.SerializeAsync(stream, report, JsonOptions, cancellationToken).ConfigureAwait(false);
    }

    private static void ValidateCase(SQLiteRecoveryCorpusCase corpusCase, string manifestPath)
    {
        if (string.IsNullOrWhiteSpace(corpusCase.Id))
        {
            throw new InvalidDataException($"Corpus case has no id: {manifestPath}");
        }
        if (string.IsNullOrWhiteSpace(corpusCase.Database))
        {
            throw new InvalidDataException($"Corpus case has no database path: {manifestPath}");
        }
        if (corpusCase.ExpectedContains.Count == 0 && corpusCase.ExpectedAbsent.Count == 0)
        {
            throw new InvalidDataException(
                $"Corpus case {corpusCase.Id} defines no validation expectations. " +
                "At least one expected_contains or expected_absent marker is required.");
        }
    }

    private static string ResolveInsideCase(string caseRoot, string relativePath)
    {
        if (Path.IsPathRooted(relativePath))
        {
            throw new InvalidDataException("Corpus file paths must be relative to the case directory.");
        }

        var root = Path.GetFullPath(caseRoot) + Path.DirectorySeparatorChar;
        var resolved = Path.GetFullPath(Path.Combine(caseRoot, relativePath));
        if (!resolved.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"Corpus path escapes its case directory: {relativePath}");
        }
        if (!File.Exists(resolved))
        {
            throw new FileNotFoundException("Corpus evidence file was not found.", resolved);
        }
        return resolved;
    }
}
