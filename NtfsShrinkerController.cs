using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace ntfsShrinker;

public class NtfsShrinkerController
{
    public class Info
    {
        public long CurrentSize { get; set; }
        public long AvaliableSizeMb { get; set; }
        public override string ToString()
        {
            return $"CurrentSize: {CurrentSize} AvaliableSizeMb: {AvaliableSizeMb}";
        }
    }

    public static Info GetInfo(char aVolumeLetter)
    {
        Info info = new Info();

        var drive = GetDriveInfo(aVolumeLetter);
        var totalSizeBytes = drive.TotalSize;
        var availableShrinkMb = QueryAvailableShrinkMb(aVolumeLetter);

        info.CurrentSize = totalSizeBytes;
        info.AvaliableSizeMb = availableShrinkMb;

        return info;
    }

    public static void ShrinkVolume(char driveLetter, long requestedMb)
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

    private static string ExtractDigits(string value) =>
        new(value.Where(char.IsDigit).ToArray());

}
