using System.Globalization;
using Microsoft.Data.Sqlite;

namespace ForgeRecover.Core;

public sealed record ExtractionContext(string BackupRoot, string WorkingRoot);

public interface IArtifactExtractor
{
    string Name { get; }
    ArtifactKind Kind { get; }
    Task<ExtractionResult> ExtractAsync(ExtractionContext context, CancellationToken cancellationToken = default);
}

public sealed class ArtifactExtractorRegistry
{
    private readonly IReadOnlyList<IArtifactExtractor> _extractors;

    public ArtifactExtractorRegistry(IEnumerable<IArtifactExtractor>? extractors = null)
    {
        _extractors = (extractors ?? CreateDefaultExtractors()).ToArray();
    }

    public IReadOnlyList<string> Names => _extractors.Select(extractor => extractor.Name).ToArray();

    public async Task<IReadOnlyList<ExtractionResult>> ExtractAsync(
        ExtractionContext context,
        IEnumerable<string>? selectedNames = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        Directory.CreateDirectory(context.WorkingRoot);

        var selected = selectedNames is null
            ? null
            : new HashSet<string>(selectedNames, StringComparer.OrdinalIgnoreCase);
        var results = new List<ExtractionResult>();

        foreach (var extractor in _extractors)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (selected is not null && !selected.Contains(extractor.Name))
            {
                continue;
            }

            try
            {
                results.Add(await extractor.ExtractAsync(context, cancellationToken).ConfigureAwait(false));
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                results.Add(new ExtractionResult(
                    extractor.Name,
                    string.Empty,
                    0,
                    new[] { $"Extractor failed: {exception.Message}" },
                    Array.Empty<ArtifactRecord>()));
            }
        }

        return results;
    }

    private static IEnumerable<IArtifactExtractor> CreateDefaultExtractors()
    {
        yield return new MessagesArtifactExtractor();
        yield return new CallHistoryArtifactExtractor();
        yield return new ContactsArtifactExtractor();
        yield return new CameraRollArtifactExtractor();
    }
}

internal static class SqliteArtifactHelpers
{
    private static readonly DateTimeOffset AppleEpoch = new(2001, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public static bool TableExists(SqliteConnection connection, string tableName)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = $name LIMIT 1;";
        command.Parameters.AddWithValue("$name", tableName);
        return command.ExecuteScalar() is not null;
    }

    public static HashSet<string> Columns(SqliteConnection connection, string tableName)
    {
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info({QuoteIdentifier(tableName)});";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            columns.Add(reader.GetString(reader.GetOrdinal("name")));
        }

        return columns;
    }

    public static void AddOptionalColumn(
        List<string> select,
        HashSet<string> columns,
        string tableAlias,
        string column,
        string outputAlias)
    {
        var qualified = string.IsNullOrWhiteSpace(tableAlias)
            ? QuoteIdentifier(column)
            : tableAlias + "." + QuoteIdentifier(column);
        select.Add(columns.Contains(column)
            ? $"{qualified} AS {QuoteIdentifier(outputAlias)}"
            : $"NULL AS {QuoteIdentifier(outputAlias)}");
    }

    public static string QuoteIdentifier(string identifier)
    {
        if (string.IsNullOrWhiteSpace(identifier)
            || identifier.Any(character => !(char.IsLetterOrDigit(character) || character == '_')))
        {
            throw new ArgumentException("Unsafe SQLite identifier.", nameof(identifier));
        }

        return '"' + identifier + '"';
    }

    public static string? ReadString(SqliteDataReader reader, string column)
    {
        var ordinal = TryGetOrdinal(reader, column);
        if (ordinal is null || reader.IsDBNull(ordinal.Value))
        {
            return null;
        }

        var value = reader.GetValue(ordinal.Value);
        return value is byte[] ? null : Convert.ToString(value, CultureInfo.InvariantCulture);
    }

    public static long? ReadInt64(SqliteDataReader reader, string column)
    {
        var ordinal = TryGetOrdinal(reader, column);
        if (ordinal is null || reader.IsDBNull(ordinal.Value))
        {
            return null;
        }

        try
        {
            return Convert.ToInt64(reader.GetValue(ordinal.Value), CultureInfo.InvariantCulture);
        }
        catch (Exception exception) when (exception is FormatException or InvalidCastException or OverflowException)
        {
            return null;
        }
    }

    public static double? ReadDouble(SqliteDataReader reader, string column)
    {
        var ordinal = TryGetOrdinal(reader, column);
        if (ordinal is null || reader.IsDBNull(ordinal.Value))
        {
            return null;
        }

        try
        {
            return Convert.ToDouble(reader.GetValue(ordinal.Value), CultureInfo.InvariantCulture);
        }
        catch (Exception exception) when (exception is FormatException or InvalidCastException or OverflowException)
        {
            return null;
        }
    }

    public static DateTimeOffset? AppleTimestamp(long? value)
    {
        if (value is null)
        {
            return null;
        }

        var absolute = value.Value == long.MinValue ? double.MaxValue : Math.Abs((double)value.Value);
        var seconds = absolute > 1_000_000_000_000d
            ? value.Value / 1_000_000_000d
            : value.Value;
        return AddAppleSeconds(seconds);
    }

    public static DateTimeOffset? AppleTimestamp(double? value)
    {
        if (value is null || double.IsNaN(value.Value) || double.IsInfinity(value.Value))
        {
            return null;
        }

        var seconds = Math.Abs(value.Value) > 1_000_000_000_000d
            ? value.Value / 1_000_000_000d
            : value.Value;
        return AddAppleSeconds(seconds);
    }

    public static string StableArtifactId(
        string extractor,
        string source,
        long? rowId,
        string? discriminator = null)
    {
        var material = $"{extractor}\n{source}\n{rowId?.ToString(CultureInfo.InvariantCulture)}\n{discriminator}";
        var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(material));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static DateTimeOffset? AddAppleSeconds(double seconds)
    {
        try
        {
            return AppleEpoch.AddSeconds(seconds);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    private static int? TryGetOrdinal(SqliteDataReader reader, string column)
    {
        try
        {
            return reader.GetOrdinal(column);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
        catch (IndexOutOfRangeException)
        {
            return null;
        }
    }
}
