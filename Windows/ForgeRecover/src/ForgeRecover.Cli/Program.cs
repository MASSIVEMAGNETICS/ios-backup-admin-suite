using System.Globalization;
using System.Text.Json;
using ForgeRecover.Core;

namespace ForgeRecover.Cli;

internal static class Program
{
    private const int Success = 0;
    private const int UsageError = 2;
    private const int EvidenceError = 3;
    private const int ToolError = 4;

    public static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        if (args.Length == 0 || IsHelp(args[0]))
        {
            PrintHelp();
            return args.Length == 0 ? UsageError : Success;
        }

        var command = args[0].ToLowerInvariant();
        var options = CommandOptions.Parse(args.Skip(1));
        try
        {
            return command switch
            {
                "discover" => RunDiscover(options),
                "ingest" => await RunIngestAsync(options).ConfigureAwait(false),
                "verify" => await RunVerifyAsync(options).ConfigureAwait(false),
                "extract" => await RunExtractAsync(options).ConfigureAwait(false),
                "pipeline" => await RunPipelineAsync(options).ConfigureAwait(false),
                "devices" => await RunDevicesAsync(options).ConfigureAwait(false),
                "device-info" => await RunDeviceInfoAsync(options).ConfigureAwait(false),
                "acquire" => await RunAcquireAsync(options).ConfigureAwait(false),
                "extractors" => RunExtractors(options),
                _ => UnknownCommand(command)
            };
        }
        catch (CommandLineException exception)
        {
            WriteError(exception.Message);
            return UsageError;
        }
        catch (FileNotFoundException exception)
        {
            WriteError(exception.Message);
            return ToolError;
        }
        catch (InvalidDataException exception)
        {
            WriteError(exception.Message);
            return EvidenceError;
        }
        catch (OperationCanceledException)
        {
            WriteError("Operation canceled.");
            return 130;
        }
        catch (Exception exception)
        {
            WriteError($"Unhandled failure: {exception.Message}");
            if (options.Has("verbose"))
            {
                Console.Error.WriteLine(exception);
            }

            return EvidenceError;
        }
    }

    private static int RunDiscover(CommandOptions options)
    {
        var roots = options.GetMany("root");
        var backups = new BackupDiscoveryService().Discover(roots);
        if (options.Has("json"))
        {
            Console.WriteLine(JsonSerializer.Serialize(backups, ForgeJson.Options));
            return Success;
        }

        if (backups.Count == 0)
        {
            Console.WriteLine("No Apple Devices/iTunes backups were found in the searched locations.");
            return Success;
        }

        Console.WriteLine($"Found {backups.Count} backup(s):");
        foreach (var backup in backups)
        {
            Console.WriteLine($"- {backup.BackupId}");
            Console.WriteLine($"  Path: {backup.RootPath}");
            Console.WriteLine($"  Origin: {backup.Origin}");
            Console.WriteLine($"  Modified UTC: {backup.LastModifiedUtc:O}");
            Console.WriteLine($"  Size: {FormatBytes(backup.TotalBytes)}");
            Console.WriteLine($"  Manifest.db: {(backup.HasManifestDatabase ? "yes" : "no")}");
        }

        return Success;
    }

    private static async Task<int> RunIngestAsync(CommandOptions options)
    {
        var source = options.Required("source");
        var output = options.Required("out");
        var caseName = options.Required("case");
        var examiner = options.Required("examiner");
        var notes = options.Get("notes");
        var copyEvidence = !options.Has("reference");

        Console.WriteLine(copyEvidence
            ? "Creating verified forensic copy..."
            : "Creating read-only reference case (source files remain external)...");
        var caseRoot = await new CaseVaultService().CreateAsync(
            source,
            output,
            caseName,
            examiner,
            notes,
            copyEvidence).ConfigureAwait(false);

        Console.WriteLine($"Case created: {caseRoot}");
        Console.WriteLine("Evidence hashes and chain-of-custody ledger are sealed in the case folder.");
        return Success;
    }

    private static async Task<int> RunVerifyAsync(CommandOptions options)
    {
        var caseRoot = options.Required("case-root");
        var result = await new CaseVaultService().VerifyAsync(caseRoot).ConfigureAwait(false);
        if (options.Has("json"))
        {
            Console.WriteLine(JsonSerializer.Serialize(result, ForgeJson.Options));
        }
        else
        {
            Console.WriteLine(result.IsValid
                ? $"VALID: {result.FilesChecked} evidence file(s) match their recorded SHA-256 hashes."
                : $"INVALID: {result.Issues.Count} integrity issue(s) detected.");
            foreach (var issue in result.Issues)
            {
                Console.WriteLine($"- {issue.RelativePath}: {issue.Message}");
                Console.WriteLine($"  Expected: {issue.ExpectedSha256}");
                Console.WriteLine($"  Actual:   {issue.ActualSha256 ?? "missing"}");
            }
        }

        return result.IsValid ? Success : EvidenceError;
    }

    private static async Task<int> RunExtractAsync(CommandOptions options)
    {
        var backup = options.Required("backup");
        var output = Path.GetFullPath(options.Required("out"));
        var selected = ParseCsvOptions(options.GetMany("type"));
        var results = await ExtractAndExportAsync(backup, output, selected).ConfigureAwait(false);
        PrintExtractionSummary(results.Results, results.Export);
        return results.Results.Any(result => result.Warnings.Any(warning => warning.StartsWith("Extractor failed:", StringComparison.OrdinalIgnoreCase)))
            ? EvidenceError
            : Success;
    }

    private static async Task<int> RunPipelineAsync(CommandOptions options)
    {
        var source = options.Required("source");
        var output = options.Required("out");
        var caseName = options.Required("case");
        var examiner = options.Required("examiner");
        var selected = ParseCsvOptions(options.GetMany("type"));
        var vault = new CaseVaultService();

        Console.WriteLine("Stage 1/3: creating verified forensic copy...");
        var caseRoot = await vault.CreateAsync(
            source,
            output,
            caseName,
            examiner,
            options.Get("notes"),
            copyEvidence: true).ConfigureAwait(false);

        Console.WriteLine("Stage 2/3: verifying evidence copy...");
        var verification = await vault.VerifyAsync(caseRoot).ConfigureAwait(false);
        if (!verification.IsValid)
        {
            throw new InvalidDataException("Evidence copy failed integrity verification; extraction was aborted.");
        }

        Console.WriteLine("Stage 3/3: extracting and exporting artifacts...");
        var evidenceBackup = Path.Combine(caseRoot, "evidence", "original");
        var extractionRoot = Path.Combine(caseRoot, "analysis");
        var extraction = await ExtractAndExportAsync(evidenceBackup, extractionRoot, selected).ConfigureAwait(false);
        await vault.RecordEventAsync(
            caseRoot,
            "ARTIFACT_EXTRACTION_COMPLETED",
            examiner,
            $"Extracted {extraction.Export.ArtifactCount} artifact(s) with ForgeRecover {ForgeRecoverConstants.ToolVersion}.")
            .ConfigureAwait(false);

        PrintExtractionSummary(extraction.Results, extraction.Export);
        Console.WriteLine($"Case root: {caseRoot}");
        return Success;
    }

    private static async Task<int> RunDevicesAsync(CommandOptions options)
    {
        var client = new LibimobiledeviceClient(options.Get("tool-dir"));
        var devices = await client.ListDeviceIdsAsync().ConfigureAwait(false);
        if (options.Has("json"))
        {
            Console.WriteLine(JsonSerializer.Serialize(devices, ForgeJson.Options));
        }
        else if (devices.Count == 0)
        {
            Console.WriteLine("No paired iOS devices were detected.");
        }
        else
        {
            foreach (var device in devices)
            {
                Console.WriteLine(device);
            }
        }

        return Success;
    }

    private static async Task<int> RunDeviceInfoAsync(CommandOptions options)
    {
        var deviceId = options.Required("udid");
        var client = new LibimobiledeviceClient(options.Get("tool-dir"));
        var result = await client.ReadDeviceInfoAsync(deviceId).ConfigureAwait(false);
        Console.Write(result.StandardOutput);
        return Success;
    }

    private static async Task<int> RunAcquireAsync(CommandOptions options)
    {
        var output = options.Required("out");
        var client = new LibimobiledeviceClient(options.Get("tool-dir"));
        var deviceId = options.Get("udid");
        if (string.IsNullOrWhiteSpace(deviceId))
        {
            var devices = await client.ListDeviceIdsAsync().ConfigureAwait(false);
            deviceId = devices.Count switch
            {
                0 => throw new InvalidOperationException("No paired iOS device was detected."),
                1 => devices[0],
                _ => throw new CommandLineException("Multiple devices are connected; specify --udid.")
            };
        }

        Console.WriteLine($"Starting full logical backup of {deviceId}...");
        Console.WriteLine("Keep the device unlocked, trusted, connected to power, and attached by USB.");
        var result = await client.CreateFullBackupAsync(deviceId, output).ConfigureAwait(false);
        Console.Write(result.StandardOutput);
        Console.WriteLine($"Backup completed: {Path.GetFullPath(output)}");
        return Success;
    }

    private static int RunExtractors(CommandOptions options)
    {
        var names = new ArtifactExtractorRegistry().Names;
        if (options.Has("json"))
        {
            Console.WriteLine(JsonSerializer.Serialize(names, ForgeJson.Options));
        }
        else
        {
            Console.WriteLine("Available extractors:");
            foreach (var name in names)
            {
                Console.WriteLine($"- {name}");
            }
        }

        return Success;
    }

    private static async Task<(IReadOnlyList<ExtractionResult> Results, ExportSummary Export)> ExtractAndExportAsync(
        string backup,
        string output,
        IReadOnlyCollection<string>? selected)
    {
        var backupRoot = Path.GetFullPath(backup);
        if (!Directory.Exists(backupRoot))
        {
            throw new DirectoryNotFoundException($"Backup folder does not exist: {backupRoot}");
        }

        var working = Path.Combine(output, "working");
        var exports = Path.Combine(output, "exports");
        var registry = new ArtifactExtractorRegistry();
        if (selected is not null)
        {
            var unknown = selected.Except(registry.Names, StringComparer.OrdinalIgnoreCase).ToArray();
            if (unknown.Length > 0)
            {
                throw new CommandLineException($"Unknown extractor(s): {string.Join(", ", unknown)}");
            }
        }

        var results = await registry.ExtractAsync(
            new ExtractionContext(backupRoot, working),
            selected).ConfigureAwait(false);
        var export = await new ArtifactExportService().ExportAsync(exports, results).ConfigureAwait(false);
        return (results, export);
    }

    private static void PrintExtractionSummary(IReadOnlyList<ExtractionResult> results, ExportSummary export)
    {
        foreach (var result in results)
        {
            Console.WriteLine($"{result.ExtractorName}: {result.Count} artifact(s)");
            foreach (var warning in result.Warnings)
            {
                Console.WriteLine($"  WARNING: {warning}");
            }
        }

        Console.WriteLine($"Total exported artifacts: {export.ArtifactCount}");
        Console.WriteLine($"Export folder: {export.OutputRoot}");
    }

    private static IReadOnlyCollection<string>? ParseCsvOptions(IReadOnlyList<string> values)
    {
        if (values.Count == 0)
        {
            return null;
        }

        return values
            .SelectMany(value => value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024)
        {
            return bytes.ToString(CultureInfo.InvariantCulture) + " B";
        }

        var units = new[] { "KB", "MB", "GB", "TB", "PB" };
        var value = (double)bytes;
        var unit = -1;
        do
        {
            value /= 1024;
            unit++;
        }
        while (value >= 1024 && unit < units.Length - 1);

        return value.ToString("0.##", CultureInfo.InvariantCulture) + " " + units[unit];
    }

    private static int UnknownCommand(string command)
    {
        WriteError($"Unknown command: {command}");
        PrintHelp();
        return UsageError;
    }

    private static bool IsHelp(string value) =>
        value.Equals("help", StringComparison.OrdinalIgnoreCase)
        || value.Equals("--help", StringComparison.OrdinalIgnoreCase)
        || value.Equals("-h", StringComparison.OrdinalIgnoreCase);

    private static void WriteError(string message) => Console.Error.WriteLine("ERROR: " + message);

    private static void PrintHelp()
    {
        Console.WriteLine("""
            ForgeRecover — authorized iOS backup acquisition and forensic artifact analysis for Windows

            Usage:
              forge-recover discover [--root PATH] [--json]
              forge-recover ingest --source BACKUP --out CASES --case NAME --examiner NAME [--notes TEXT] [--reference]
              forge-recover verify --case-root CASE_FOLDER [--json]
              forge-recover extract --backup BACKUP --out OUTPUT [--type messages,calls,contacts,media]
              forge-recover pipeline --source BACKUP --out CASES --case NAME --examiner NAME [--type LIST] [--notes TEXT]
              forge-recover devices [--tool-dir PATH] [--json]
              forge-recover device-info --udid DEVICE_ID [--tool-dir PATH]
              forge-recover acquire --out BACKUP_FOLDER [--udid DEVICE_ID] [--tool-dir PATH]
              forge-recover extractors [--json]

            Guardrails:
              - Use only on devices, accounts, and backups you own or are explicitly authorized to examine.
              - The suite does not bypass passcodes, Activation Lock, Apple encryption, or account authentication.
              - "Recovered" means present in an authorized logical backup or exported source; no raw deleted-page carving is performed.

            Examples:
              forge-recover discover
              forge-recover pipeline --source "C:\Backups\000081..." --out "D:\Cases" --case "Phone Review" --examiner "B. Emery"
              forge-recover extract --backup "C:\Backups\000081..." --out "D:\Analysis" --type messages,calls
              forge-recover acquire --out "D:\Acquisitions\iPhone" --udid 00008110...
            """);
    }
}

internal sealed class CommandLineException : Exception
{
    public CommandLineException(string message) : base(message)
    {
    }
}

internal sealed class CommandOptions
{
    private readonly Dictionary<string, List<string?>> _options;

    private CommandOptions(Dictionary<string, List<string?>> options)
    {
        _options = options;
    }

    public static CommandOptions Parse(IEnumerable<string> arguments)
    {
        var tokens = arguments.ToArray();
        var options = new Dictionary<string, List<string?>>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < tokens.Length; index++)
        {
            var token = tokens[index];
            if (!token.StartsWith("--", StringComparison.Ordinal) || token.Length == 2)
            {
                throw new CommandLineException($"Unexpected positional argument: {token}");
            }

            var key = token[2..];
            string? value = null;
            if (index + 1 < tokens.Length && !tokens[index + 1].StartsWith("--", StringComparison.Ordinal))
            {
                value = tokens[++index];
            }

            if (!options.TryGetValue(key, out var values))
            {
                values = new List<string?>();
                options[key] = values;
            }

            values.Add(value);
        }

        return new CommandOptions(options);
    }

    public bool Has(string key) => _options.ContainsKey(key);

    public string? Get(string key)
    {
        if (!_options.TryGetValue(key, out var values) || values.Count == 0)
        {
            return null;
        }

        return values[^1];
    }

    public IReadOnlyList<string> GetMany(string key)
    {
        return !_options.TryGetValue(key, out var values)
            ? Array.Empty<string>()
            : values.Where(value => !string.IsNullOrWhiteSpace(value)).Select(value => value!).ToArray();
    }

    public string Required(string key)
    {
        var value = Get(key);
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new CommandLineException($"Missing required option --{key}.");
        }

        return value;
    }
}
