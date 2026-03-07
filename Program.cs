using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Text.Json;

const string Usage = """
Usage:
  ntfsShrinker shrink <device> <bytesToShrink>
  ntfsShrinker reset <device>
  ntfsShrinker current-size <device>
  ntfsShrinker original-size <device>

Examples:
  ntfsShrinker shrink /dev/sdb1 1048576
  ntfsShrinker reset /dev/sdb1
  ntfsShrinker current-size /dev/sdb1
  ntfsShrinker original-size /dev/sdb1
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

    var device = NormalizeDevicePath(args[1]);
    if (!long.TryParse(args[2], out var bytesToShrink) || bytesToShrink <= 0)
    {
        return Fail("bytesToShrink must be a positive integer.");
    }

    var currentSize = NtfsVolumeService.GetCurrentSizeBytes(device);
    var originalSize = stateStore.GetOriginalSize(device);

    if (originalSize is null)
    {
        stateStore.SetOriginalSize(device, currentSize);
        originalSize = currentSize;
    }

    if (bytesToShrink >= currentSize)
    {
        return Fail($"Cannot shrink by {bytesToShrink} bytes because current size is {currentSize} bytes.");
    }

    var targetSize = currentSize - bytesToShrink;
    NtfsVolumeService.Resize(device, targetSize);

    Console.WriteLine($"original-size={originalSize}");
    Console.WriteLine($"current-size={targetSize}");
    return 0;
}

static int Reset(string[] args, SizeStateStore stateStore)
{
    if (args.Length != 2)
    {
        return Fail("Invalid arguments for reset.");
    }

    var device = NormalizeDevicePath(args[1]);
    var originalSize = stateStore.GetOriginalSize(device);
    if (originalSize is null)
    {
        return Fail($"No original size recorded for device: {device}");
    }

    NtfsVolumeService.Resize(device, originalSize.Value);

    Console.WriteLine($"original-size={originalSize.Value}");
    Console.WriteLine($"current-size={originalSize.Value}");
    return 0;
}

static int PrintCurrentSize(string[] args)
{
    if (args.Length != 2)
    {
        return Fail("Invalid arguments for current-size.");
    }

    var device = NormalizeDevicePath(args[1]);
    var currentSize = NtfsVolumeService.GetCurrentSizeBytes(device);
    Console.WriteLine(currentSize);
    return 0;
}

static int PrintOriginalSize(string[] args, SizeStateStore stateStore)
{
    if (args.Length != 2)
    {
        return Fail("Invalid arguments for original-size.");
    }

    var device = NormalizeDevicePath(args[1]);
    var originalSize = stateStore.GetOriginalSize(device);
    if (originalSize is null)
    {
        return Fail($"No original size recorded for device: {device}");
    }

    Console.WriteLine(originalSize.Value);
    return 0;
}

static string NormalizeDevicePath(string rawDevice)
{
    if (string.IsNullOrWhiteSpace(rawDevice))
    {
        throw new ArgumentException("Device path cannot be empty.");
    }

    return rawDevice.Trim();
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
    public static long GetCurrentSizeBytes(string device)
    {
        var output = RunProcess("ntfsresize", "--info", "--force", device);
        var match = Regex.Match(
            output,
            @"Current volume size:\s*(\d+)\s+bytes",
            RegexOptions.IgnoreCase | RegexOptions.Multiline);

        if (!match.Success || !long.TryParse(match.Groups[1].Value, out var bytes) || bytes <= 0)
        {
            throw new InvalidOperationException(
                $"Failed to parse NTFS current volume size from output:{Environment.NewLine}{output}");
        }

        return bytes;
    }

    public static void Resize(string device, long targetSizeBytes)
    {
        if (targetSizeBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(targetSizeBytes), "Target size must be positive.");
        }

        var output = RunProcess(
            "ntfsresize",
            "--force",
            "--size",
            targetSizeBytes.ToString(CultureInfo.InvariantCulture),
            device);
        if (!string.IsNullOrWhiteSpace(output))
        {
            Console.WriteLine(output);
        }
    }

    private static string RunProcess(string fileName, params string[] arguments)
    {
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = fileName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (var arg in arguments)
        {
            process.StartInfo.ArgumentList.Add(arg);
        }

        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Failed to start command '{fileName}'. Ensure it is installed and in PATH.", ex);
        }

        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Command '{fileName} {string.Join(' ', arguments)}' failed with exit code {process.ExitCode}:{Environment.NewLine}{stderr}");
        }

        return $"{stdout}{Environment.NewLine}{stderr}".Trim();
    }
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

    public long? GetOriginalSize(string device)
    {
        lock (_sync)
        {
            var state = ReadState();
            return state.TryGetValue(device, out var size) ? size : null;
        }
    }

    public void SetOriginalSize(string device, long sizeBytes)
    {
        lock (_sync)
        {
            var state = ReadState();
            state[device] = sizeBytes;
            WriteState(state);
        }
    }

    private Dictionary<string, long> ReadState()
    {
        if (!File.Exists(_stateFilePath))
        {
            return new Dictionary<string, long>(StringComparer.Ordinal);
        }

        var rawJson = File.ReadAllText(_stateFilePath);
        if (string.IsNullOrWhiteSpace(rawJson))
        {
            return new Dictionary<string, long>(StringComparer.Ordinal);
        }

        var state = JsonSerializer.Deserialize<Dictionary<string, long>>(rawJson);
        return state ?? new Dictionary<string, long>(StringComparer.Ordinal);
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
