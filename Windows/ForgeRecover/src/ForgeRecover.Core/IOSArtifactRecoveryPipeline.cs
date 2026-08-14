using System.Text.Json;

namespace ForgeRecover.Core;

public sealed record IOSArtifactRecoveryPipelineResult(
    string OutputRoot,
    string RowRecoveryReport,
    string CorrelationReport,
    int ReconstructedRows,
    int CorrelatedArtifacts,
    ExportSummary ArtifactExports);

public sealed class IOSArtifactRecoveryPipeline
{
    public async Task<IOSArtifactRecoveryPipelineResult> RunAsync(
        string databasePath,
        string outputRoot,
        string? walPath = null,
        SQLiteRowRecoveryOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputRoot);
        var database = Path.GetFullPath(databasePath);
        var output = Path.GetFullPath(outputRoot);
        Directory.CreateDirectory(output);

        var rowReport = await new SQLiteRowRecoveryService()
            .RecoverAsync(database, walPath, options, cancellationToken)
            .ConfigureAwait(false);
        var correlation = new IOSHistoricalArtifactCorrelator().Correlate(database, rowReport);

        var rowReportPath = Path.Combine(output, "sqlite-row-recovery.json");
        var correlationPath = Path.Combine(output, "ios-artifact-correlation.json");
        await WriteJsonAtomicAsync(rowReportPath, rowReport, cancellationToken).ConfigureAwait(false);
        await WriteJsonAtomicAsync(correlationPath, correlation, cancellationToken).ConfigureAwait(false);

        var extractionResults = correlation.Artifacts
            .GroupBy(artifact => artifact.Kind)
            .OrderBy(group => group.Key)
            .Select(group => new ExtractionResult(
                "recovered-" + group.Key.ToString().ToLowerInvariant() + "-candidates",
                database,
                group.Count(),
                Array.Empty<string>(),
                group.ToArray()))
            .ToArray();
        var exportRoot = Path.Combine(output, "artifact-exports");
        var export = await new ArtifactExportService()
            .ExportAsync(exportRoot, extractionResults, cancellationToken)
            .ConfigureAwait(false);

        return new IOSArtifactRecoveryPipelineResult(
            output,
            rowReportPath,
            correlationPath,
            rowReport.Rows.Count,
            correlation.Artifacts.Count,
            export);
    }

    private static async Task WriteJsonAtomicAsync(
        string path,
        object value,
        CancellationToken cancellationToken)
    {
        var temporary = path + ".tmp." + Guid.NewGuid().ToString("N");
        try
        {
            await File.WriteAllTextAsync(
                temporary,
                JsonSerializer.Serialize(value, ForgeJson.Options),
                cancellationToken).ConfigureAwait(false);
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}
