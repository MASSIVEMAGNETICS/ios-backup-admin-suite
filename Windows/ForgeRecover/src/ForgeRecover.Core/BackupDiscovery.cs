namespace ForgeRecover.Core;

public static class AppleBackupPaths
{
    public static IReadOnlyList<string> GetDefaultWindowsRoots()
    {
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

        AddIfNotEmpty(roots, Path.Combine(userProfile, "Apple", "MobileSync", "Backup"));
        AddIfNotEmpty(roots, Path.Combine(appData, "Apple Computer", "MobileSync", "Backup"));
        return roots.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static void AddIfNotEmpty(HashSet<string> roots, string path)
    {
        if (!string.IsNullOrWhiteSpace(path))
        {
            roots.Add(Path.GetFullPath(path));
        }
    }
}

public sealed class BackupDiscoveryService
{
    public IReadOnlyList<BackupDescriptor> Discover(IEnumerable<string>? additionalRoots = null)
    {
        var roots = new HashSet<string>(AppleBackupPaths.GetDefaultWindowsRoots(), StringComparer.OrdinalIgnoreCase);
        if (additionalRoots is not null)
        {
            foreach (var root in additionalRoots.Where(root => !string.IsNullOrWhiteSpace(root)))
            {
                roots.Add(Path.GetFullPath(root));
            }
        }

        var results = new List<BackupDescriptor>();
        foreach (var root in roots.OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            if (!Directory.Exists(root))
            {
                continue;
            }

            foreach (var backupDirectory in Directory.EnumerateDirectories(root).OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
            {
                var manifestDatabase = Path.Combine(backupDirectory, "Manifest.db");
                var legacyManifest = Path.Combine(backupDirectory, "Manifest.mbdb");
                if (!File.Exists(manifestDatabase) && !File.Exists(legacyManifest))
                {
                    continue;
                }

                var info = new DirectoryInfo(backupDirectory);
                results.Add(new BackupDescriptor(
                    backupDirectory,
                    info.Name,
                    backupDirectory.Contains("Apple Computer", StringComparison.OrdinalIgnoreCase)
                        ? BackupOrigin.ITunesDesktop
                        : BackupOrigin.AppleDevices,
                    new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero),
                    CalculateDirectorySize(backupDirectory),
                    File.Exists(manifestDatabase)));
            }
        }

        return results
            .OrderByDescending(item => item.LastModifiedUtc)
            .ThenBy(item => item.RootPath, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static long CalculateDirectorySize(string root)
    {
        long total = 0;
        foreach (var file in Directory.EnumerateFiles(root, "*", new EnumerationOptions
                 {
                     RecurseSubdirectories = true,
                     IgnoreInaccessible = true,
                     AttributesToSkip = FileAttributes.ReparsePoint
                 }))
        {
            try
            {
                total = checked(total + new FileInfo(file).Length);
            }
            catch (IOException)
            {
                // A live backup may rotate a file while discovery runs. Ingest is strict.
            }
            catch (UnauthorizedAccessException)
            {
                // Discovery is best-effort. Ingest reports inaccessible evidence.
            }
            catch (OverflowException)
            {
                return long.MaxValue;
            }
        }

        return total;
    }
}
