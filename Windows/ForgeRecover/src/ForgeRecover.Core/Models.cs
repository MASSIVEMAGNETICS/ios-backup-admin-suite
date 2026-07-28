using System.Text.Json.Serialization;

namespace ForgeRecover.Core;

public static class ForgeRecoverConstants
{
    public const string ToolName = "ForgeRecover";
    public const string ToolVersion = "0.1.0";
    public const string CaseSchemaVersion = "1.0";
}

public enum BackupOrigin
{
    Unknown,
    AppleDevices,
    ITunesDesktop,
    Libimobiledevice,
    ImportedFolder
}

public sealed record BackupDescriptor(
    string RootPath,
    string BackupId,
    BackupOrigin Origin,
    DateTimeOffset LastModifiedUtc,
    long TotalBytes,
    bool HasManifestDatabase,
    string? DeviceName = null,
    string? ProductVersion = null,
    bool? IsEncrypted = null);

public sealed record EvidenceFileRecord(
    string RelativePath,
    string EvidenceRelativePath,
    long Length,
    DateTimeOffset LastWriteTimeUtc,
    string Sha256);

public sealed record ChainOfCustodyEvent(
    DateTimeOffset TimestampUtc,
    string EventType,
    string Actor,
    string Details,
    string? Sha256 = null);

public sealed class CaseManifest
{
    public string SchemaVersion { get; init; } = ForgeRecoverConstants.CaseSchemaVersion;
    public string CaseId { get; init; } = Guid.NewGuid().ToString("N");
    public string CaseName { get; init; } = string.Empty;
    public string Examiner { get; init; } = string.Empty;
    public DateTimeOffset CreatedUtc { get; init; } = DateTimeOffset.UtcNow;
    public string SourcePath { get; init; } = string.Empty;
    public BackupOrigin SourceType { get; init; } = BackupOrigin.ImportedFolder;
    public string AcquisitionMode { get; init; } = "forensic-copy";
    public string? Notes { get; init; }
    public List<EvidenceFileRecord> EvidenceFiles { get; init; } = new();
    public Dictionary<string, string> ToolVersions { get; init; } = new(StringComparer.OrdinalIgnoreCase)
    {
        [ForgeRecoverConstants.ToolName] = ForgeRecoverConstants.ToolVersion
    };
}

public sealed record ResolvedBackupFile(
    string FileId,
    string Domain,
    string RelativePath,
    string PhysicalPath,
    long? Flags);

public enum ArtifactKind
{
    Message,
    Call,
    Contact,
    Photo,
    Video,
    Note,
    Calendar,
    Location,
    Application,
    File,
    Unknown
}

public sealed record ArtifactRecord(
    string ArtifactId,
    ArtifactKind Kind,
    DateTimeOffset? TimestampUtc,
    string? Primary,
    string? Secondary,
    string? Body,
    string? Direction,
    string SourceDatabase,
    long? SourceRowId,
    IReadOnlyDictionary<string, string?> Metadata);

public sealed record ExtractionResult(
    string ExtractorName,
    string SourceDatabase,
    int Count,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<ArtifactRecord> Artifacts);

public sealed record VerificationIssue(
    string RelativePath,
    string ExpectedSha256,
    string? ActualSha256,
    string Message);

public sealed record VerificationResult(
    bool IsValid,
    int FilesChecked,
    IReadOnlyList<VerificationIssue> Issues);

public sealed record ToolExecutionResult(
    string Executable,
    IReadOnlyList<string> Arguments,
    int ExitCode,
    string StandardOutput,
    string StandardError)
{
    [JsonIgnore]
    public bool Succeeded => ExitCode == 0;
}
