# ntfsShrinker

Command-line utility for NTFS volume size management on Windows 10+.

## Features

- Shrink an NTFS volume by a specific number of bytes.
- Reset volume size to the original recorded size.
- Print current size in bytes.
- Print original recorded size in bytes.

## Requirements

- Windows 10 or newer.
- PowerShell with Storage cmdlets (`Get-Partition`, `Resize-Partition`, `Get-Volume`).
- Administrator privileges.
- .NET 8 SDK/runtime.

## Usage

```bash
dotnet run -- shrink <volume> <bytesToShrink>
dotnet run -- reset <volume>
dotnet run -- current-size <volume>
dotnet run -- original-size <volume>
```

Examples:

```bash
dotnet run -- shrink C 1048576
dotnet run -- reset C:
dotnet run -- current-size D
dotnet run -- original-size D:
```

## Notes

- The first time you run `shrink` for a volume, the current size is saved as that volume's "original size".
- Original sizes are stored in:
  - `%LOCALAPPDATA%\ntfsShrinker\state.json`.
- `<volume>` must be a drive letter such as `C` or `C:`.