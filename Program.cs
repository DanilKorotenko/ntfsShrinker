using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

const string Usage = """
Usage:
  ntfsShrinker shrink <volume> <bytesToShrink>
  ntfsShrinker reset <volume>
  ntfsShrinker current-size <volume>
  ntfsShrinker original-size <volume>

Examples:
  ntfsShrinker shrink C 1048576
  ntfsShrinker reset C:
  ntfsShrinker current-size D
  ntfsShrinker original-size D:
""";

var stateStore = new SizeStateStore();

if (args.Length == 0)
{
    Console.WriteLine(Usage);
    return 1;
}

try
{
    return args[0] switch
    {
        "shrink" => Shrink(args, stateStore),
        "reset" => Reset(args, stateStore),
        "current-size" => PrintCurrentSize(args),
        "original-size" => PrintOriginalSize(args, stateStore),
        "-h" or "--help" or "help" => PrintUsage(),
        _ => Fail($"Unknown command: {args[0]}")
    };
}
catch (Exception ex)
{
    Console.Error.WriteLine(ex.Message);
    return 1;
}

static int Shrink(string[] args, SizeStateStore stateStore)
{
    if (args.Length != 3)
    {
        return Fail("Invalid arguments for shrink.");
    }

    var volume = NormalizeVolume(args[1]);
    if (!long.TryParse(args[2], out var bytesToShrink) || bytesToShrink <= 0)
    {
        return Fail("bytesToShrink must be a positive integer.");
    }

    var currentSize = NtfsVolumeService.GetCurrentSizeBytes(volume);
    var originalSize = stateStore.GetOriginalSize(volume);

    if (originalSize is null)
    {
        stateStore.SetOriginalSize(volume, currentSize);
        originalSize = currentSize;
    }

    if (bytesToShrink >= currentSize)
    {
        return Fail($"Cannot shrink by {bytesToShrink} bytes because current size is {currentSize} bytes.");
    }

    var targetSize = currentSize - bytesToShrink;
    NtfsVolumeService.Resize(volume, targetSize);
    var resizedSize = NtfsVolumeService.GetCurrentSizeBytes(volume);

    Console.WriteLine($"original-size={originalSize}");
    Console.WriteLine($"current-size={resizedSize}");
    return 0;
}

static int Reset(string[] args, SizeStateStore stateStore)
{
    if (args.Length != 2)
    {
        return Fail("Invalid arguments for reset.");
    }

    var volume = NormalizeVolume(args[1]);
    var originalSize = stateStore.GetOriginalSize(volume);
    if (originalSize is null)
    {
        return Fail($"No original size recorded for volume: {volume}");
    }

    NtfsVolumeService.Resize(volume, originalSize.Value);
    var resizedSize = NtfsVolumeService.GetCurrentSizeBytes(volume);

    Console.WriteLine($"original-size={originalSize.Value}");
    Console.WriteLine($"current-size={resizedSize}");
    return 0;
}

static int PrintCurrentSize(string[] args)
{
    if (args.Length != 2)
    {
        return Fail("Invalid arguments for current-size.");
    }

    var volume = NormalizeVolume(args[1]);
    var currentSize = NtfsVolumeService.GetCurrentSizeBytes(volume);
    Console.WriteLine(currentSize);
    return 0;
}

static int PrintOriginalSize(string[] args, SizeStateStore stateStore)
{
    if (args.Length != 2)
    {
        return Fail("Invalid arguments for original-size.");
    }

    var volume = NormalizeVolume(args[1]);
    var originalSize = stateStore.GetOriginalSize(volume);
    if (originalSize is null)
    {
        return Fail($"No original size recorded for volume: {volume}");
    }

    Console.WriteLine(originalSize.Value);
    return 0;
}

static string NormalizeVolume(string rawVolume)
{
    if (string.IsNullOrWhiteSpace(rawVolume))
    {
        throw new ArgumentException("Volume cannot be empty.");
    }

    var value = rawVolume.Trim().TrimEnd('\\', '/');
    if (value.Length == 1 && char.IsLetter(value[0]))
    {
        return char.ToUpperInvariant(value[0]).ToString();
    }

    if (value.Length == 2 && char.IsLetter(value[0]) && value[1] == ':')
    {
        return char.ToUpperInvariant(value[0]).ToString();
    }

    throw new ArgumentException("Volume must be a drive letter like C or C:.");
}

static int PrintUsage()
{
    Console.WriteLine(Usage);
    return 0;
}

static int Fail(string message)
{
    Console.Error.WriteLine(message);
    Console.Error.WriteLine(Usage);
    return 1;
}

internal sealed class NtfsVolumeService
{
    private const long BytesPerMiB = 1024 * 1024;
    private static readonly Regex DiskPartErrorRegex = new(
        "(?im)(DiskPart has encountered an error|Virtual Disk Service error|The arguments specified for this command are not valid|There is no volume selected)",
        RegexOptions.Compiled);

    public static long GetCurrentSizeBytes(string volume)
    {
        EnsureWindowsPlatform();
        var info = GetVolumeInfo(volume);
        if (!string.Equals(info.FileSystem, "NTFS", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Volume {volume} is not NTFS (detected: {info.FileSystem}).");
        }

        return info.SizeBytes;
    }

    public static void Resize(string volume, long targetSizeBytes)
    {
        if (targetSizeBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(targetSizeBytes), "Target size must be positive.");
        }

        EnsureWindowsPlatform();
        var info = GetVolumeInfo(volume);
        if (!string.Equals(info.FileSystem, "NTFS", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Volume {volume} is not NTFS (detected: {info.FileSystem}).");
        }

        if (targetSizeBytes == info.SizeBytes)
        {
            return;
        }

        if (targetSizeBytes < info.SizeBytes)
        {
            var shrinkBytes = info.SizeBytes - targetSizeBytes;
            var shrinkMiB = ToDiskPartMiB(shrinkBytes);
            RunDiskPart(
                $"select volume {volume}",
                $"shrink desired={shrinkMiB.ToString(CultureInfo.InvariantCulture)}");
        }
        else
        {
            var extendBytes = targetSizeBytes - info.SizeBytes;
            var extendMiB = ToDiskPartMiB(extendBytes);
            RunDiskPart(
                $"select volume {volume}",
                $"extend size={extendMiB.ToString(CultureInfo.InvariantCulture)}");
        }
    }

    private static void EnsureWindowsPlatform()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "This utility is intended to run on Windows 10+.");
        }
    }

    private static long ToDiskPartMiB(long bytes)
    {
        if (bytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(bytes), "Resize amount must be positive.");
        }

        if (bytes % BytesPerMiB != 0)
        {
            throw new InvalidOperationException(
                $"DiskPart resize granularity is 1 MiB. Requested amount {bytes} bytes must be divisible by {BytesPerMiB}.");
        }

        return bytes / BytesPerMiB;
    }

    private static VolumeInfo GetVolumeInfo(string volume)
    {
        var output = RunDiskPart(
            "list volume",
            $"select volume {volume}",
            "detail volume");

        var fileSystem = ParseFileSystem(output, volume);
        var sizeBytes = ParseSizeBytes(output, volume);
        return new VolumeInfo(fileSystem, sizeBytes);
    }

    private static string ParseFileSystem(string output, string volume)
    {
        var detailMatch = Regex.Match(
            output,
            @"(?im)^\s*File System\s*:\s*(?<fs>\S+)\s*$");
        if (detailMatch.Success)
        {
            return detailMatch.Groups["fs"].Value.Trim();
        }

        var listLineMatch = Regex.Match(
            output,
            $@"(?im)^\s*Volume\s+\d+\s+{Regex.Escape(volume)}\s+.*?\s+(?<fs>[A-Za-z0-9]+)\s+\S+\s+[\d\.,]+\s*(B|KB|MB|GB|TB)\b");
        if (listLineMatch.Success)
        {
            return listLineMatch.Groups["fs"].Value.Trim();
        }

        throw new InvalidOperationException(
            $"Failed to determine filesystem for volume {volume} from DiskPart output:{Environment.NewLine}{output}");
    }

    private static long ParseSizeBytes(string output, string volume)
    {
        var detailCapacityMatch = Regex.Match(
            output,
            @"(?im)^\s*Volume Capacity\s*:\s*(?<size>[\d\.,]+)\s*(?<unit>B|KB|MB|GB|TB)\b");
        if (detailCapacityMatch.Success)
        {
            return ConvertToBytes(detailCapacityMatch.Groups["size"].Value, detailCapacityMatch.Groups["unit"].Value);
        }

        var detailSizeMatch = Regex.Match(
            output,
            @"(?im)^\s*Size\s*:\s*(?<size>[\d\.,]+)\s*(?<unit>B|KB|MB|GB|TB)\b");
        if (detailSizeMatch.Success)
        {
            return ConvertToBytes(detailSizeMatch.Groups["size"].Value, detailSizeMatch.Groups["unit"].Value);
        }

        var listLineMatch = Regex.Match(
            output,
            $@"(?im)^\s*Volume\s+\d+\s+{Regex.Escape(volume)}\s+.*?\s+(?<fs>[A-Za-z0-9]+)\s+\S+\s+(?<size>[\d\.,]+)\s*(?<unit>B|KB|MB|GB|TB)\b");
        if (listLineMatch.Success)
        {
            return ConvertToBytes(listLineMatch.Groups["size"].Value, listLineMatch.Groups["unit"].Value);
        }

        throw new InvalidOperationException(
            $"Failed to determine size for volume {volume} from DiskPart output:{Environment.NewLine}{output}");
    }

    private static long ConvertToBytes(string rawSize, string rawUnit)
    {
        var normalized = rawSize.Trim().Replace(" ", string.Empty);

        if (normalized.Contains(',') && normalized.Contains('.'))
        {
            normalized = normalized.Replace(",", string.Empty);
        }
        else if (normalized.Contains(','))
        {
            normalized = normalized.Replace(',', '.');
        }

        if (!decimal.TryParse(
                normalized,
                NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture,
                out var number) || number <= 0)
        {
            throw new InvalidOperationException($"Failed to parse DiskPart size number: {rawSize}");
        }

        long multiplier = rawUnit.ToUpperInvariant() switch
        {
            "B" => 1L,
            "KB" => 1024L,
            "MB" => 1024L * 1024L,
            "GB" => 1024L * 1024L * 1024L,
            "TB" => 1024L * 1024L * 1024L * 1024L,
            _ => throw new InvalidOperationException($"Unsupported DiskPart unit: {rawUnit}")
        };

        return checked((long)Math.Round(number * multiplier, MidpointRounding.AwayFromZero));
    }

    private static string RunDiskPart(params string[] commands)
    {
        var scriptPath = Path.Combine(Path.GetTempPath(), $"ntfsShrinker-{Guid.NewGuid():N}.txt");
        File.WriteAllLines(scriptPath, commands);

        try
        {
            return RunProcess("diskpart.exe", "/s", scriptPath);
        }
        finally
        {
            try
            {
                if (File.Exists(scriptPath))
                {
                    File.Delete(scriptPath);
                }
            }
            catch
            {
                // best effort cleanup
            }
        }
    }

    private static string RunProcess(string fileName, params string[] arguments)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };
        foreach (var arg in arguments)
        {
            process.StartInfo.ArgumentList.Add(arg);
        }

        try
        {
            process.Start();
            var stdout = process.StandardOutput.ReadToEnd();
            var stderr = process.StandardError.ReadToEnd();
            process.WaitForExit();

            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"Command '{fileName} {string.Join(' ', arguments)}' failed with exit code {process.ExitCode}:{Environment.NewLine}{stderr}");
            }

            var output = $"{stdout}{Environment.NewLine}{stderr}".Trim();
            if (DiskPartErrorRegex.IsMatch(output))
            {
                throw new InvalidOperationException(
                    $"Command '{fileName} {string.Join(' ', arguments)}' reported an error:{Environment.NewLine}{output}");
            }

            return output;
        }
        catch (InvalidOperationException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Failed to run command '{fileName}'. Ensure required Windows tools are available.", ex);
        }
    }

    private readonly record struct VolumeInfo(string FileSystem, long SizeBytes);
}

internal sealed class SizeStateStore
{
    private readonly string _stateFilePath;
    private readonly object _sync = new();

    public SizeStateStore()
    {
        var stateDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ntfsShrinker");

        Directory.CreateDirectory(stateDirectory);
        _stateFilePath = Path.Combine(stateDirectory, "state.json");
    }

    public long? GetOriginalSize(string volume)
    {
        lock (_sync)
        {
            var state = ReadState();
            return state.TryGetValue(volume, out var size) ? size : null;
        }
    }

    public void SetOriginalSize(string volume, long sizeBytes)
    {
        lock (_sync)
        {
            var state = ReadState();
            state[volume] = sizeBytes;
            WriteState(state);
        }
    }

    private Dictionary<string, long> ReadState()
    {
        if (!File.Exists(_stateFilePath))
        {
            return new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        }

        var rawJson = File.ReadAllText(_stateFilePath);
        if (string.IsNullOrWhiteSpace(rawJson))
        {
            return new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        }

        var state = JsonSerializer.Deserialize<Dictionary<string, long>>(rawJson);
        return state is null
            ? new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, long>(state, StringComparer.OrdinalIgnoreCase);
    }

    private void WriteState(Dictionary<string, long> state)
    {
        var json = JsonSerializer.Serialize(state, new JsonSerializerOptions
        {
            WriteIndented = true
        });

        File.WriteAllText(_stateFilePath, json);
    }
}
