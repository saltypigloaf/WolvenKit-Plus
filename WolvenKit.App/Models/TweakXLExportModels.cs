using System;
using System.Collections.Generic;
using WolvenKit.RED4.Types;
using YamlDotNet.Serialization;

namespace WolvenKit.App.Models;

public sealed record TweakXLExportOptions
{
    public string ExportRoot { get; init; } = "";

    public IReadOnlyList<TweakDBID> RecordIds { get; init; } = Array.Empty<TweakDBID>();

    public IReadOnlyList<string> RecordNames { get; init; } = Array.Empty<string>();

    public bool IncludeInlineRecords { get; init; }

    public bool OverwriteExisting { get; init; }

    public bool OverwriteExistingFiles { get; init; }

    public bool WriteReport { get; init; } = true;

    [YamlIgnore]
    public bool ShouldOverwriteExisting => OverwriteExisting || OverwriteExistingFiles;
}

public sealed record TweakXLExportProgress
{
    public int RequestedRecords { get; init; }

    public int ProcessedRecords { get; init; }

    public int ExportedRecords { get; init; }

    public int FailedRecords { get; init; }

    public int SkippedRecords { get; init; }

    public string? CurrentRecordId { get; init; }

    public string? CurrentRecordName { get; init; }

    public string? CurrentPath { get; init; }

    public double Completion =>
        RequestedRecords <= 0
            ? 0
            : Math.Clamp((double)ProcessedRecords / RequestedRecords, 0, 1);
}

public sealed record TweakXLExportReport
{
    [YamlMember(Alias = "sourceTweakDbPath")]
    public string SourceTweakDbPath { get; init; } = "";

    [YamlMember(Alias = "exportRoot")]
    public string ExportRoot { get; init; } = "";

    [YamlMember(Alias = "startedAt")]
    public DateTimeOffset StartedAt { get; init; }

    [YamlMember(Alias = "finishedAt")]
    public DateTimeOffset? FinishedAt { get; init; }

    [YamlMember(Alias = "elapsed")]
    public TimeSpan Elapsed { get; init; }

    [YamlMember(Alias = "requestedRecords")]
    public int RequestedRecords { get; init; }

    [YamlMember(Alias = "exportedRecords")]
    public int ExportedRecords { get; init; }

    [YamlMember(Alias = "failedRecords")]
    public int FailedRecords { get; init; }

    [YamlMember(Alias = "skippedRecords")]
    public int SkippedRecords { get; init; }

    [YamlMember(Alias = "canceled")]
    public bool Canceled { get; init; }

    [YamlMember(Alias = "skippedInlineRecords")]
    public int SkippedInlineRecords { get; init; }

    [YamlMember(Alias = "skippedUnresolvedRecords")]
    public int SkippedUnresolvedRecords { get; init; }

    [YamlMember(Alias = "pathCollisions")]
    public int PathCollisions { get; init; }

    [YamlMember(Alias = "existingFileSkips")]
    public int ExistingFileSkips { get; init; }

    // Distinct CLR types encountered in attempted serialization, including failed records.
    // These are observations, not a completeness audit of skipped/unvisited values.
    [YamlMember(Alias = "fallbackValueTypes")]
    public IReadOnlyList<string> FallbackValueTypes { get; init; } = Array.Empty<string>();

    [YamlMember(Alias = "unsupportedValueTypes")]
    public IReadOnlyList<string> UnsupportedValueTypes { get; init; } = Array.Empty<string>();

    [YamlMember(Alias = "skippedInlineRecordDetails")]
    public IReadOnlyList<TweakXLExportRecordReference> SkippedInlineRecordDetails { get; init; } =
        Array.Empty<TweakXLExportRecordReference>();

    [YamlMember(Alias = "skippedUnresolvedRecordDetails")]
    public IReadOnlyList<TweakXLExportRecordReference> SkippedUnresolvedRecordDetails { get; init; } =
        Array.Empty<TweakXLExportRecordReference>();

    [YamlMember(Alias = "pathCollisionDetails")]
    public IReadOnlyList<TweakXLExportRecordReference> PathCollisionDetails { get; init; } =
        Array.Empty<TweakXLExportRecordReference>();

    [YamlMember(Alias = "existingFileSkipDetails")]
    public IReadOnlyList<TweakXLExportRecordReference> ExistingFileSkipDetails { get; init; } =
        Array.Empty<TweakXLExportRecordReference>();

    [YamlMember(Alias = "failures")]
    public IReadOnlyList<TweakXLExportFailure> Failures { get; init; } = Array.Empty<TweakXLExportFailure>();
}

public sealed record TweakXLExportRecordReference
{
    [YamlMember(Alias = "recordId")]
    public string RecordId { get; init; } = "";

    [YamlMember(Alias = "recordName")]
    public string? RecordName { get; init; }

    [YamlMember(Alias = "relativePath")]
    public string? RelativePath { get; init; }

    [YamlMember(Alias = "reason")]
    public string Reason { get; init; } = "";
}

public sealed record TweakXLExportFailure
{
    [YamlMember(Alias = "recordId")]
    public string RecordId { get; init; } = "";

    [YamlMember(Alias = "recordName")]
    public string? RecordName { get; init; }

    [YamlMember(Alias = "error")]
    public string Error { get; init; } = "";
}
