using System;
using System.Collections.Generic;
using System.IO;

namespace WolvenKit.App.Helpers;

public static class TweakXLExportPathHelper
{
    public const string YamlExtension = ".yaml";
    public const string ReportFileName = "export_report.yaml";

    private static readonly HashSet<string> s_reservedWindowsNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON",
        "PRN",
        "AUX",
        "NUL",
        "COM1",
        "COM2",
        "COM3",
        "COM4",
        "COM5",
        "COM6",
        "COM7",
        "COM8",
        "COM9",
        "LPT1",
        "LPT2",
        "LPT3",
        "LPT4",
        "LPT5",
        "LPT6",
        "LPT7",
        "LPT8",
        "LPT9"
    };

    private static readonly HashSet<char> s_invalidFileNameChars = new(Path.GetInvalidFileNameChars())
    {
        Path.DirectorySeparatorChar,
        Path.AltDirectorySeparatorChar,
        Path.PathSeparator,
        Path.VolumeSeparatorChar
    };

    public static string GetRelativePath(string recordName) => GetRelativePathParts(recordName).ToRelativePath();

    public static IReadOnlyList<TweakXLExportPathResult> GetRelativePaths(IEnumerable<TweakXLExportPathRecord> records)
    {
        ArgumentNullException.ThrowIfNull(records);

        var usedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var results = new List<TweakXLExportPathResult>();

        foreach (var record in records)
        {
            var pathParts = GetRelativePathParts(record.RecordName);
            var relativePath = pathParts.ToRelativePath();
            var hadCollision = false;

            if (!usedPaths.Add(relativePath))
            {
                hadCollision = true;
                var suffix = GetCollisionSuffix(record);
                var attempt = 0;

                do
                {
                    var attemptSuffix = attempt == 0 ? suffix : $"{suffix}_{attempt + 1}";
                    relativePath = pathParts.ToRelativePath(attemptSuffix);
                    attempt++;
                } while (!usedPaths.Add(relativePath));
            }

            results.Add(new TweakXLExportPathResult(record.RecordName, record.RecordId, relativePath, hadCollision));
        }

        return results;
    }

    public static string GetAbsolutePath(string exportRoot, string relativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(exportRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);

        var rootPath = Path.GetFullPath(exportRoot);
        var fullPath = Path.GetFullPath(Path.Combine(rootPath, relativePath));
        var rootPathWithSeparator = Path.EndsInDirectorySeparator(rootPath)
            ? rootPath
            : rootPath + Path.DirectorySeparatorChar;

        if (!fullPath.Equals(rootPath, StringComparison.OrdinalIgnoreCase) &&
            !fullPath.StartsWith(rootPathWithSeparator, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Export path '{relativePath}' resolves outside the export root.");
        }

        return fullPath;
    }

    public static string GetReportPath(string exportRoot) => GetAbsolutePath(exportRoot, ReportFileName);

    private static TweakXLExportPathParts GetRelativePathParts(string? recordName)
    {
        var folder = SanitizeFileNameSegment(GetFirstSegment(recordName));
        var fileStem = SanitizeRecordName(recordName);

        return new TweakXLExportPathParts(folder, fileStem);
    }

    private static string GetFirstSegment(string? recordName)
    {
        if (string.IsNullOrEmpty(recordName))
        {
            return "";
        }

        var dotIndex = recordName.IndexOf('.');
        return dotIndex >= 0 ? recordName[..dotIndex] : recordName;
    }

    private static string SanitizeRecordName(string? recordName)
    {
        var trimmedRecordName = (recordName ?? "").TrimEnd(' ', '.');
        if (trimmedRecordName.Length == 0)
        {
            return "_";
        }

        var segments = trimmedRecordName.Split('.');
        for (var i = 0; i < segments.Length; i++)
        {
            segments[i] = SanitizeFileNameSegment(segments[i]);
        }

        var fileStem = string.Join(".", segments).TrimEnd(' ', '.');
        return fileStem.Length == 0 ? "_" : fileStem;
    }

    private static string SanitizeFileNameSegment(string segment)
    {
        var sanitizedChars = new char[segment.Length];
        for (var i = 0; i < segment.Length; i++)
        {
            sanitizedChars[i] = s_invalidFileNameChars.Contains(segment[i]) ? '_' : segment[i];
        }

        var sanitized = new string(sanitizedChars).TrimEnd(' ', '.');
        return IsInvalidSegment(sanitized) ? "_" : sanitized;
    }

    private static bool IsInvalidSegment(string segment) =>
        segment.Length == 0 ||
        segment is "." or ".." ||
        s_reservedWindowsNames.Contains(segment);

    private static string GetCollisionSuffix(TweakXLExportPathRecord record) =>
        record.RecordId is { } recordId
            ? $"_0x{recordId:X16}"
            : $"_0x{GetStableHash(record.RecordName):X16}";

    private static ulong GetStableHash(string value)
    {
        const ulong offsetBasis = 14695981039346656037;
        const ulong prime = 1099511628211;

        var hash = offsetBasis;
        foreach (var character in value)
        {
            unchecked
            {
                hash ^= character;
                hash *= prime;
            }
        }

        return hash;
    }

    private readonly record struct TweakXLExportPathParts(string Folder, string FileStem)
    {
        public string ToRelativePath(string suffix = "") => Path.Combine(Folder, $"{FileStem}{suffix}{YamlExtension}");
    }
}

public readonly record struct TweakXLExportPathRecord(string RecordName, ulong? RecordId = null);

public readonly record struct TweakXLExportPathResult(
    string RecordName,
    ulong? RecordId,
    string RelativePath,
    bool HadCollision = false);
