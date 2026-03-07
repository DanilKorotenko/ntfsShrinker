# ntfsShrinker

Command-line utility for NTFS volume size management.

## Features

- Shrink an NTFS volume by a specific number of bytes.
- Reset volume size to the original recorded size.
- Print current size in bytes.
- Print original recorded size in bytes.

## Requirements

- Linux with `ntfsresize` installed and available in `PATH`.
- Permissions required to resize block devices (typically root).

## Usage

```bash
dotnet run -- shrink <device> <bytesToShrink>
dotnet run -- reset <device>
dotnet run -- current-size <device>
dotnet run -- original-size <device>
```

Examples:

```bash
dotnet run -- shrink /dev/sdb1 1048576
dotnet run -- reset /dev/sdb1
dotnet run -- current-size /dev/sdb1
dotnet run -- original-size /dev/sdb1
```

## Notes

- The first time you run `shrink` for a device, the current size is saved as that device's "original size".
- Original sizes are stored in:
  - `~/.local/share/ntfsShrinker/state.json` (via `LocalApplicationData` on Linux).