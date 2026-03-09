# ntfsShrinker

Command line utility for Windows to inspect and shrink NTFS volumes.

## Features

- Print total volume size before shrink
- Print available shrink space
- Shrink an NTFS volume by a specified amount (in MB)

## Requirements

- Windows OS
- Administrator/elevated terminal (required by `diskpart`)
- .NET SDK (project targets `net9.0`)

## Usage

```bash
dotnet run -- info --drive C
dotnet run -- shrink --drive C --mb 10240
```

Or after publishing/building:

```bash
ntfsShrinker info --drive C
ntfsShrinker shrink --drive C --mb 10240
```

## Commands

### 1) Show size and available shrink space

```bash
ntfsShrinker info --drive <letter>
```

Example:

```bash
ntfsShrinker info --drive C
```

Output includes:
- Total size before shrink
- Available shrink space

### 2) Shrink volume

```bash
ntfsShrinker shrink --drive <letter> --mb <amount>
```

Example:

```bash
ntfsShrinker shrink --drive C --mb 10240
```

Output includes:
- Total size before shrink
- Available shrink space
- Requested shrink amount
- Total size after shrink (on success)

## Notes

- Only NTFS volumes are supported.
- The tool validates that requested shrink amount does not exceed currently available shrink space.