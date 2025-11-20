# Duplicate Detector

Utility for ScreenCaptureApp to find and optionally delete exact duplicate screenshots using pixel hashing.

## Requirements
- .NET 8 SDK

## Build & Test
- Restore and run tests: `dotnet test`
- Run CLI: `dotnet run --project src/DuplicateDetector.Cli -- dedupe scan --root "<path>" [options]`

## CLI
```
dedupe scan --root <path> [--manifest <path>] [--json] [--delete-duplicates]
            [--keep newest|oldest] [--dry-run] [--max-parallel N] [--include-ext .png,.jpg]
```
- `--root <path>` (required): Root folder containing `Display_*` subfolders. Wildcards are allowed (e.g., `Display_0\*`).
- `--manifest <path>`: Enable manifest cache file for hashes (path, lastWriteUtc, dimensions, hash). Defaults inside root when set.
- `--include-ext <list>`: Comma-separated extensions to scan (default: `.png`).
- `--max-parallel <N>`: Concurrency for hashing (default: CPU count).
- `--delete-duplicates`: Delete duplicates (per display), keeping one file according to `--keep`.
- `--keep newest|oldest`: Which file to keep per display when deleting (default: `newest`).
- `--dry-run`: Show planned deletions without removing files.
- `-V | --verbose <N>`: Verbosity (0 = no output, 1 = summary counts, 2+ = full detail). Default: 1.
- `--json`: Emit the full `DuplicateReport` as JSON (otherwise prints a table).
- `-h | --help`: Show CLI help.

Example:
```
dotnet run --project src/DuplicateDetector.Cli -- dedupe scan --root "Display-Screenshots" ^
  --manifest "Display-Screenshots\\manifest.json" --include-ext .png,.jpg --max-parallel 4 --json
```

## JSON Report (excerpt)
```json
{
  "Stats": {
    "TotalFiles": 12,
    "HashedFiles": 8,
    "DuplicateGroups": 2,
    "DuplicateFiles": 5,
    "DeletedFiles": 0
  },
  "Groups": [
    {
      "Hash": "A1B2C3...",
      "Dimensions": { "Width": 1920, "Height": 1080 },
      "Files": [
        { "Path": "Display_1\\shot1.png", "DisplayId": "Display_1", "TimestampUtc": "2024-06-04T10:00:00Z" }
      ]
    }
  ],
  "Errors": [],
  "PlannedDeletions": [],
  "DeletedFiles": []
}
```

## Integration Notes
- Reference `DuplicateDetector.Core` and call `await new DuplicateScanner().ScanAsync(options)` after captures complete.
- Populate `DuplicateScannerOptions.RootPath` with the screenshots root; customize extensions or manifest path as needed.
- Honor `DuplicateReport.PlannedDeletions/DeletedFiles` in UI logs when deletion is enabled.
