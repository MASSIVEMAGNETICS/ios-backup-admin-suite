using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Data;
using ForgeRecover.Core;
using Microsoft.Win32;

namespace ForgeRecover.App;

public partial class MainWindow : Window
{
    private CancellationTokenSource? _operationCancellation;

    public MainWindow()
    {
        InitializeComponent();
        ArtifactsView = CollectionViewSource.GetDefaultView(Artifacts);
        ArtifactsView.Filter = FilterArtifact;
        DataContext = this;

        BackupComboBox.SelectionChanged += (_, _) => UpdateBackupDetails();
        Loaded += async (_, _) =>
        {
            ExaminerTextBox.Text = Environment.UserName;
            CasesOutputTextBox.Text = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                "ForgeRecover Cases");
            AcquisitionOutputTextBox.Text = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                "ForgeRecover Acquisitions");
            await RefreshBackupsAsync().ConfigureAwait(true);
        };
    }

    public ObservableCollection<BackupDescriptor> Backups { get; } = new();
    public ObservableCollection<ArtifactSelectionRow> Artifacts { get; } = new();
    public ICollectionView ArtifactsView { get; }

    private async void RefreshBackups_Click(object sender, RoutedEventArgs e) =>
        await RefreshBackupsAsync().ConfigureAwait(true);

    private async Task RefreshBackupsAsync()
    {
        await ExecuteBusyAsync("Discovering local Apple backups...", async cancellationToken =>
        {
            var discovered = await Task.Run(
                () => new BackupDiscoveryService().Discover(),
                cancellationToken).ConfigureAwait(true);
            Backups.Clear();
            foreach (var backup in discovered)
            {
                Backups.Add(backup);
            }

            if (Backups.Count > 0)
            {
                BackupComboBox.SelectedIndex = 0;
            }

            Log($"Discovered {Backups.Count} Apple Devices/iTunes backup(s).");
        }).ConfigureAwait(true);
    }

    private void BrowseBackup_Click(object sender, RoutedEventArgs e)
    {
        var path = ChooseFolder("Select an Apple Devices/iTunes backup folder", SelectedBackupPath());
        if (path is null)
        {
            return;
        }

        if (!File.Exists(Path.Combine(path, "Manifest.db")))
        {
            MessageBox.Show(
                this,
                "The selected folder does not contain Manifest.db.",
                "Not an iOS backup",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        var existing = Backups.FirstOrDefault(item =>
            string.Equals(item.RootPath, path, StringComparison.OrdinalIgnoreCase));
        if (existing is null)
        {
            var info = new DirectoryInfo(path);
            existing = new BackupDescriptor(
                path,
                info.Name,
                BackupOrigin.ImportedFolder,
                new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero),
                0,
                true);
            Backups.Insert(0, existing);
        }

        BackupComboBox.SelectedItem = existing;
        Log($"Selected backup: {path}");
    }

    private void BrowseCasesOutput_Click(object sender, RoutedEventArgs e)
    {
        var path = ChooseFolder("Select the parent folder for forensic cases", CasesOutputTextBox.Text);
        if (path is not null)
        {
            CasesOutputTextBox.Text = path;
        }
    }

    private void BrowseCaseRoot_Click(object sender, RoutedEventArgs e)
    {
        var path = ChooseFolder("Select a ForgeRecover case folder", CaseRootTextBox.Text);
        if (path is not null)
        {
            CaseRootTextBox.Text = path;
        }
    }

    private void BrowseToolDirectory_Click(object sender, RoutedEventArgs e)
    {
        var path = ChooseFolder("Select the libimobiledevice tools folder", ToolDirectoryTextBox.Text);
        if (path is not null)
        {
            ToolDirectoryTextBox.Text = path;
        }
    }

    private void BrowseAcquisitionOutput_Click(object sender, RoutedEventArgs e)
    {
        var path = ChooseFolder("Select the acquisition output folder", AcquisitionOutputTextBox.Text);
        if (path is not null)
        {
            AcquisitionOutputTextBox.Text = path;
        }
    }

    private async void RunPipeline_Click(object sender, RoutedEventArgs e)
    {
        var source = SelectedBackupPath();
        if (source is null)
        {
            ShowValidation("Select or browse to an iOS backup first.");
            return;
        }

        var caseName = CaseNameTextBox.Text.Trim();
        var examiner = ExaminerTextBox.Text.Trim();
        var output = CasesOutputTextBox.Text.Trim();
        var extractors = SelectedExtractors();
        if (string.IsNullOrWhiteSpace(caseName)
            || string.IsNullOrWhiteSpace(examiner)
            || string.IsNullOrWhiteSpace(output))
        {
            ShowValidation("Case name, examiner, and cases folder are required.");
            return;
        }

        if (extractors.Count == 0)
        {
            ShowValidation("Select at least one artifact plugin.");
            return;
        }

        await ExecuteBusyAsync("Creating verified forensic case...", async cancellationToken =>
        {
            var vault = new CaseVaultService();
            Log("Stage 1/3 — copying and hashing evidence.");
            var caseRoot = await vault.CreateAsync(
                source,
                output,
                caseName,
                examiner,
                NotesTextBox.Text,
                copyEvidence: true,
                cancellationToken).ConfigureAwait(true);

            Log("Stage 2/3 — re-verifying copied evidence.");
            var verification = await vault.VerifyAsync(caseRoot, cancellationToken).ConfigureAwait(true);
            if (!verification.IsValid)
            {
                throw new InvalidDataException(
                    $"Evidence verification failed with {verification.Issues.Count} issue(s). Analysis was aborted.");
            }

            Log($"Verified {verification.FilesChecked} evidence file(s). Stage 3/3 — extracting artifacts.");
            var analysisRoot = Path.Combine(caseRoot, "analysis");
            var results = await new ArtifactExtractorRegistry().ExtractAsync(
                new ExtractionContext(
                    Path.Combine(caseRoot, "evidence", "original"),
                    Path.Combine(analysisRoot, "working")),
                extractors,
                cancellationToken).ConfigureAwait(true);
            var export = await new ArtifactExportService().ExportAsync(
                Path.Combine(analysisRoot, "exports"),
                results,
                cancellationToken).ConfigureAwait(true);

            await vault.RecordEventAsync(
                caseRoot,
                "ARTIFACT_EXTRACTION_COMPLETED",
                examiner,
                $"Extracted {export.ArtifactCount} artifact(s) with ForgeRecover {ForgeRecoverConstants.ToolVersion}.",
                cancellationToken: cancellationToken).ConfigureAwait(true);

            CaseRootTextBox.Text = caseRoot;
            PopulateArtifacts(results);
            MainTabs.SelectedIndex = 0;
            Log($"Case complete: {caseRoot}");
            Log($"Exported {export.ArtifactCount} artifact(s) to {export.OutputRoot}.");
            StatusTextBlock.Text = $"Complete — {export.ArtifactCount} artifacts";
        }).ConfigureAwait(true);
    }

    private async void VerifyCase_Click(object sender, RoutedEventArgs e)
    {
        var caseRoot = CaseRootTextBox.Text.Trim();
        if (!Directory.Exists(caseRoot))
        {
            ShowValidation("Select a valid ForgeRecover case folder.");
            return;
        }

        await ExecuteBusyAsync("Verifying evidence hashes...", async cancellationToken =>
        {
            var result = await new CaseVaultService().VerifyAsync(caseRoot, cancellationToken).ConfigureAwait(true);
            if (result.IsValid)
            {
                Log($"Integrity valid: {result.FilesChecked} file(s) match recorded SHA-256 hashes.");
                MessageBox.Show(
                    this,
                    $"VALID\n\n{result.FilesChecked} evidence file(s) match their recorded SHA-256 hashes.",
                    "Case verification",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            foreach (var issue in result.Issues)
            {
                Log($"INTEGRITY FAILURE — {issue.RelativePath}: {issue.Message}");
            }

            MessageBox.Show(
                this,
                $"INVALID\n\n{result.Issues.Count} evidence integrity issue(s) were detected. Review the activity log.",
                "Case verification",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }).ConfigureAwait(true);
    }

    private void OpenCaseFolder_Click(object sender, RoutedEventArgs e)
    {
        var path = CaseRootTextBox.Text.Trim();
        if (!Directory.Exists(path))
        {
            ShowValidation("Select a valid case folder first.");
            return;
        }

        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    private async void DetectDevices_Click(object sender, RoutedEventArgs e)
    {
        await ExecuteBusyAsync("Detecting paired iOS devices...", async cancellationToken =>
        {
            var client = new LibimobiledeviceClient(NullIfWhiteSpace(ToolDirectoryTextBox.Text));
            var devices = await client.ListDeviceIdsAsync(cancellationToken).ConfigureAwait(true);
            DeviceComboBox.ItemsSource = devices;
            if (devices.Count > 0)
            {
                DeviceComboBox.SelectedIndex = 0;
            }

            Log(devices.Count == 0
                ? "No paired iOS devices were detected."
                : $"Detected {devices.Count} paired device(s).");
        }).ConfigureAwait(true);
    }

    private async void AcquireDevice_Click(object sender, RoutedEventArgs e)
    {
        var deviceId = DeviceComboBox.SelectedItem as string;
        var outputParent = AcquisitionOutputTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(deviceId) || string.IsNullOrWhiteSpace(outputParent))
        {
            ShowValidation("Detect and select a paired device, then choose an acquisition folder.");
            return;
        }

        var destination = Path.Combine(
            outputParent,
            $"{SanitizeFileName(deviceId)}_{DateTimeOffset.UtcNow:yyyyMMdd_HHmmss}");
        await ExecuteBusyAsync("Acquiring full logical backup...", async cancellationToken =>
        {
            Log($"Starting authorized logical acquisition for {deviceId}.");
            Log("Keep the device unlocked, trusted, connected by USB, and attached to power.");
            var client = new LibimobiledeviceClient(NullIfWhiteSpace(ToolDirectoryTextBox.Text));
            var result = await client.CreateFullBackupAsync(deviceId, destination, cancellationToken)
                .ConfigureAwait(true);
            if (!string.IsNullOrWhiteSpace(result.StandardOutput))
            {
                Log(result.StandardOutput.Trim());
            }

            Log($"Acquisition completed: {destination}");
            MessageBox.Show(
                this,
                $"Logical backup completed.\n\n{destination}",
                "Acquisition complete",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }).ConfigureAwait(true);
    }

    private void ArtifactSearch_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        ArtifactsView.Refresh();
        UpdateArtifactCount();
    }

    private void SelectVisible_Click(object sender, RoutedEventArgs e)
    {
        foreach (var row in ArtifactsView.Cast<ArtifactSelectionRow>())
        {
            row.IsSelected = true;
        }
    }

    private void ClearSelection_Click(object sender, RoutedEventArgs e)
    {
        foreach (var row in Artifacts)
        {
            row.IsSelected = false;
        }
    }

    private async void ExportSelected_Click(object sender, RoutedEventArgs e)
    {
        var selected = Artifacts.Where(row => row.IsSelected).Select(row => row.Artifact).ToArray();
        if (selected.Length == 0)
        {
            ShowValidation("Select at least one artifact in the preview grid.");
            return;
        }

        var destination = ChooseFolder("Select a folder for the selective export", CaseRootTextBox.Text);
        if (destination is null)
        {
            return;
        }

        var output = Path.Combine(destination, $"selected-export-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}");
        await ExecuteBusyAsync("Exporting selected artifacts...", async cancellationToken =>
        {
            var results = selected
                .GroupBy(artifact => artifact.Kind)
                .Select(group => new ExtractionResult(
                    "selected-" + group.Key.ToString().ToLowerInvariant(),
                    "selective-workbench-export",
                    group.Count(),
                    Array.Empty<string>(),
                    group.ToArray()))
                .ToArray();
            var summary = await new ArtifactExportService().ExportAsync(output, results, cancellationToken)
                .ConfigureAwait(true);
            Log($"Selective export complete: {summary.ArtifactCount} artifact(s) to {summary.OutputRoot}.");
            Process.Start(new ProcessStartInfo(summary.OutputRoot) { UseShellExecute = true });
        }).ConfigureAwait(true);
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        _operationCancellation?.Cancel();
        StatusTextBlock.Text = "Canceling...";
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_operationCancellation is null)
        {
            return;
        }

        var answer = MessageBox.Show(
            this,
            "A forensic operation is still active. Cancel it and close ForgeRecover?",
            "Active operation",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (answer == MessageBoxResult.No)
        {
            e.Cancel = true;
            return;
        }

        _operationCancellation.Cancel();
    }

    private async Task ExecuteBusyAsync(string status, Func<CancellationToken, Task> operation)
    {
        if (_operationCancellation is not null)
        {
            MessageBox.Show(this, "Another operation is already active.", "ForgeRecover", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        _operationCancellation = new CancellationTokenSource();
        SetBusy(true, status);
        try
        {
            await operation(_operationCancellation.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            Log("Operation canceled.");
            StatusTextBlock.Text = "Canceled";
        }
        catch (Exception exception)
        {
            Log("ERROR — " + exception);
            StatusTextBlock.Text = "Failed";
            MessageBox.Show(
                this,
                exception.Message,
                "ForgeRecover operation failed",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            _operationCancellation.Dispose();
            _operationCancellation = null;
            SetBusy(false, StatusTextBlock.Text);
        }
    }

    private void PopulateArtifacts(IEnumerable<ExtractionResult> results)
    {
        Artifacts.Clear();
        foreach (var artifact in results
                     .SelectMany(result => result.Artifacts)
                     .OrderByDescending(artifact => artifact.TimestampUtc)
                     .ThenBy(artifact => artifact.Kind))
        {
            Artifacts.Add(new ArtifactSelectionRow(artifact));
        }

        ArtifactsView.Refresh();
        UpdateArtifactCount();
    }

    private bool FilterArtifact(object value)
    {
        if (value is not ArtifactSelectionRow row)
        {
            return false;
        }

        var query = ArtifactSearchTextBox?.Text.Trim();
        if (string.IsNullOrWhiteSpace(query))
        {
            return true;
        }

        return row.Timestamp.Contains(query, StringComparison.OrdinalIgnoreCase)
               || row.Kind.Contains(query, StringComparison.OrdinalIgnoreCase)
               || row.Primary.Contains(query, StringComparison.OrdinalIgnoreCase)
               || row.Secondary.Contains(query, StringComparison.OrdinalIgnoreCase)
               || row.Direction.Contains(query, StringComparison.OrdinalIgnoreCase)
               || row.Body.Contains(query, StringComparison.OrdinalIgnoreCase)
               || row.Source.Contains(query, StringComparison.OrdinalIgnoreCase);
    }

    private void UpdateArtifactCount()
    {
        var visible = ArtifactsView.Cast<object>().Count();
        ArtifactCountText.Text = $"{visible:n0} visible / {Artifacts.Count:n0} total";
    }

    private void UpdateBackupDetails()
    {
        if (BackupComboBox.SelectedItem is not BackupDescriptor backup)
        {
            BackupDetailsText.Text = string.Empty;
            return;
        }

        BackupDetailsText.Text =
            $"Origin: {backup.Origin}\nModified UTC: {backup.LastModifiedUtc:O}\n" +
            (backup.TotalBytes > 0 ? $"Size: {FormatBytes(backup.TotalBytes)}" : "Size: not indexed");
    }

    private IReadOnlyList<string> SelectedExtractors()
    {
        var selected = new List<string>();
        if (MessagesCheckBox.IsChecked == true) selected.Add("messages");
        if (CallsCheckBox.IsChecked == true) selected.Add("calls");
        if (ContactsCheckBox.IsChecked == true) selected.Add("contacts");
        if (MediaCheckBox.IsChecked == true) selected.Add("media");
        return selected;
    }

    private string? SelectedBackupPath() =>
        (BackupComboBox.SelectedItem as BackupDescriptor)?.RootPath;

    private void SetBusy(bool busy, string status)
    {
        RunPipelineButton.IsEnabled = !busy;
        VerifyCaseButton.IsEnabled = !busy;
        DetectDevicesButton.IsEnabled = !busy;
        AcquireButton.IsEnabled = !busy;
        ExportSelectedButton.IsEnabled = !busy;
        CancelButton.IsEnabled = busy;
        BusyProgressBar.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        StatusTextBlock.Text = status;
    }

    private void Log(string message)
    {
        var line = $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}] {message}";
        ActivityTextBox.AppendText(line + Environment.NewLine);
        ActivityTextBox.ScrollToEnd();
    }

    private void ShowValidation(string message) => MessageBox.Show(
        this,
        message,
        "ForgeRecover",
        MessageBoxButton.OK,
        MessageBoxImage.Warning);

    private string? ChooseFolder(string title, string? initialDirectory)
    {
        var dialog = new OpenFolderDialog
        {
            Title = title,
            Multiselect = false
        };
        if (!string.IsNullOrWhiteSpace(initialDirectory) && Directory.Exists(initialDirectory))
        {
            dialog.InitialDirectory = initialDirectory;
        }

        return dialog.ShowDialog(this) == true ? dialog.FolderName : null;
    }

    private static string? NullIfWhiteSpace(string value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string SanitizeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars().ToHashSet();
        var sanitized = new string(value.Select(character => invalid.Contains(character) ? '_' : character).ToArray());
        return string.IsNullOrWhiteSpace(sanitized) ? "device" : sanitized;
    }

    private static string FormatBytes(long bytes)
    {
        var units = new[] { "B", "KB", "MB", "GB", "TB", "PB" };
        var value = (double)bytes;
        var index = 0;
        while (value >= 1024 && index < units.Length - 1)
        {
            value /= 1024;
            index++;
        }

        return $"{value:0.##} {units[index]}";
    }
}
