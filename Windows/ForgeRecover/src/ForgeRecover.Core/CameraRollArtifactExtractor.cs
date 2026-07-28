using System.Globalization;

namespace ForgeRecover.Core;

public sealed class CameraRollArtifactExtractor : IArtifactExtractor
{
    private static readonly HashSet<string> PhotoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".heic", ".heif", ".png", ".gif", ".tif", ".tiff", ".dng", ".raw"
    };

    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mov", ".mp4", ".m4v", ".3gp"
    };

    public string Name => "media";
    public ArtifactKind Kind => ArtifactKind.Photo;

    public async Task<ExtractionResult> ExtractAsync(
        ExtractionContext context,
        CancellationToken cancellationToken = default)
    {
        using var resolver = new ManifestDbResolver(context.BackupRoot);
        var files = resolver.Enumerate("CameraRollDomain", "Media/DCIM/")
            .Concat(resolver.Enumerate("MediaDomain", "Media/DCIM/"))
            .GroupBy(item => item.FileId, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();

        var artifacts = new List<ArtifactRecord>();
        var warnings = new List<string>();
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var extension = Path.GetExtension(file.RelativePath);
            var kind = PhotoExtensions.Contains(extension)
                ? ArtifactKind.Photo
                : VideoExtensions.Contains(extension)
                    ? ArtifactKind.Video
                    : ArtifactKind.Unknown;
            if (kind == ArtifactKind.Unknown)
            {
                continue;
            }

            try
            {
                var info = new FileInfo(file.PhysicalPath);
                var sha256 = await EvidenceHashService.ComputeSha256Async(file.PhysicalPath, cancellationToken)
                    .ConfigureAwait(false);
                artifacts.Add(new ArtifactRecord(
                    SqliteArtifactHelpers.StableArtifactId(Name, file.RelativePath, null, file.FileId),
                    kind,
                    new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero),
                    Path.GetFileName(file.RelativePath),
                    file.Domain,
                    file.RelativePath,
                    null,
                    file.RelativePath,
                    null,
                    new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["file_id"] = file.FileId,
                        ["bytes"] = info.Length.ToString(CultureInfo.InvariantCulture),
                        ["sha256"] = sha256,
                        ["timestamp_basis"] = "backup_payload_last_write_time",
                        ["recovery_status"] = "present_in_backup_payload"
                    }));
            }
            catch (IOException exception)
            {
                warnings.Add($"Could not read {file.RelativePath}: {exception.Message}");
            }
            catch (UnauthorizedAccessException exception)
            {
                warnings.Add($"Access denied for {file.RelativePath}: {exception.Message}");
            }
        }

        return new ExtractionResult(
            Name,
            "CameraRollDomain/MediaDomain",
            artifacts.Count,
            warnings,
            artifacts);
    }
}
