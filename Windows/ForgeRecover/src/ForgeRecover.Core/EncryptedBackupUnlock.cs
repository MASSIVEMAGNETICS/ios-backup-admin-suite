using System.Diagnostics;
using System.Text.Json;

namespace ForgeRecover.Core;

public sealed record EncryptedBackupUnlockResult(
    string OutputRoot,
    int ExitCode,
    string StandardOutput,
    string StandardError,
    string ReportPath)
{
    public bool Succeeded => ExitCode == 0;
}

public sealed class EncryptedBackupUnlockService
{
    public async Task<EncryptedBackupUnlockResult> UnlockAsync(
        string backupRoot,
        string outputRoot,
        char[] password,
        string? pythonExecutable = null,
        string? helperPath = null,
        string mode = "core",
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(backupRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputRoot);
        ArgumentNullException.ThrowIfNull(password);
        if (password.Length == 0)
        {
            throw new ArgumentException("Backup password cannot be empty.", nameof(password));
        }

        if (mode is not ("core" or "all"))
        {
            throw new ArgumentOutOfRangeException(nameof(mode), "Unlock mode must be 'core' or 'all'.");
        }

        var backup = Path.GetFullPath(backupRoot);
        var output = Path.GetFullPath(outputRoot);
        if (!Directory.Exists(backup))
        {
            throw new DirectoryNotFoundException($"Encrypted backup directory does not exist: {backup}");
        }

        var manifestPlist = Path.Combine(backup, "Manifest.plist");
        var manifestDatabase = Path.Combine(backup, "Manifest.db");
        if (!File.Exists(manifestPlist) || !File.Exists(manifestDatabase))
        {
            throw new InvalidDataException("Modern encrypted backup requires Manifest.plist and Manifest.db.");
        }

        var helper = ResolveHelperPath(helperPath);
        var python = ResolvePythonExecutable(pythonExecutable, helper);
        Directory.CreateDirectory(output);

        var startInfo = new ProcessStartInfo
        {
            FileName = python,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(helper) ?? Environment.CurrentDirectory
        };
        startInfo.ArgumentList.Add(helper);
        startInfo.ArgumentList.Add("unlock");
        startInfo.ArgumentList.Add("--backup");
        startInfo.ArgumentList.Add(backup);
        startInfo.ArgumentList.Add("--out");
        startInfo.ArgumentList.Add(output);
        startInfo.ArgumentList.Add("--mode");
        startInfo.ArgumentList.Add(mode);

        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        try
        {
            if (!process.Start())
            {
                throw new InvalidOperationException("Failed to start encrypted-backup unlock helper.");
            }
        }
        catch (System.ComponentModel.Win32Exception exception)
        {
            throw new FileNotFoundException(
                $"Python runtime for encrypted-backup unlock was not found: {python}",
                python,
                exception);
        }

        var standardOutputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var standardErrorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.StandardInput.WriteAsync(password.AsMemory(), cancellationToken).ConfigureAwait(false);
        await process.StandardInput.WriteLineAsync().ConfigureAwait(false);
        process.StandardInput.Close();

        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        var standardOutput = await standardOutputTask.ConfigureAwait(false);
        var standardError = await standardErrorTask.ConfigureAwait(false);
        var reportPath = Path.Combine(output, "unlock-report.json");

        var result = new EncryptedBackupUnlockResult(
            output,
            process.ExitCode,
            standardOutput,
            standardError,
            reportPath);

        if (!result.Succeeded)
        {
            var message = string.IsNullOrWhiteSpace(standardError) ? standardOutput : standardError;
            throw new InvalidDataException(
                $"Encrypted-backup unlock failed with exit code {process.ExitCode}: {message.Trim()}");
        }

        if (!File.Exists(reportPath))
        {
            throw new InvalidDataException("Unlock helper completed without producing unlock-report.json.");
        }

        ValidateDerivedOutput(output, reportPath);
        return result;
    }

    public static void ClearPassword(char[]? password)
    {
        if (password is not null)
        {
            Array.Clear(password, 0, password.Length);
        }
    }

    private static string ResolveHelperPath(string? helperPath)
    {
        if (!string.IsNullOrWhiteSpace(helperPath))
        {
            var explicitPath = Path.GetFullPath(helperPath);
            if (!File.Exists(explicitPath))
            {
                throw new FileNotFoundException("Encrypted-backup helper was not found.", explicitPath);
            }
            return explicitPath;
        }

        var candidates = new[]
        {
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "tools", "forge_ios_backup_unlock.py")),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "tools", "forge_ios_backup_unlock.py")),
            Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, "Windows", "ForgeRecover", "tools", "forge_ios_backup_unlock.py")),
            Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, "tools", "forge_ios_backup_unlock.py"))
        };
        return candidates.FirstOrDefault(File.Exists)
            ?? throw new FileNotFoundException(
                "forge_ios_backup_unlock.py was not found. Install or package the ForgeRecover tools directory.");
    }

    private static string ResolvePythonExecutable(string? pythonExecutable, string helperPath)
    {
        if (!string.IsNullOrWhiteSpace(pythonExecutable))
        {
            return pythonExecutable;
        }

        var helperDirectory = Path.GetDirectoryName(helperPath) ?? Environment.CurrentDirectory;
        var venvPython = Path.Combine(helperDirectory, ".venv", "Scripts", "python.exe");
        return File.Exists(venvPython) ? venvPython : "python";
    }

    private static void ValidateDerivedOutput(string outputRoot, string reportPath)
    {
        var manifestPath = Path.Combine(outputRoot, "Manifest.db");
        ManifestDbResolver.EnsureSqliteHeader(manifestPath);

        using var document = JsonDocument.Parse(File.ReadAllText(reportPath));
        if (!document.RootElement.TryGetProperty("evidence_semantics", out var semantics)
            || semantics.GetString() != "derived_working_copy_not_original_evidence")
        {
            throw new InvalidDataException("Unlock report is missing the required derived-evidence classification.");
        }
    }
}
