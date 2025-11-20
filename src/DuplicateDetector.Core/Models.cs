using System.Text.Json.Serialization;

namespace DuplicateDetector.Core;

public enum KeepStrategy
{
    Newest,
    Oldest
}

public record CaptureImageInfo(string Path, string DisplayId, DateTime TimestampUtc);

public record ImageDimensions(int Width, int Height);

public record DuplicateGroup(string Hash, ImageDimensions Dimensions, IReadOnlyList<CaptureImageInfo> Files);

public record DuplicateStats(int TotalFiles, int HashedFiles, int DuplicateGroups, int DuplicateFiles, int DeletedFiles);

public class DuplicateReport
{
    public DuplicateStats Stats { get; set; } = new DuplicateStats(0, 0, 0, 0, 0);

    public List<DuplicateGroup> Groups { get; init; } = new();

    public List<string> Errors { get; init; } = new();

    public List<string> PlannedDeletions { get; init; } = new();

    public List<string> DeletedFiles { get; init; } = new();
}

public record PixelHashResult(string Hash, int Width, int Height);

public record ManifestEntry
{
    public string Path { get; init; } = string.Empty;

    public DateTime LastWriteUtc { get; init; }

    public string Hash { get; init; } = string.Empty;

    public int Width { get; init; }

    public int Height { get; init; }
}

public class DuplicateScannerOptions
{
    public string RootPath { get; init; } = string.Empty;

    public string DisplayPattern { get; init; } = "Display_*";

    public IReadOnlyList<string> Extensions { get; init; } = new[] { ".png" };

    public bool UseManifest { get; init; }

    public string? ManifestPath { get; init; }

    public int? MaxDegreeOfParallelism { get; init; }

    public bool DeleteDuplicates { get; init; }

    public KeepStrategy KeepStrategy { get; init; } = KeepStrategy.Newest;

    public bool DryRun { get; init; }
}
