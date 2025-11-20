using System.Diagnostics;
using DuplicateDetector.Core;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace DuplicateDetector.Tests;

public class DuplicateScannerTests
{
    [Fact]
    public async Task PixelHasher_IsDeterministic()
    {
        using var temp = new TempDir();
        var img1 = CreateImage(Path.Combine(temp.Path, "Display_1"), "a.png",
            new Rgba32(255, 0, 0, 255), new Rgba32(0, 255, 0, 255), new Rgba32(0, 0, 255, 255), new Rgba32(255, 255, 255, 255));
        var img2 = CreateImage(Path.Combine(temp.Path, "Display_1"), "b.png",
            new Rgba32(255, 0, 0, 255), new Rgba32(0, 255, 0, 255), new Rgba32(0, 0, 255, 255), new Rgba32(255, 255, 255, 255));
        var img3 = CreateImage(Path.Combine(temp.Path, "Display_1"), "c.png",
            new Rgba32(0, 0, 0, 255), new Rgba32(0, 0, 0, 255), new Rgba32(0, 0, 0, 255), new Rgba32(0, 0, 0, 255));

        var hasher = new PixelHasher();
        var hash1 = await hasher.HashAsync(img1);
        var hash2 = await hasher.HashAsync(img2);
        var hash3 = await hasher.HashAsync(img3);

        Assert.Equal(hash1.Hash, hash2.Hash);
        Assert.NotEqual(hash1.Hash, hash3.Hash);
    }

    [Fact]
    public async Task Scanner_GroupsDuplicates()
    {
        using var temp = new TempDir();
        var display1 = Path.Combine(temp.Path, "Display_1");
        var display2 = Path.Combine(temp.Path, "Display_2");

        var img1 = CreateImage(display1, "a.png", Red(), Red(), Red(), Red());
        var img2 = CreateImage(display2, "b.png", Red(), Red(), Red(), Red());
        _ = CreateImage(display1, "unique.png", Blue(), Blue(), Blue(), Blue());

        var scanner = new DuplicateScanner();
        var report = await scanner.ScanAsync(new DuplicateScannerOptions { RootPath = temp.Path });

        Assert.Single(report.Groups);
        Assert.Equal(2, report.Groups[0].Files.Count);
        Assert.Empty(report.Errors);
    }

    [Fact]
    public async Task Scanner_ReusesManifestWhenTimestampUnchanged()
    {
        using var temp = new TempDir();
        var display = Path.Combine(temp.Path, "Display_1");
        var img1 = CreateImage(display, "a.png", Red(), Red(), Red(), Red());
        var img2 = CreateImage(display, "b.png", Red(), Red(), Red(), Red());
        var manifest = Path.Combine(temp.Path, "manifest.json");

        var firstScanner = new DuplicateScanner();
        await firstScanner.ScanAsync(new DuplicateScannerOptions
        {
            RootPath = temp.Path,
            UseManifest = true,
            ManifestPath = manifest
        });

        var countingHasher = new CountingHasher();
        var secondScanner = new DuplicateScanner(countingHasher);
        await secondScanner.ScanAsync(new DuplicateScannerOptions
        {
            RootPath = temp.Path,
            UseManifest = true,
            ManifestPath = manifest
        });

        Assert.Equal(0, countingHasher.HashCalls);
    }

    [Fact]
    public async Task Scanner_DryRunPlansDeletionsOnly()
    {
        using var temp = new TempDir();
        var display = Path.Combine(temp.Path, "Display_1");
        var older = CreateImage(display, "old.png", Red(), Red(), Red(), Red());
        var newer = CreateImage(display, "new.png", Red(), Red(), Red(), Red());

        File.SetLastWriteTimeUtc(older, DateTime.UtcNow.AddMinutes(-10));
        File.SetLastWriteTimeUtc(newer, DateTime.UtcNow);

        var scanner = new DuplicateScanner();
        var report = await scanner.ScanAsync(new DuplicateScannerOptions
        {
            RootPath = temp.Path,
            DeleteDuplicates = true,
            DryRun = true,
            KeepStrategy = KeepStrategy.Newest
        });

        Assert.Single(report.PlannedDeletions);
        Assert.True(File.Exists(older));
        Assert.True(File.Exists(newer));
    }

    [Fact]
    public async Task Scanner_DeletesDuplicatesUsingKeepStrategy()
    {
        using var temp = new TempDir();
        var display = Path.Combine(temp.Path, "Display_1");
        var older = CreateImage(display, "old.png", Red(), Red(), Red(), Red());
        var newer = CreateImage(display, "new.png", Red(), Red(), Red(), Red());

        File.SetLastWriteTimeUtc(older, DateTime.UtcNow.AddMinutes(-10));
        File.SetLastWriteTimeUtc(newer, DateTime.UtcNow);

        var scanner = new DuplicateScanner();
        var report = await scanner.ScanAsync(new DuplicateScannerOptions
        {
            RootPath = temp.Path,
            DeleteDuplicates = true,
            KeepStrategy = KeepStrategy.Newest
        });

        Assert.Single(report.DeletedFiles);
        Assert.False(File.Exists(older));
        Assert.True(File.Exists(newer));
    }

    [Fact]
    public async Task Cli_RunsAndOutputsJson()
    {
        using var temp = new TempDir();
        var display = Path.Combine(temp.Path, "Display_1");
        _ = CreateImage(display, "a.png", Red(), Red(), Red(), Red());
        _ = CreateImage(display, "b.png", Red(), Red(), Red(), Red());

        var repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
        var cliProject = Path.Combine(repoRoot, "src", "DuplicateDetector.Cli", "DuplicateDetector.Cli.csproj");

        var psi = new ProcessStartInfo("dotnet", $"run --project \"{cliProject}\" -- dedupe scan --root \"{temp.Path}\" --json")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = repoRoot
        };

        using var process = Process.Start(psi);
        Assert.NotNull(process);
        var output = await process.StandardOutput.ReadToEndAsync();
        var error = await process.StandardError.ReadToEndAsync();
        process.WaitForExit(30000);

        Assert.True(process.HasExited);
        Assert.Equal(0, process.ExitCode);
        Assert.Contains("DuplicateGroups", output);
        Assert.True(string.IsNullOrWhiteSpace(error), $"stderr: {error}");
    }

    private static string CreateImage(string displayPath, string fileName, params Rgba32[] pixels)
    {
        Directory.CreateDirectory(displayPath);
        if (pixels.Length != 4)
        {
            throw new ArgumentException("Exactly 4 pixels required for 2x2 test image.");
        }

        var path = Path.Combine(displayPath, fileName);
        using var image = new Image<Rgba32>(2, 2);
        var index = 0;
        for (int y = 0; y < 2; y++)
        {
            for (int x = 0; x < 2; x++)
            {
                image[x, y] = pixels[index++];
            }
        }

        image.SaveAsPng(path);
        return path;
    }

    private static Rgba32 Red() => new Rgba32(200, 0, 0, 255);
    private static Rgba32 Blue() => new Rgba32(0, 0, 200, 255);

    private sealed class CountingHasher : PixelHasher
    {
        private int _hashCalls;
        public int HashCalls => _hashCalls;

        public override async Task<PixelHashResult> HashAsync(string path, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _hashCalls);
            return await base.HashAsync(path, cancellationToken);
        }
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), System.IO.Path.GetRandomFileName());

        public TempDir()
        {
            Directory.CreateDirectory(Path);
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch
            {
                // ignore cleanup failures in tests
            }
        }
    }
}
