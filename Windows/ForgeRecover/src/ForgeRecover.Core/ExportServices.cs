using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;

namespace ForgeRecover.Core;

public sealed record ExportSummary(
    string OutputRoot,
    int ArtifactCount,
    IReadOnlyList<string> Files);

public sealed class ArtifactExportService
{
    public async Task<ExportSummary> ExportAsync(
        string outputRoot,
        IReadOnlyList<ExtractionResult> results,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputRoot);
        ArgumentNullException.ThrowIfNull(results);

        var fullOutputRoot = Path.GetFullPath(outputRoot);
        Directory.CreateDirectory(fullOutputRoot);
        var generated = new List<string>();
        var allArtifacts = results.SelectMany(result => result.Artifacts).ToArray();

        var summaryPath = Path.Combine(fullOutputRoot, "extraction-summary.json");
        await WriteAtomicAsync(
            summaryPath,
            JsonSerializer.Serialize(results, ForgeJson.Options),
            cancellationToken).ConfigureAwait(false);
        generated.Add(summaryPath);

        foreach (var result in results)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var safeName = SafeName(result.ExtractorName);
            var jsonPath = Path.Combine(fullOutputRoot, safeName + ".json");
            var csvPath = Path.Combine(fullOutputRoot, safeName + ".csv");
            var htmlPath = Path.Combine(fullOutputRoot, safeName + ".html");

            await WriteAtomicAsync(
                jsonPath,
                JsonSerializer.Serialize(result, ForgeJson.Options),
                cancellationToken).ConfigureAwait(false);
            await WriteAtomicAsync(csvPath, BuildCsv(result.Artifacts), cancellationToken).ConfigureAwait(false);
            await WriteAtomicAsync(htmlPath, BuildHtml(result), cancellationToken).ConfigureAwait(false);

            generated.Add(jsonPath);
            generated.Add(csvPath);
            generated.Add(htmlPath);
        }

        var combinedCsv = Path.Combine(fullOutputRoot, "all-artifacts.csv");
        await WriteAtomicAsync(combinedCsv, BuildCsv(allArtifacts), cancellationToken).ConfigureAwait(false);
        generated.Add(combinedCsv);

        return new ExportSummary(
            fullOutputRoot,
            allArtifacts.Length,
            generated.Select(path => Path.GetRelativePath(fullOutputRoot, path).Replace('\\', '/')).ToArray());
    }

    private static string BuildCsv(IEnumerable<ArtifactRecord> artifacts)
    {
        var builder = new StringBuilder();
        AppendCsvRow(builder, new[]
        {
            "artifact_id",
            "kind",
            "timestamp_utc",
            "primary",
            "secondary",
            "body",
            "direction",
            "source_database",
            "source_row_id",
            "metadata_json"
        });

        foreach (var artifact in artifacts)
        {
            AppendCsvRow(builder, new[]
            {
                artifact.ArtifactId,
                artifact.Kind.ToString(),
                artifact.TimestampUtc?.ToString("O", CultureInfo.InvariantCulture),
                artifact.Primary,
                artifact.Secondary,
                artifact.Body,
                artifact.Direction,
                artifact.SourceDatabase,
                artifact.SourceRowId?.ToString(CultureInfo.InvariantCulture),
                JsonSerializer.Serialize(artifact.Metadata, ForgeJson.Options)
            });
        }

        return builder.ToString();
    }

    private static void AppendCsvRow(StringBuilder builder, IEnumerable<string?> values)
    {
        var first = true;
        foreach (var value in values)
        {
            if (!first)
            {
                builder.Append(',');
            }

            first = false;
            builder.Append(EscapeCsv(value));
        }

        builder.AppendLine();
    }

    private static string EscapeCsv(string? value)
    {
        if (value is null)
        {
            return string.Empty;
        }

        var requiresQuotes = value.IndexOfAny(new[] { ',', '"', '\r', '\n' }) >= 0;
        if (!requiresQuotes)
        {
            return value;
        }

        return '"' + value.Replace("\"", "\"\"", StringComparison.Ordinal) + '"';
    }

    private static string BuildHtml(ExtractionResult result)
    {
        var builder = new StringBuilder();
        builder.AppendLine("<!doctype html>");
        builder.AppendLine("<html lang=\"en\"><head><meta charset=\"utf-8\">");
        builder.Append("<title>").Append(Html(result.ExtractorName)).AppendLine(" - ForgeRecover</title>");
        builder.AppendLine("<style>body{font-family:Segoe UI,Arial,sans-serif;margin:24px;color:#111}table{border-collapse:collapse;width:100%;font-size:13px}th,td{border:1px solid #ccc;padding:7px;vertical-align:top;text-align:left}th{background:#f3f3f3;position:sticky;top:0}.meta{white-space:pre-wrap;word-break:break-word}.muted{color:#555}</style></head><body>");
        builder.Append("<h1>ForgeRecover: ").Append(Html(result.ExtractorName)).AppendLine("</h1>");
        builder.Append("<p class=\"muted\">Source: ").Append(Html(result.SourceDatabase))
            .Append(" | Artifacts: ").Append(result.Count.ToString(CultureInfo.InvariantCulture)).AppendLine("</p>");

        if (result.Warnings.Count > 0)
        {
            builder.AppendLine("<h2>Warnings</h2><ul>");
            foreach (var warning in result.Warnings)
            {
                builder.Append("<li>").Append(Html(warning)).AppendLine("</li>");
            }

            builder.AppendLine("</ul>");
        }

        builder.AppendLine("<table><thead><tr><th>UTC</th><th>Kind</th><th>Primary</th><th>Secondary</th><th>Direction</th><th>Body</th><th>Source</th><th>Metadata</th></tr></thead><tbody>");
        foreach (var artifact in result.Artifacts)
        {
            builder.Append("<tr><td>").Append(Html(artifact.TimestampUtc?.ToString("O", CultureInfo.InvariantCulture)))
                .Append("</td><td>").Append(Html(artifact.Kind.ToString()))
                .Append("</td><td>").Append(Html(artifact.Primary))
                .Append("</td><td>").Append(Html(artifact.Secondary))
                .Append("</td><td>").Append(Html(artifact.Direction))
                .Append("</td><td>").Append(Html(artifact.Body))
                .Append("</td><td>").Append(Html($"{artifact.SourceDatabase}#{artifact.SourceRowId}"))
                .Append("</td><td class=\"meta\">")
                .Append(Html(JsonSerializer.Serialize(artifact.Metadata, ForgeJson.Options)))
                .AppendLine("</td></tr>");
        }

        builder.AppendLine("</tbody></table></body></html>");
        return builder.ToString();
    }

    private static string Html(string? value) => WebUtility.HtmlEncode(value ?? string.Empty);

    private static async Task WriteAtomicAsync(string path, string content, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporaryPath = path + ".tmp." + Guid.NewGuid().ToString("N");
        await File.WriteAllTextAsync(temporaryPath, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true), cancellationToken)
            .ConfigureAwait(false);
        File.Move(temporaryPath, path, overwrite: true);
    }

    private static string SafeName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars().ToHashSet();
        var safe = new string(name.Select(character => invalid.Contains(character) ? '_' : character).ToArray());
        return string.IsNullOrWhiteSpace(safe) ? "artifacts" : safe;
    }
}
