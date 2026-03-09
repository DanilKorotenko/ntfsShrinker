using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace ntfsShrinker;

public class NtfsShrinkerCli
{
    private const string InfoCommand = "info";
    private const string ShrinkCommand = "shrink";

    public int Run(string[] args)
    {
        if (args.Length == 0 || args.Any(IsHelpArgument))
        {
            PrintUsage();
            return 1;
        }

        if (!OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine("Error: this utility can only run on Windows.");
            return 1;
        }

        var command = args[0].Trim().ToLowerInvariant();
        IReadOnlyDictionary<string, string> options;
        try
        {
            options = ParseOptions(args.Skip(1).ToArray());
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            PrintUsage();
            return 1;
        }

        if (!options.TryGetValue("drive", out var driveArgument))
        {
            Console.Error.WriteLine("Error: missing required option --drive <letter>.");
            PrintUsage();
            return 1;
        }

        if (!TryParseDriveLetter(driveArgument, out var driveLetter))
        {
            Console.Error.WriteLine("Error: --drive must be a valid drive letter like C or C:.");
            return 1;
        }

        try
        {
            ValidateNtfsVolume(driveLetter);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            return 1;
        }

        try
        {
            return command switch
            {
                InfoCommand => RunInfo(driveLetter),
                ShrinkCommand => RunShrink(driveLetter, options),
                _ => UnknownCommand(command)
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            return 1;
        }
    }

    private static int RunInfo(char driveLetter)
    {
        var drive = GetDriveInfo(driveLetter);
        var totalSizeBytes = drive.TotalSize;
        var availableShrinkMb = QueryAvailableShrinkMb(driveLetter);

        Console.WriteLine($"Volume: {driveLetter}:");
        Console.WriteLine($"Total size before shrink: {FormatBytes(totalSizeBytes)} ({totalSizeBytes:N0} bytes)");
        Console.WriteLine($"Available shrink space: {availableShrinkMb:N0} MB ({FormatBytes(MbToBytes(availableShrinkMb))})");
        return 0;
    }

    private static int RunShrink(char driveLetter, IReadOnlyDictionary<string, string> options)
    {
        if (!options.TryGetValue("mb", out var mbText) ||
            !long.TryParse(mbText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var requestedMb) ||
            requestedMb <= 0)
        {
            Console.Error.WriteLine("Error: missing or invalid --mb <amount>. Use a positive integer amount in MB.");
            return 1;
        }

        var driveBefore = GetDriveInfo(driveLetter);
        var totalBefore = driveBefore.TotalSize;
        var availableShrinkMb = QueryAvailableShrinkMb(driveLetter);

        Console.WriteLine($"Volume: {driveLetter}:");
        Console.WriteLine($"Total size before shrink: {FormatBytes(totalBefore)} ({totalBefore:N0} bytes)");
        Console.WriteLine($"Available shrink space: {availableShrinkMb:N0} MB ({FormatBytes(MbToBytes(availableShrinkMb))})");
        Console.WriteLine($"Requested shrink amount: {requestedMb:N0} MB");

        if (requestedMb > availableShrinkMb)
        {
            Console.Error.WriteLine($"Error: requested amount exceeds available shrink space ({availableShrinkMb:N0} MB).");
            return 1;
        }

        ShrinkVolume(driveLetter, requestedMb);

        var driveAfter = GetDriveInfo(driveLetter);
        var totalAfter = driveAfter.TotalSize;
        Console.WriteLine("Shrink operation completed successfully.");
        Console.WriteLine($"Total size after shrink: {FormatBytes(totalAfter)} ({totalAfter:N0} bytes)");
        return 0;
    }

    private static int UnknownCommand(string command)
    {
        Console.Error.WriteLine($"Error: unknown command '{command}'.");
        PrintUsage();
        return 1;
    }

    private static void ValidateNtfsVolume(char driveLetter)
    {
        var drive = GetDriveInfo(driveLetter);

        if (!drive.IsReady)
        {
            throw new InvalidOperationException($"drive {driveLetter}: is not ready.");
        }

        if (!string.Equals(drive.DriveFormat, "NTFS", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"drive {driveLetter}: is '{drive.DriveFormat}', expected NTFS.");
        }
    }

    private static DriveInfo GetDriveInfo(char driveLetter)
    {
        var driveRoot = $"{char.ToUpperInvariant(driveLetter)}:\\";
        var drive = new DriveInfo(driveRoot);
        if (!drive.IsReady)
        {
            throw new InvalidOperationException($"drive {driveLetter}: is not accessible.");
        }

        return drive;
    }

    private static long QueryAvailableShrinkMb(char driveLetter)
    {
        var output = RunDiskPart(
            $"""
            select volume {driveLetter}
            shrink querymax
            """);

        var mb = ParseQueryMaxMb(output);
        if (mb is null)
        {
            throw new InvalidOperationException(
                "Unable to parse available shrink space from diskpart output. " +
                "Ensure you run this utility from an elevated terminal.");
        }

        return mb.Value;
    }

    private static void ShrinkVolume(char driveLetter, long requestedMb)
    {
        var output = RunDiskPart(
            $"""
            select volume {driveLetter}
            shrink desired={requestedMb}
            """);

        var normalized = output.ToLowerInvariant();
        if (!normalized.Contains("successfully shrunk"))
        {
            throw new InvalidOperationException(
                "DiskPart did not report a successful shrink operation. " +
                "Run from an elevated terminal and verify the requested size is valid.");
        }
    }

    private static string RunDiskPart(string scriptBody)
    {
        var scriptPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.txt");
        File.WriteAllText(scriptPath, scriptBody + Environment.NewLine + "exit" + Environment.NewLine, Encoding.ASCII);

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "diskpart.exe",
                Arguments = $"/s \"{scriptPath}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(psi)
                ?? throw new InvalidOperationException("Failed to start diskpart.");

            var stdOut = process.StandardOutput.ReadToEnd();
            var stdErr = process.StandardError.ReadToEnd();
            process.WaitForExit();

            var combinedOutput = string.Concat(stdOut, Environment.NewLine, stdErr);
            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"diskpart exited with code {process.ExitCode}.{Environment.NewLine}{combinedOutput}");
            }

            if (combinedOutput.IndexOf("error", StringComparison.OrdinalIgnoreCase) >= 0 &&
                combinedOutput.IndexOf("successfully", StringComparison.OrdinalIgnoreCase) < 0)
            {
                throw new InvalidOperationException($"diskpart reported an error:{Environment.NewLine}{combinedOutput}");
            }

            return combinedOutput;
        }
        finally
        {
            if (File.Exists(scriptPath))
            {
                File.Delete(scriptPath);
            }
        }
    }

    private static long? ParseQueryMaxMb(string output)
    {
        foreach (var line in output.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.IndexOf("maximum", StringComparison.OrdinalIgnoreCase) < 0 &&
                line.IndexOf("reclaimable", StringComparison.OrdinalIgnoreCase) < 0)
            {
                continue;
            }

            var match = Regex.Match(line, @"(?<value>\d[\d,\.]*)\s*MB", RegexOptions.IgnoreCase);
            if (!match.Success)
            {
                continue;
            }

            var numeric = ExtractDigits(match.Groups["value"].Value);
            if (long.TryParse(numeric, NumberStyles.Integer, CultureInfo.InvariantCulture, out var mb))
            {
                return mb;
            }
        }

        var fallbackMatch = Regex.Match(output, @"(?<value>\d[\d,\.]*)\s*MB", RegexOptions.IgnoreCase);
        if (fallbackMatch.Success)
        {
            var numeric = ExtractDigits(fallbackMatch.Groups["value"].Value);
            if (long.TryParse(numeric, NumberStyles.Integer, CultureInfo.InvariantCulture, out var mb))
            {
                return mb;
            }
        }

        return null;
    }

    private static long MbToBytes(long megabytes) => megabytes * 1024L * 1024L;

    private static string ExtractDigits(string value) =>
        new(value.Where(char.IsDigit).ToArray());

    private static Dictionary<string, string> ParseOptions(string[] args)
    {
        var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < args.Length; i++)
        {
            var token = args[i];
            if (!token.StartsWith("--", StringComparison.Ordinal))
            {
                throw new ArgumentException($"Unexpected argument '{token}'. Expected --option value pairs.");
            }

            var key = token[2..];
            if (i + 1 >= args.Length || args[i + 1].StartsWith("--", StringComparison.Ordinal))
            {
                throw new ArgumentException($"Missing value for option '{token}'.");
            }

            options[key] = args[++i];
        }

        return options;
    }

    private static bool TryParseDriveLetter(string input, out char driveLetter)
    {
        driveLetter = default;
        var normalized = input.Trim().TrimEnd(':');
        if (normalized.Length != 1 || !char.IsLetter(normalized[0]))
        {
            return false;
        }

        driveLetter = char.ToUpperInvariant(normalized[0]);
        return true;
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = ["bytes", "KB", "MB", "GB", "TB", "PB"];
        double value = bytes;
        var unitIndex = 0;

        while (value >= 1024 && unitIndex < units.Length - 1)
        {
            value /= 1024;
            unitIndex++;
        }

        return unitIndex == 0
            ? $"{bytes:N0} {units[unitIndex]}"
            : $"{value:N2} {units[unitIndex]}";
    }

    private static bool IsHelpArgument(string value) =>
        value is "-h" or "--help" or "/?" or "help";

    private static void PrintUsage()
    {
        Console.WriteLine(
            """
            NTFS Volume Shrinker

            Usage:
              ntfsShrinker info --drive <letter>
              ntfsShrinker shrink --drive <letter> --mb <amount>

            Examples:
              ntfsShrinker info --drive C
              ntfsShrinker shrink --drive C --mb 10240

            Notes:
              - Run from an elevated (Administrator) terminal.
              - The target volume must use NTFS.
            """);
    }
}
