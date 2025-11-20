using System.Collections.Concurrent;
using System.Text.Json;

namespace DuplicateDetector.Core;

public class DuplicateScanner
{
    private readonly PixelHasher _hasher;

    public DuplicateScanner(PixelHasher? hasher = null)
    {
        _hasher = hasher ?? new PixelHasher();
    }

    public async Task<DuplicateReport> ScanAsync(DuplicateScannerOptions options, CancellationToken cancellationToken = default)
    {
        var report = new DuplicateReport();

        if (string.IsNullOrWhiteSpace(options.RootPath) || !Directory.Exists(options.RootPath))
        {
            report.Errors.Add("Root path is missing or does not exist.");
            return report;
        }

        var sourceExtensions = options.Extensions ?? Array.Empty<string>();
        var extensions = sourceExtensions.Count > 0
            ? sourceExtensions.Select(e => e.StartsWith('.') ? e : "." + e).ToArray()
            : new[] { ".png" };

        var manifestPath = options.ManifestPath ?? Path.Combine(options.RootPath, "duplicate-manifest.json");
        var manifest = options.UseManifest
            ? await LoadManifestAsync(manifestPath, cancellationToken)
            : new ConcurrentDictionary<string, ManifestEntry>(StringComparer.OrdinalIgnoreCase);

        var files = EnumerateFiles(options.RootPath, options.DisplayPattern, extensions);
        report.Stats = report.Stats with { TotalFiles = files.Count };

        var filesByLength = files
            .GroupBy(f => f.Length)
            .Where(g => g.Count() > 1)
            .SelectMany(g => g)
            .ToList();

        var results = new ConcurrentDictionary<string, (PixelHashResult hash, CaptureImageInfo info)>(StringComparer.OrdinalIgnoreCase);
        var errors = new ConcurrentBag<string>();
        var hashedCount = 0;
        var maxDegree = options.MaxDegreeOfParallelism.HasValue && options.MaxDegreeOfParallelism.Value > 0
            ? options.MaxDegreeOfParallelism.Value
            : Environment.ProcessorCount;
        using var semaphore = new SemaphoreSlim(maxDegree);
        var tasks = new List<Task>();

        foreach (var file in filesByLength)
        {
            tasks.Add(ProcessFileAsync(file));
        }

        await Task.WhenAll(tasks);

        var groups = results.Values
            .GroupBy(r => r.hash.Hash, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .Select(g => new DuplicateGroup(
                g.Key,
                new ImageDimensions(g.First().hash.Width, g.First().hash.Height),
                g.Select(x => x.info).OrderBy(i => i.DisplayId).ThenBy(i => i.TimestampUtc).ToList()))
            .ToList();

        report.Groups.AddRange(groups);

        var duplicateFiles = groups.Sum(g => g.Files.Count);
        var deletedCount = 0;

        if (options.DeleteDuplicates)
        {
            foreach (var group in groups)
            {
                var byDisplay = group.Files.GroupBy(f => f.DisplayId, StringComparer.OrdinalIgnoreCase);
                foreach (var displayGroup in byDisplay)
                {
                    var ordered = options.KeepStrategy == KeepStrategy.Newest
                        ? displayGroup.OrderByDescending(f => f.TimestampUtc).ToList()
                        : displayGroup.OrderBy(f => f.TimestampUtc).ToList();

                    foreach (var duplicate in ordered.Skip(1))
                    {
                        report.PlannedDeletions.Add(duplicate.Path);
                        if (options.DryRun)
                        {
                            continue;
                        }

                        try
                        {
                            File.Delete(duplicate.Path);
                            report.DeletedFiles.Add(duplicate.Path);
                            deletedCount++;
                        }
                        catch (Exception ex)
                        {
                            errors.Add($"Failed to delete {duplicate.Path}: {ex.Message}");
                        }
                    }
                }
            }
        }

        foreach (var error in errors)
        {
            report.Errors.Add(error);
        }

        if (options.UseManifest)
        {
            RemoveStaleManifestEntries(manifest, files.Select(f => f.Path));
            await SaveManifestAsync(manifestPath, manifest, cancellationToken);
        }

        report.Stats = new DuplicateStats(
            report.Stats.TotalFiles,
            hashedCount,
            report.Groups.Count,
            duplicateFiles,
            deletedCount);

        return report;

        async Task ProcessFileAsync(FileCandidate file)
        {
            await semaphore.WaitAsync(cancellationToken);
            try
            {
                PixelHashResult hash;
                if (options.UseManifest &&
                    manifest.TryGetValue(file.Path, out var entry) &&
                    entry.LastWriteUtc == file.LastWriteUtc)
                {
                    hash = new PixelHashResult(entry.Hash, entry.Width, entry.Height);
                }
                else
                {
                    hash = await _hasher.HashAsync(file.Path, cancellationToken);
                    Interlocked.Increment(ref hashedCount);
                }

                var info = new CaptureImageInfo(file.Path, file.DisplayId, file.LastWriteUtc);
                results[file.Path] = (hash, info);

                if (options.UseManifest)
                {
                    manifest[file.Path] = new ManifestEntry
                    {
                        Path = file.Path,
                        LastWriteUtc = file.LastWriteUtc,
                        Hash = hash.Hash,
                        Width = hash.Width,
                        Height = hash.Height
                    };
                }
            }
            catch (Exception ex)
            {
                errors.Add($"Failed to hash {file.Path}: {ex.Message}");
            }
            finally
            {
                semaphore.Release();
            }
        }
    }

    private static List<FileCandidate> EnumerateFiles(string root, string displayPattern, IEnumerable<string> extensions)
    {
        var files = new List<FileCandidate>();
        var displays = Directory.GetDirectories(root, displayPattern, SearchOption.TopDirectoryOnly);
        var folderName = Path.GetFileName(root);
        if (displays.Length == 0 && MatchesPattern(folderName, displayPattern))
        {
            displays = new[] { root };
        }
        var normalizedExtensions = new HashSet<string>(extensions.Select(e => e.ToLowerInvariant()));

        foreach (var display in displays)
        {
            var displayId = Path.GetFileName(display);
            foreach (var file in Directory.EnumerateFiles(display, "*", SearchOption.AllDirectories))
            {
                var extension = Path.GetExtension(file).ToLowerInvariant();
                if (!normalizedExtensions.Contains(extension))
                {
                    continue;
                }

                var info = new FileInfo(file);
                files.Add(new FileCandidate(file, displayId, info.Length, info.LastWriteTimeUtc));
            }
        }

        return files;
    }

    private static async Task<ConcurrentDictionary<string, ManifestEntry>> LoadManifestAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var entries = await JsonSerializer.DeserializeAsync<List<ManifestEntry>>(stream, cancellationToken: cancellationToken)
                          ?? new List<ManifestEntry>();
            return new ConcurrentDictionary<string, ManifestEntry>(entries.ToDictionary(e => e.Path, StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase);
        }
        catch (FileNotFoundException)
        {
            return new ConcurrentDictionary<string, ManifestEntry>(StringComparer.OrdinalIgnoreCase);
        }
        catch (JsonException)
        {
            return new ConcurrentDictionary<string, ManifestEntry>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private static async Task SaveManifestAsync(string path, ConcurrentDictionary<string, ManifestEntry> manifest, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await using var stream = File.Open(path, FileMode.Create, FileAccess.Write, FileShare.None);
        await JsonSerializer.SerializeAsync(stream, manifest.Values.OrderBy(e => e.Path, StringComparer.OrdinalIgnoreCase).ToList(),
            new JsonSerializerOptions { WriteIndented = true }, cancellationToken);
    }

    private static void RemoveStaleManifestEntries(ConcurrentDictionary<string, ManifestEntry> manifest, IEnumerable<string> validPaths)
    {
        var valid = new HashSet<string>(validPaths, StringComparer.OrdinalIgnoreCase);
        foreach (var path in manifest.Keys.ToArray())
        {
            if (!valid.Contains(path))
            {
                manifest.TryRemove(path, out _);
            }
        }
    }

    private static bool MatchesPattern(string name, string pattern)
    {
        var regex = "^" + System.Text.RegularExpressions.Regex.Escape(pattern)
            .Replace("\\*", ".*")
            .Replace("\\?", ".") + "$";
        return System.Text.RegularExpressions.Regex.IsMatch(name, regex, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    }

    private sealed record FileCandidate(string Path, string DisplayId, long Length, DateTime LastWriteUtc);
}
