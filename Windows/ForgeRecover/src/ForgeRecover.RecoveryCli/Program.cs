using System.Text.Json;
using System.Text.Json.Serialization;
using ForgeRecover.Core;

namespace ForgeRecover.RecoveryCli;

internal static class Program
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        if (args.Length == 0 || IsHelp(args[0]))
        {
            PrintHelp();
            return args.Length == 0 ? 2 : 0;
        }

        try
        {
            var options = Options.Parse(args.Skip(1));
            return args[0].ToLowerInvariant() switch
            {
                "unlock-backup" => await UnlockBackupAsync(options).ConfigureAwait(false),
                "recover-sqlite" => await RecoverSqliteAsync(options).ConfigureAwait(false),
                "recover-rows" => await RecoverRowsAsync(options).ConfigureAwait(false),
                "validate-corpus" => await ValidateCorpusAsync(options).ConfigureAwait(false),
                _ => Unknown(args[0])
            };
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("ERROR: Operation canceled.");
            return 130;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("ERROR: " + exception.Message);
            return exception is ArgumentException ? 2 : 3;
        }
    }

    private static async Task<int> UnlockBackupAsync(Options options)
    {
        var backup = options.Required("backup");
        var output = options.Required("out");
        var mode = options.Get("mode") ?? "core";
        var passwordEnvironmentVariable = options.Get("password-env");
        char[]? password = null;
        try
        {
            password = ReadPassword(passwordEnvironmentVariable);
            Console.WriteLine("Unlocking authorized encrypted backup into a DERIVED working copy...");
            var result = await new EncryptedBackupUnlockService().UnlockAsync(
                backup,
                output,
                password,
                options.Get("python"),
                options.Get("helper"),
                mode).ConfigureAwait(false);
            Console.WriteLine($"Derived working copy: {result.OutputRoot}");
            Console.WriteLine($"Unlock report: {result.ReportPath}");
            Console.WriteLine("Original encrypted backup was not modified.");
            return 0;
        }
        finally
        {
            EncryptedBackupUnlockService.ClearPassword(password);
        }
    }

    private static async Task<int> RecoverSqliteAsync(Options options)
    {
        var database = options.Required("db");
        var output = Path.GetFullPath(options.Required("out"));
        var recoveryOptions = new SQLiteRecoveryOptions
        {
            MinimumTextCharacters = options.GetInt("min-chars", 8),
            MaximumFragments = options.GetInt("max-fragments", 20_000),
            IncludeUtf16LittleEndian = !options.Has("no-utf16"),
            IncludeUncommittedWal = options.Has("include-uncommitted-wal"),
            IncludeCurrentWalPages = options.Has("include-current-wal"),
            IncludeWalContentStillPresentInCurrentPage = options.Has("include-duplicate-wal-content")
        };

        var report = await new SQLiteRecoveryEngine().RecoverAsync(
            database,
            options.Get("wal"),
            recoveryOptions).ConfigureAwait(false);
        Directory.CreateDirectory(Path.GetDirectoryName(output) ?? Environment.CurrentDirectory);
        await File.WriteAllTextAsync(output, JsonSerializer.Serialize(report, JsonOptions)).ConfigureAwait(false);

        Console.WriteLine($"Fragments: {report.Fragments.Count}");
        Console.WriteLine($"Validated WAL frames: {report.WalFramesValidated}");
        Console.WriteLine($"Committed WAL transactions: {report.WalTransactionsCommitted}");
        Console.WriteLine($"Report: {output}");
        foreach (var warning in report.Warnings)
        {
            Console.WriteLine("WARNING: " + warning);
        }
        return 0;
    }

    private static async Task<int> RecoverRowsAsync(Options options)
    {
        var database = options.Required("db");
        var output = Path.GetFullPath(options.Required("out"));
        var recoveryOptions = new SQLiteRowRecoveryOptions
        {
            MaximumRows = options.GetInt("max-rows", 20_000),
            MaximumWalFrames = options.GetInt("max-wal-frames", 250_000),
            MaximumOverflowPagesPerRecord = options.GetInt("max-overflow-pages", 16_384),
            MaximumTextCharactersPerValue = options.GetInt("max-text-chars", 1_000_000),
            IncludeCurrentWalRows = options.Has("include-current-wal"),
            IncludeHistoricalRowsStillCurrent = options.Has("include-still-current"),
            IncludeFreelistCandidates = !options.Has("no-freelist-candidates"),
            IncludeBTreeFreeSpaceCandidates = !options.Has("no-btree-free-space"),
            ScanUnstructuredFreelistPages = !options.Has("no-unstructured-freelist")
        };

        var report = await new SQLiteRowRecoveryEngine().RecoverAsync(
            database,
            options.Get("wal"),
            recoveryOptions).ConfigureAwait(false);
        Directory.CreateDirectory(Path.GetDirectoryName(output) ?? Environment.CurrentDirectory);
        await File.WriteAllTextAsync(output, JsonSerializer.Serialize(report, JsonOptions)).ConfigureAwait(false);

        Console.WriteLine($"Schema-aware rows/candidates: {report.Rows.Count}");
        Console.WriteLine($"Current rows indexed for comparison: {report.CurrentRowsIndexed}");
        Console.WriteLine($"Validated WAL frames: {report.WalFramesValidated}");
        Console.WriteLine($"Committed WAL transactions: {report.WalTransactionsCommitted}");
        foreach (var group in report.Rows.GroupBy(row => row.RecoveryStatus).OrderBy(group => group.Key, StringComparer.Ordinal))
            Console.WriteLine($"  {group.Key}: {group.Count()}");
        Console.WriteLine($"Report: {output}");
        foreach (var warning in report.Warnings)
            Console.WriteLine("WARNING: " + warning);
        Console.WriteLine("Evidence note: reconstructed SQLite rows are not automatically labeled deleted SMS/iMessages; artifact correlation is a separate proof step.");
        return 0;
    }

    private static async Task<int> ValidateCorpusAsync(Options options)
    {
        var root = options.Required("root");
        var output = Path.GetFullPath(options.Required("out"));
        var runner = new SQLiteRecoveryCorpusRunner();
        var report = await runner.RunAsync(root).ConfigureAwait(false);
        await runner.SaveReportAsync(report, output).ConfigureAwait(false);
        Console.WriteLine($"Corpus cases: {report.Cases}");
        Console.WriteLine($"Passed: {report.Passed}");
        Console.WriteLine($"Failed: {report.Failed}");
        Console.WriteLine($"Report: {output}");
        return report.IsValid ? 0 : 5;
    }

    private static char[] ReadPassword(string? environmentVariable)
    {
        if (!string.IsNullOrWhiteSpace(environmentVariable))
        {
            var fromEnvironment = Environment.GetEnvironmentVariable(environmentVariable);
            if (string.IsNullOrEmpty(fromEnvironment))
                throw new ArgumentException($"Environment variable {environmentVariable} is empty or missing.");
            return fromEnvironment.ToCharArray();
        }

        if (Console.IsInputRedirected)
            throw new ArgumentException(
                "Interactive password input is unavailable. Use --password-env NAME; passwords are never accepted on argv.");

        Console.Write("Backup password: ");
        var characters = new List<char>();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter)
            {
                Console.WriteLine();
                break;
            }
            if (key.Key == ConsoleKey.Backspace)
            {
                if (characters.Count > 0) characters.RemoveAt(characters.Count - 1);
                continue;
            }
            if (!char.IsControl(key.KeyChar)) characters.Add(key.KeyChar);
        }
        if (characters.Count == 0) throw new ArgumentException("Backup password cannot be empty.");
        return characters.ToArray();
    }

    private static int Unknown(string command)
    {
        Console.Error.WriteLine($"ERROR: Unknown recovery command: {command}");
        PrintHelp();
        return 2;
    }

    private static bool IsHelp(string value) =>
        value.Equals("help", StringComparison.OrdinalIgnoreCase)
        || value.Equals("--help", StringComparison.OrdinalIgnoreCase)
        || value.Equals("-h", StringComparison.OrdinalIgnoreCase);

    private static void PrintHelp() => Console.WriteLine("""
        ForgeRecover Recovery — authorized encrypted-backup unlock and SQLite history recovery

        Usage:
          forge-recover-recovery unlock-backup --backup PATH --out PATH [--mode core|all] [--password-env NAME] [--python PATH] [--helper PATH]
          forge-recover-recovery recover-sqlite --db FILE --out REPORT.json [--wal FILE] [--min-chars N] [--max-fragments N] [--include-current-wal] [--include-uncommitted-wal] [--no-utf16]
          forge-recover-recovery recover-rows --db FILE --out REPORT.json [--wal FILE] [--max-rows N] [--include-current-wal] [--include-still-current] [--no-freelist-candidates] [--no-btree-free-space]
          forge-recover-recovery validate-corpus --root CORPUS --out REPORT.json

        Evidence semantics:
          - unlock-backup requires the existing backup password; it does not guess or bypass it.
          - decrypted output is a derived working copy, never the original evidence object.
          - recover-sqlite emits classified text fragments from freelist, b-tree free space, and checksum-valid WAL history.
          - recover-rows decodes structurally valid table-leaf cells, SQLite record headers/serial types, typed columns, and overflow chains.
          - historical_row_absent_current means a structurally reconstructed row was present in historical SQLite state and no same rowid exists in the current mapped table. It is not by itself proof of a deleted SMS/iMessage.
          - validate-corpus only passes explicit known-positive / known-negative controlled cases.
        """);

    private sealed class Options
    {
        private readonly Dictionary<string, string?> _values;
        private Options(Dictionary<string, string?> values) => _values = values;

        public static Options Parse(IEnumerable<string> args)
        {
            var tokens = args.ToArray();
            var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            for (var index = 0; index < tokens.Length; index++)
            {
                var token = tokens[index];
                if (!token.StartsWith("--", StringComparison.Ordinal) || token.Length == 2)
                    throw new ArgumentException($"Unexpected positional argument: {token}");
                var key = token[2..];
                string? value = null;
                if (index + 1 < tokens.Length && !tokens[index + 1].StartsWith("--", StringComparison.Ordinal))
                    value = tokens[++index];
                values[key] = value;
            }
            return new Options(values);
        }

        public bool Has(string key) => _values.ContainsKey(key);
        public string? Get(string key) => _values.TryGetValue(key, out var value) ? value : null;
        public string Required(string key) => !string.IsNullOrWhiteSpace(Get(key))
            ? Get(key)!
            : throw new ArgumentException($"Missing required option --{key}.");
        public int GetInt(string key, int fallback)
        {
            var value = Get(key);
            if (string.IsNullOrWhiteSpace(value)) return fallback;
            return int.TryParse(value, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : throw new ArgumentException($"Option --{key} must be an integer.");
        }
    }
}
