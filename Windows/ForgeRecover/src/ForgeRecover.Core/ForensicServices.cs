using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ForgeRecover.Core;

public static class ForgeJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };
}

public static class EvidenceHashService
{
    private const int BufferSize = 1024 * 1024;

    public static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        await using var stream = new FileStream(path, new FileStreamOptions
        {
            Access = FileAccess.Read,
            Mode = FileMode.Open,
            Share = FileShare.ReadWrite | FileShare.Delete,
            Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
            BufferSize = BufferSize
        });

        using var sha256 = SHA256.Create();
        var hash = await sha256.ComputeHashAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}

public sealed class CaseVaultService
{
    public async Task<string> CreateAsync(
        string sourcePath,
        string outputParent,
        string caseName,
        string examiner,
        string? notes = null,
        bool copyEvidence = true,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputParent);
        ArgumentException.ThrowIfNullOrWhiteSpace(caseName);
        ArgumentException.ThrowIfNullOrWhiteSpace(examiner);

        var sourceRoot = Path.GetFullPath(sourcePath);
        var outputRoot = Path.GetFullPath(outputParent);

        if (!Directory.Exists(sourceRoot))
        {
            throw new DirectoryNotFoundException($"Evidence source does not exist: {sourceRoot}");
        }

        Directory.CreateDirectory(outputRoot);
        if (IsSubPath(outputRoot, sourceRoot))
        {
            throw new InvalidOperationException("Case output cannot be created inside the evidence source.");
        }

        var caseFolderName = $"{SanitizeFileName(caseName)}_{DateTimeOffset.UtcNow:yyyyMMdd_HHmmss}_{Guid.NewGuid():N}";
        var caseRoot = Path.Combine(outputRoot, caseFolderName);
        var evidenceRoot = Path.Combine(caseRoot, "evidence", "original");
        var workingRoot = Path.Combine(caseRoot, "working");
        var exportsRoot = Path.Combine(caseRoot, "exports");
        var logsRoot = Path.Combine(caseRoot, "logs");

        Directory.CreateDirectory(evidenceRoot);
        Directory.CreateDirectory(workingRoot);
        Directory.CreateDirectory(exportsRoot);
        Directory.CreateDirectory(logsRoot);

        var manifest = new CaseManifest
        {
            CaseName = caseName,
            Examiner = examiner,
            SourcePath = sourceRoot,
            SourceType = BackupOrigin.ImportedFolder,
            AcquisitionMode = copyEvidence ? "forensic-copy" : "read-only-reference",
            Notes = notes
        };

        await AppendLedgerAsync(caseRoot, new ChainOfCustodyEvent(
            DateTimeOffset.UtcNow,
            "CASE_CREATED",
            examiner,
            $"Case {manifest.CaseId} created from {sourceRoot}."), cancellationToken).ConfigureAwait(false);

        foreach (var sourceFile in EnumerateEvidenceFiles(sourceRoot))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var relativePath = Path.GetRelativePath(sourceRoot, sourceFile);
            EnsureSafeRelativePath(relativePath);
            var sourceInfo = new FileInfo(sourceFile);
            var sourceHash = await EvidenceHashService.ComputeSha256Async(sourceFile, cancellationToken).ConfigureAwait(false);

            string evidenceRelativePath;
            if (copyEvidence)
            {
                evidenceRelativePath = Path.Combine("evidence", "original", relativePath);
                var destination = Path.Combine(caseRoot, evidenceRelativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                await CopyFileAsync(sourceFile, destination, cancellationToken).ConfigureAwait(false);
                File.SetLastWriteTimeUtc(destination, sourceInfo.LastWriteTimeUtc);

                var destinationHash = await EvidenceHashService.ComputeSha256Async(destination, cancellationToken).ConfigureAwait(false);
                if (!CryptographicOperations.FixedTimeEquals(
                        Convert.FromHexString(sourceHash),
                        Convert.FromHexString(destinationHash)))
                {
                    throw new IOException($"Hash mismatch after copying evidence file: {relativePath}");
                }
            }
            else
            {
                evidenceRelativePath = relativePath;
            }

            manifest.EvidenceFiles.Add(new EvidenceFileRecord(
                relativePath,
                evidenceRelativePath.Replace('\\', '/'),
                sourceInfo.Length,
                new DateTimeOffset(sourceInfo.LastWriteTimeUtc, TimeSpan.Zero),
                sourceHash));

            await AppendLedgerAsync(caseRoot, new ChainOfCustodyEvent(
                DateTimeOffset.UtcNow,
                copyEvidence ? "EVIDENCE_COPIED" : "EVIDENCE_REFERENCED",
                examiner,
                relativePath,
                sourceHash), cancellationToken).ConfigureAwait(false);
        }

        var caseManifestPath = Path.Combine(caseRoot, "case.json");
        await WriteJsonAtomicAsync(caseManifestPath, manifest, cancellationToken).ConfigureAwait(false);

        await AppendLedgerAsync(caseRoot, new ChainOfCustodyEvent(
            DateTimeOffset.UtcNow,
            "CASE_SEALED",
            examiner,
            $"Recorded {manifest.EvidenceFiles.Count} evidence files."), cancellationToken).ConfigureAwait(false);

        return caseRoot;
    }

    public async Task<VerificationResult> VerifyAsync(string caseRoot, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(caseRoot);
        var fullCaseRoot = Path.GetFullPath(caseRoot);
        var manifestPath = Path.Combine(fullCaseRoot, "case.json");

        if (!File.Exists(manifestPath))
        {
            throw new FileNotFoundException("case.json was not found.", manifestPath);
        }

        var manifest = JsonSerializer.Deserialize<CaseManifest>(
            await File.ReadAllTextAsync(manifestPath, cancellationToken).ConfigureAwait(false),
            ForgeJson.Options) ?? throw new InvalidDataException("case.json could not be decoded.");

        var issues = new List<VerificationIssue>();
        var checkedCount = 0;

        foreach (var record in manifest.EvidenceFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var evidencePath = manifest.AcquisitionMode == "forensic-copy"
                ? Path.Combine(fullCaseRoot, record.EvidenceRelativePath.Replace('/', Path.DirectorySeparatorChar))
                : Path.Combine(manifest.SourcePath, record.RelativePath.Replace('/', Path.DirectorySeparatorChar));

            if (!File.Exists(evidencePath))
            {
                issues.Add(new VerificationIssue(record.RelativePath, record.Sha256, null, "Evidence file is missing."));
                continue;
            }

            var actual = await EvidenceHashService.ComputeSha256Async(evidencePath, cancellationToken).ConfigureAwait(false);
            checkedCount++;
            if (!string.Equals(actual, record.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                issues.Add(new VerificationIssue(record.RelativePath, record.Sha256, actual, "SHA-256 mismatch."));
            }
        }

        return new VerificationResult(issues.Count == 0, checkedCount, issues);
    }

    public Task RecordEventAsync(
        string caseRoot,
        string eventType,
        string actor,
        string details,
        string? sha256 = null,
        CancellationToken cancellationToken = default) =>
        AppendLedgerAsync(caseRoot, new ChainOfCustodyEvent(
            DateTimeOffset.UtcNow,
            eventType,
            actor,
            details,
            sha256), cancellationToken);

    private static IEnumerable<string> EnumerateEvidenceFiles(string sourceRoot)
    {
        return Directory.EnumerateFiles(sourceRoot, "*", new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = false,
            ReturnSpecialDirectories = false,
            AttributesToSkip = FileAttributes.ReparsePoint
        }).OrderBy(path => path, StringComparer.OrdinalIgnoreCase);
    }

    private static async Task CopyFileAsync(string source, string destination, CancellationToken cancellationToken)
    {
        await using var input = new FileStream(source, new FileStreamOptions
        {
            Access = FileAccess.Read,
            Mode = FileMode.Open,
            Share = FileShare.ReadWrite | FileShare.Delete,
            Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
            BufferSize = 1024 * 1024
        });
        await using var output = new FileStream(destination, new FileStreamOptions
        {
            Access = FileAccess.Write,
            Mode = FileMode.CreateNew,
            Share = FileShare.None,
            Options = FileOptions.Asynchronous | FileOptions.SequentialScan | FileOptions.WriteThrough,
            BufferSize = 1024 * 1024
        });

        await input.CopyToAsync(output, 1024 * 1024, cancellationToken).ConfigureAwait(false);
        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
        output.Flush(flushToDisk: true);
    }

    private static async Task AppendLedgerAsync(
        string caseRoot,
        ChainOfCustodyEvent entry,
        CancellationToken cancellationToken)
    {
        var ledgerPath = Path.Combine(caseRoot, "logs", "chain-of-custody.jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(ledgerPath)!);
        var line = JsonSerializer.Serialize(entry, ForgeJson.Options) + Environment.NewLine;
        var bytes = Encoding.UTF8.GetBytes(line);

        await using var stream = new FileStream(ledgerPath, new FileStreamOptions
        {
            Access = FileAccess.Write,
            Mode = FileMode.Append,
            Share = FileShare.Read,
            Options = FileOptions.Asynchronous | FileOptions.WriteThrough
        });
        await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        stream.Flush(flushToDisk: true);
    }

    private static async Task WriteJsonAtomicAsync<T>(string path, T value, CancellationToken cancellationToken)
    {
        var tempPath = path + ".tmp." + Guid.NewGuid().ToString("N");
        var json = JsonSerializer.Serialize(value, ForgeJson.Options);
        await File.WriteAllTextAsync(tempPath, json, new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
        File.Move(tempPath, path, overwrite: true);
    }

    private static bool IsSubPath(string candidate, string parent)
    {
        var normalizedCandidate = Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidate)) + Path.DirectorySeparatorChar;
        var normalizedParent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(parent)) + Path.DirectorySeparatorChar;
        return normalizedCandidate.StartsWith(normalizedParent, StringComparison.OrdinalIgnoreCase);
    }

    private static void EnsureSafeRelativePath(string relativePath)
    {
        if (Path.IsPathRooted(relativePath) || relativePath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Contains(".."))
        {
            throw new InvalidDataException($"Unsafe evidence path: {relativePath}");
        }
    }

    private static string SanitizeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars().ToHashSet();
        var sanitized = new string(value.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();
        return string.IsNullOrWhiteSpace(sanitized) ? "case" : sanitized;
    }
}
