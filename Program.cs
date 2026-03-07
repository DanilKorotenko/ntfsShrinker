using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

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
    public static long GetCurrentSizeBytes(string volume)
    {
        EnsureWindowsPlatform();
        EnsureNtfs(volume);

        var output = RunPowerShell(
            $"$p = Get-Partition -DriveLetter '{volume}' -ErrorAction Stop; [Console]::Out.Write($p.Size)");
        if (!long.TryParse(output, out var bytes) || bytes <= 0)
        {
            throw new InvalidOperationException(
                $"Failed to parse current size for volume {volume}. Output: {output}");
        }

        return bytes;
    }

    public static void Resize(string volume, long targetSizeBytes)
    {
        if (targetSizeBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(targetSizeBytes), "Target size must be positive.");
        }

        EnsureWindowsPlatform();
        EnsureNtfs(volume);
        RunPowerShell(
            $"Resize-Partition -DriveLetter '{volume}' -Size {targetSizeBytes.ToString(CultureInfo.InvariantCulture)} -ErrorAction Stop | Out-Null");
    }

    private static void EnsureWindowsPlatform()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "This utility is intended to run on Windows 10+.");
        }
    }

    private static void EnsureNtfs(string volume)
    {
        var fs = RunPowerShell(
            $"$v = Get-Volume -DriveLetter '{volume}' -ErrorAction Stop; [Console]::Out.Write($v.FileSystem)");

        if (!string.Equals(fs, "NTFS", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Volume {volume} is not NTFS (detected: {fs}).");
        }
    }

    private static string RunPowerShell(string script)
    {
        return RunProcess(
            "powershell.exe",
            "-NoProfile",
            "-NonInteractive",
            "-ExecutionPolicy",
            "Bypass",
            "-Command",
            script);
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

            return stdout.Trim();
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
