using System.Text.Json;
using DuplicateDetector.Core;

namespace DuplicateDetector.Cli;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args.Length == 0 || args.Contains("--help", StringComparer.OrdinalIgnoreCase) || args.Contains("-h"))
        {
            PrintUsage();
            return args.Length == 0 ? 1 : 0;
        }

        if (!args[0].Equals("dedupe", StringComparison.OrdinalIgnoreCase) ||
            args.Length < 2 ||
            !args[1].Equals("scan", StringComparison.OrdinalIgnoreCase))
        {
            Console.Error.WriteLine("Unknown command.");
            PrintUsage();
            return 1;
        }

        ParsedOptions parsed;
        try
        {
            parsed = ParseOptions(args.Skip(2).ToArray());
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            PrintUsage();
            return 1;
        }

        if (!parsed.IsValid)
        {
            Console.Error.WriteLine(parsed.Error);
            PrintUsage();
            return 1;
        }

        var options = new DuplicateScannerOptions
        {
            RootPath = NormalizeRoot(parsed.Root!),
            Extensions = parsed.Extensions.Any() ? parsed.Extensions : new[] { ".png" },
            UseManifest = parsed.ManifestPath != null,
            ManifestPath = parsed.ManifestPath,
            MaxDegreeOfParallelism = parsed.MaxParallel,
            DeleteDuplicates = parsed.DeleteDuplicates,
            KeepStrategy = parsed.KeepStrategy,
            DryRun = parsed.DryRun
        };

        var scanner = new DuplicateScanner();
        var report = await scanner.ScanAsync(options);

        if (parsed.OutputJson)
        {
            var json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
            Console.WriteLine(json);
        }
        else
        {
            PrintReport(report, options, parsed.Verbosity);
        }

        return report.Errors.Count == 0 ? 0 : 2;
    }

    private static void PrintReport(DuplicateReport report, DuplicateScannerOptions options, int verbosity)
    {
        if (verbosity <= 0)
        {
            if (report.Errors.Count > 0)
            {
                Console.WriteLine("Errors:");
                foreach (var error in report.Errors)
                {
                    Console.WriteLine($"  {error}");
                }
            }
            return;
        }

        Console.WriteLine($"Scanned files: {report.Stats.TotalFiles}");
        Console.WriteLine($"Hashed files: {report.Stats.HashedFiles}");
        Console.WriteLine($"Duplicate groups: {report.Stats.DuplicateGroups}");
        if (options.DeleteDuplicates)
        {
            var list = options.DryRun ? report.PlannedDeletions : report.DeletedFiles;
            Console.WriteLine(options.DryRun
                ? $"Planned deletions: {list.Count}"
                : $"Deletions executed: {list.Count}");
        }
        Console.WriteLine();

        if (verbosity == 1)
        {
            if (report.Errors.Count > 0)
            {
                Console.WriteLine("Errors:");
                foreach (var error in report.Errors)
                {
                    Console.WriteLine($"  {error}");
                }
            }
            return;
        }

        if (report.Groups.Count == 0)
        {
            Console.WriteLine("No duplicates detected.");
        }
        else
        {
            foreach (var group in report.Groups)
            {
                Console.WriteLine($"Hash: {group.Hash}  Size: {group.Dimensions.Width}x{group.Dimensions.Height}  Count: {group.Files.Count}");
                foreach (var file in group.Files)
                {
                    Console.WriteLine($"  [{file.DisplayId}] {file.TimestampUtc:u}  {file.Path}");
                }
                Console.WriteLine();
            }
        }

        if (options.DeleteDuplicates)
        {
            Console.WriteLine(options.DryRun ? "Planned deletions:" : "Deleted files:");
            var list = options.DryRun ? report.PlannedDeletions : report.DeletedFiles;
            if (list.Count == 0)
            {
                Console.WriteLine("  (none)");
            }
            else
            {
                foreach (var path in list)
                {
                    Console.WriteLine($"  {path}");
                }
            }
            Console.WriteLine();
        }

        if (report.Errors.Count > 0)
        {
            Console.WriteLine("Errors:");
            foreach (var error in report.Errors)
            {
                Console.WriteLine($"  {error}");
            }
        }
    }

    private static void PrintUsage()
    {
        Console.WriteLine("Usage:");
        Console.WriteLine("  duplicate-detector dedupe scan --root <path> [options]");
        Console.WriteLine();
        Console.WriteLine("Required:");
        Console.WriteLine("  --root <path>           Root folder containing Display_* subfolders (wildcards allowed).");
        Console.WriteLine();
        Console.WriteLine("Options:");
        Console.WriteLine("  --manifest <path>       Enable manifest cache file for hashes.");
        Console.WriteLine("  --include-ext <list>    Comma-separated extensions to scan (default: .png).");
        Console.WriteLine("  --max-parallel <N>      Limit hashing concurrency (default: CPU count).");
        Console.WriteLine("  --delete-duplicates     Delete duplicates, keeping one per display.");
        Console.WriteLine("  --keep newest|oldest    Strategy for which file to keep per display (default: newest).");
        Console.WriteLine("  --dry-run               Show planned deletions without deleting.");
        Console.WriteLine("  -V | --verbose <N>      Verbosity (0=no output, 1=summary, 2=full; default 1).");
        Console.WriteLine("  --json                  Output full report as JSON.");
        Console.WriteLine("  -h | --help             Show this help.");
    }

    private static ParsedOptions ParseOptions(string[] args)
    {
        string? root = null;
        string? manifest = null;
        bool outputJson = false;
        bool deleteDuplicates = false;
        bool dryRun = false;
        int? maxParallel = null;
        var extensions = new List<string>();
        var keepStrategy = KeepStrategy.Newest;
        int verbosity = 1;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--root":
                    root = RequireValue(args, ref i);
                    break;
                case "--manifest":
                    manifest = RequireValue(args, ref i);
                    break;
                case "--json":
                    outputJson = true;
                    break;
                case "--delete-duplicates":
                    deleteDuplicates = true;
                    break;
                case "--keep":
                    var keepValue = RequireValue(args, ref i);
                    if (keepValue.Equals("newest", StringComparison.OrdinalIgnoreCase))
                    {
                        keepStrategy = KeepStrategy.Newest;
                    }
                    else if (keepValue.Equals("oldest", StringComparison.OrdinalIgnoreCase))
                    {
                        keepStrategy = KeepStrategy.Oldest;
                    }
                    else
                    {
                        return ParsedOptions.Invalid("Keep strategy must be newest or oldest.");
                    }
                    break;
                case "--dry-run":
                    dryRun = true;
                    break;
                case "--verbose":
                case "-V":
                    if (!int.TryParse(RequireValue(args, ref i), out verbosity) || verbosity < 0)
                    {
                        return ParsedOptions.Invalid("verbose must be an integer >= 0.");
                    }
                    break;
                case "--max-parallel":
                    if (!int.TryParse(RequireValue(args, ref i), out var parsedParallel) || parsedParallel <= 0)
                    {
                        return ParsedOptions.Invalid("max-parallel must be a positive integer.");
                    }
                    maxParallel = parsedParallel;
                    break;
                case "--include-ext":
                    var value = RequireValue(args, ref i);
                    extensions = value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .Select(e => e.StartsWith('.') ? e : "." + e)
                        .ToList();
                    break;
                default:
                    return ParsedOptions.Invalid($"Unknown argument: {args[i]}");
            }
        }

        if (string.IsNullOrWhiteSpace(root))
        {
            return ParsedOptions.Invalid("Root path is required.");
        }

        return ParsedOptions.Valid(root!, manifest, outputJson, deleteDuplicates, dryRun, maxParallel, extensions, keepStrategy, verbosity);
    }

    private static string RequireValue(string[] args, ref int index)
    {
        if (index + 1 >= args.Length)
        {
            throw new ArgumentException($"Missing value for {args[index]}");
        }
        index++;
        return args[index];
    }

    internal record ParsedOptions(
        bool IsValid,
        string? Root,
        string? ManifestPath,
        bool OutputJson,
        bool DeleteDuplicates,
        bool DryRun,
        int? MaxParallel,
        List<string> Extensions,
        KeepStrategy KeepStrategy,
        int Verbosity,
        string? Error)
    {
        public static ParsedOptions Invalid(string error) => new ParsedOptions(false, null, null, false, false, false, null, new List<string>(), KeepStrategy.Newest, 1, error);

        public static ParsedOptions Valid(string root, string? manifest, bool outputJson, bool deleteDuplicates, bool dryRun, int? maxParallel, List<string> extensions, KeepStrategy keep, int verbosity, string? error = null)
            => new ParsedOptions(true, root, manifest, outputJson, deleteDuplicates, dryRun, maxParallel, extensions, keep, verbosity, error);
    }

    private static string NormalizeRoot(string root)
    {
        var trimmed = root.Trim();
        if (trimmed.EndsWith("*", StringComparison.Ordinal))
        {
            trimmed = trimmed.TrimEnd('*');
        }

        trimmed = Path.TrimEndingDirectorySeparator(trimmed);
        return trimmed;
    }
}
