using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using WolvenKit.App.Helpers;
using WolvenKit.App.Models;
using WolvenKit.Common.Services;
using WolvenKit.RED4.Types;
using YamlDotNet.Serialization;

namespace WolvenKit.App.Services;

public sealed class TweakXLBulkExportService : ITweakXLBulkExportService
{
    private static readonly Regex s_inlineRecordRegex = new("_inline[0-9]+", RegexOptions.Compiled);

    private readonly ITweakDBService _tweakDbService;
    private readonly ITweakXLRecordOverrideService _recordOverrideService;

    public TweakXLBulkExportService(
        ITweakDBService tweakDbService,
        ITweakXLRecordOverrideService recordOverrideService)
    {
        _tweakDbService = tweakDbService;
        _recordOverrideService = recordOverrideService;
    }

    public async Task<TweakXLExportReport> ExportAsync(
        TweakXLExportOptions options,
        IProgress<TweakXLExportProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.ExportRoot);

        var startedAt = DateTimeOffset.UtcNow;
        var stopwatch = Stopwatch.StartNew();
        var exportRoot = Path.GetFullPath(options.ExportRoot);
        Directory.CreateDirectory(exportRoot);

        var requestedRecordIds = GetRequestedRecordIds(options).ToList();
        var plans = CreateExportPlans(requestedRecordIds, options.IncludeInlineRecords);
        AssignRelativePaths(plans);

        var skippedInlineRecords = plans
            .Where(plan => plan.SkipReason == TweakXLExportSkipReason.InlineRecord)
            .Select(plan => CreateRecordReference(plan, "inline record"))
            .ToList();
        var skippedUnresolvedRecords = plans
            .Where(plan => plan.SkipReason == TweakXLExportSkipReason.UnresolvedRecord)
            .Select(plan => CreateRecordReference(plan, "unresolved record name"))
            .ToList();
        var pathCollisions = plans
            .Where(plan => plan.HadPathCollision)
            .Select(plan => CreateRecordReference(plan, "path collision"))
            .ToList();

        var exportedRecords = 0;
        var failedRecords = 0;
        var skippedRecords = 0;
        var existingFileSkips = 0;
        var failures = new List<TweakXLExportFailure>();
        var existingFileSkipDetails = new List<TweakXLExportRecordReference>();
        var diagnostics = new TweakXLSerializationDiagnostics();
        var canceled = false;

        ReportProgress(null);

        foreach (var plan in plans)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                canceled = true;
                break;
            }

            if (plan.SkipReason != TweakXLExportSkipReason.None)
            {
                skippedRecords++;
                ReportProgress(plan);
                continue;
            }

            try
            {
                cancellationToken.ThrowIfCancellationRequested();

                var relativePath = plan.RelativePath ?? throw new InvalidOperationException("Export path was not assigned.");
                var absolutePath = TweakXLExportPathHelper.GetAbsolutePath(exportRoot, relativePath);

                if (!options.ShouldOverwriteExisting && File.Exists(absolutePath))
                {
                    skippedRecords++;
                    existingFileSkips++;
                    existingFileSkipDetails.Add(CreateRecordReference(plan, "existing file"));
                    ReportProgress(plan);
                    continue;
                }

                if (Path.GetDirectoryName(absolutePath) is { } directory)
                {
                    Directory.CreateDirectory(directory);
                }

                var txl = _recordOverrideService.BuildRecordOverride(plan.RecordId);
                var yaml = _recordOverrideService.SerializeRecordOverride(txl, diagnostics);
                await WriteRecordYamlAsync(absolutePath, yaml, options.ShouldOverwriteExisting, cancellationToken);

                exportedRecords++;
                ReportProgress(plan);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                canceled = true;
                break;
            }
            catch (Exception ex)
            {
                failedRecords++;
                failures.Add(new TweakXLExportFailure
                {
                    RecordId = FormatRecordId(plan.RecordId),
                    RecordName = plan.RecordName,
                    Error = ex.Message
                });
                ReportProgress(plan);
                // Cleanup can wrap cancellation in an I/O error. Retain both outcomes,
                // even when this is the last plan and there is no next-loop token check.
                if (cancellationToken.IsCancellationRequested)
                {
                    canceled = true;
                    break;
                }
            }
        }

        stopwatch.Stop();

        var report = new TweakXLExportReport
        {
            SourceTweakDbPath = _tweakDbService.SourcePath ?? "",
            ExportRoot = exportRoot,
            StartedAt = startedAt,
            FinishedAt = DateTimeOffset.UtcNow,
            Elapsed = stopwatch.Elapsed,
            RequestedRecords = requestedRecordIds.Count,
            ExportedRecords = exportedRecords,
            FailedRecords = failedRecords,
            SkippedRecords = skippedRecords,
            Canceled = canceled,
            SkippedInlineRecords = skippedInlineRecords.Count,
            SkippedUnresolvedRecords = skippedUnresolvedRecords.Count,
            PathCollisions = pathCollisions.Count,
            ExistingFileSkips = existingFileSkips,
            FallbackValueTypes = diagnostics.FallbackValueTypes,
            UnsupportedValueTypes = diagnostics.UnsupportedValueTypes,
            SkippedInlineRecordDetails = skippedInlineRecords,
            SkippedUnresolvedRecordDetails = skippedUnresolvedRecords,
            PathCollisionDetails = pathCollisions,
            ExistingFileSkipDetails = existingFileSkipDetails,
            Failures = failures
        };

        if (options.WriteReport)
        {
            var reportPath = TweakXLExportPathHelper.GetReportPath(exportRoot);
            var serializer = new SerializerBuilder()
                .WithIndentedSequences()
                .Build();

            // A canceled run must still publish its report; preserve the previous report if writing fails.
            await WriteRecordYamlAsync(reportPath, serializer.Serialize(report), true, CancellationToken.None);
        }

        return report;

        void ReportProgress(ExportPlan? currentPlan)
        {
            progress?.Report(new TweakXLExportProgress
            {
                RequestedRecords = requestedRecordIds.Count,
                ProcessedRecords = exportedRecords + failedRecords + skippedRecords,
                ExportedRecords = exportedRecords,
                FailedRecords = failedRecords,
                SkippedRecords = skippedRecords,
                CurrentRecordId = currentPlan is null ? null : FormatRecordId(currentPlan.RecordId),
                CurrentRecordName = currentPlan?.RecordName,
                CurrentPath = currentPlan?.RelativePath
            });
        }
    }

    private IReadOnlyList<TweakDBID> GetRequestedRecordIds(TweakXLExportOptions options)
    {
        if (options.RecordIds.Count > 0)
        {
            return options.RecordIds;
        }

        if (options.RecordNames.Count > 0)
        {
            return options.RecordNames
                .Where(recordName => !string.IsNullOrWhiteSpace(recordName))
                .Select(recordName => (TweakDBID)recordName)
                .ToList();
        }

        return _tweakDbService.GetRecords()
            .Select(recordId => (TweakDBID)recordId)
            .ToList();
    }

    private List<ExportPlan> CreateExportPlans(IReadOnlyList<TweakDBID> requestedRecordIds, bool includeInlineRecords)
    {
        var plans = new List<ExportPlan>(requestedRecordIds.Count);

        foreach (var recordId in requestedRecordIds)
        {
            var recordName = ResolveRecordName(recordId);
            if (string.IsNullOrWhiteSpace(recordName))
            {
                plans.Add(new ExportPlan(recordId, null, TweakXLExportSkipReason.UnresolvedRecord));
                continue;
            }

            if (!includeInlineRecords && s_inlineRecordRegex.IsMatch(recordName))
            {
                plans.Add(new ExportPlan(recordId, recordName, TweakXLExportSkipReason.InlineRecord));
                continue;
            }

            plans.Add(new ExportPlan(recordId, recordName, TweakXLExportSkipReason.None));
        }

        return plans;
    }

    private static void AssignRelativePaths(List<ExportPlan> plans)
    {
        var exportablePlans = plans
            .Where(plan => plan.SkipReason == TweakXLExportSkipReason.None)
            .ToList();
        var pathResults = TweakXLExportPathHelper.GetRelativePaths(
            exportablePlans.Select(plan => new TweakXLExportPathRecord(plan.RecordName!, plan.RecordId)));

        foreach (var (plan, pathResult) in exportablePlans.Zip(pathResults))
        {
            plan.RelativePath = pathResult.RelativePath;
            plan.HadPathCollision = pathResult.HadCollision;
        }
    }

    private string? ResolveRecordName(TweakDBID recordId) =>
        _tweakDbService.GetString(recordId) ?? recordId.GetResolvedText();

    private static TweakXLExportRecordReference CreateRecordReference(ExportPlan plan, string reason) =>
        new()
        {
            RecordId = FormatRecordId(plan.RecordId),
            RecordName = plan.RecordName,
            RelativePath = plan.RelativePath,
            Reason = reason
        };

    private static string FormatRecordId(TweakDBID recordId) =>
        $"0x{((ulong)recordId).ToString("X16", CultureInfo.InvariantCulture)}";

    private static Task WriteRecordYamlAsync(
        string absolutePath,
        string yaml,
        bool overwriteExisting,
        CancellationToken cancellationToken)
        => TweakXLAtomicFileWriter.WriteAsync(
            absolutePath,
            overwriteExisting,
            async (stream, token) =>
            {
                await using var writer = new StreamWriter(stream, leaveOpen: true);
                await writer.WriteAsync(yaml.AsMemory(), token);
                await writer.FlushAsync(token);
            },
            cancellationToken);

    private sealed class ExportPlan
    {
        public ExportPlan(TweakDBID recordId, string? recordName, TweakXLExportSkipReason skipReason)
        {
            RecordId = recordId;
            RecordName = recordName;
            SkipReason = skipReason;
        }

        public TweakDBID RecordId { get; }
        public string? RecordName { get; }
        public TweakXLExportSkipReason SkipReason { get; }
        public string? RelativePath { get; set; }
        public bool HadPathCollision { get; set; }
    }

    private enum TweakXLExportSkipReason
    {
        None,
        InlineRecord,
        UnresolvedRecord
    }
}
