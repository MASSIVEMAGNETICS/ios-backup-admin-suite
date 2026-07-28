using System.Diagnostics;

namespace ForgeRecover.Core;

public sealed class ExternalToolRunner
{
    public async Task<ToolExecutionResult> RunAsync(
        string executable,
        IEnumerable<string> arguments,
        string? workingDirectory = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executable);
        ArgumentNullException.ThrowIfNull(arguments);

        var argumentList = arguments.ToArray();
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = string.IsNullOrWhiteSpace(workingDirectory)
                ? Environment.CurrentDirectory
                : Path.GetFullPath(workingDirectory)
        };
        foreach (var argument in argumentList)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        try
        {
            if (!process.Start())
            {
                throw new InvalidOperationException($"Failed to start external tool: {executable}");
            }
        }
        catch (System.ComponentModel.Win32Exception exception)
        {
            throw new FileNotFoundException(
                $"External tool was not found: {executable}",
                executable,
                exception);
        }

        var standardOutputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var standardErrorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        var standardOutput = await standardOutputTask.ConfigureAwait(false);
        var standardError = await standardErrorTask.ConfigureAwait(false);

        return new ToolExecutionResult(
            executable,
            argumentList,
            process.ExitCode,
            standardOutput,
            standardError);
    }
}

public sealed class LibimobiledeviceClient
{
    private readonly string? _toolDirectory;
    private readonly ExternalToolRunner _runner;

    public LibimobiledeviceClient(string? toolDirectory = null, ExternalToolRunner? runner = null)
    {
        _toolDirectory = string.IsNullOrWhiteSpace(toolDirectory) ? null : Path.GetFullPath(toolDirectory);
        _runner = runner ?? new ExternalToolRunner();
    }

    public async Task<IReadOnlyList<string>> ListDeviceIdsAsync(CancellationToken cancellationToken = default)
    {
        var result = await _runner.RunAsync(
            ToolPath("idevice_id"),
            new[] { "-l" },
            cancellationToken: cancellationToken).ConfigureAwait(false);
        EnsureSuccess(result);

        return result.StandardOutput
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public async Task<ToolExecutionResult> ReadDeviceInfoAsync(
        string deviceId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
        var result = await _runner.RunAsync(
            ToolPath("ideviceinfo"),
            new[] { "-u", deviceId },
            cancellationToken: cancellationToken).ConfigureAwait(false);
        EnsureSuccess(result);
        return result;
    }

    public async Task<ToolExecutionResult> CreateFullBackupAsync(
        string deviceId,
        string destination,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(destination);

        var output = Path.GetFullPath(destination);
        Directory.CreateDirectory(output);
        var result = await _runner.RunAsync(
            ToolPath("idevicebackup2"),
            new[] { "-u", deviceId, "backup", "--full", output },
            cancellationToken: cancellationToken).ConfigureAwait(false);
        EnsureSuccess(result);
        return result;
    }

    private string ToolPath(string baseName)
    {
        var fileName = OperatingSystem.IsWindows() ? baseName + ".exe" : baseName;
        return _toolDirectory is null ? fileName : Path.Combine(_toolDirectory, fileName);
    }

    private static void EnsureSuccess(ToolExecutionResult result)
    {
        if (result.Succeeded)
        {
            return;
        }

        var error = string.IsNullOrWhiteSpace(result.StandardError)
            ? result.StandardOutput
            : result.StandardError;
        throw new InvalidOperationException(
            $"{Path.GetFileName(result.Executable)} failed with exit code {result.ExitCode}: {error.Trim()}");
    }
}
