using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WolvenKit.App.Models;
using YamlDotNet.Serialization;

namespace WolvenKit.UnitTests;

[TestClass]
public class TweakXLExportModelTests
{
    [TestMethod]
    public void Options_Defaults_AreSafeAndPredictable()
    {
        var options = new TweakXLExportOptions();

        Assert.AreEqual("", options.ExportRoot);
        Assert.AreEqual(0, options.RecordIds.Count);
        Assert.AreEqual(0, options.RecordNames.Count);
        Assert.IsFalse(options.IncludeInlineRecords);
        Assert.IsFalse(options.OverwriteExisting);
        Assert.IsFalse(options.OverwriteExistingFiles);
        Assert.IsFalse(options.ShouldOverwriteExisting);
        Assert.IsTrue(options.WriteReport);
    }

    [TestMethod]
    public void Options_ShouldOverwriteExisting_AcceptsLegacyAndNewOptionNames()
    {
        Assert.IsTrue(new TweakXLExportOptions { OverwriteExisting = true }.ShouldOverwriteExisting);
        Assert.IsTrue(new TweakXLExportOptions { OverwriteExistingFiles = true }.ShouldOverwriteExisting);
    }

    [TestMethod]
    public void Progress_Defaults_AreSafeAndPredictable()
    {
        var progress = new TweakXLExportProgress();

        Assert.AreEqual(0, progress.RequestedRecords);
        Assert.AreEqual(0, progress.ProcessedRecords);
        Assert.AreEqual(0, progress.ExportedRecords);
        Assert.AreEqual(0, progress.FailedRecords);
        Assert.AreEqual(0, progress.SkippedRecords);
        Assert.AreEqual(0, progress.Completion);
        Assert.IsNull(progress.CurrentRecordId);
        Assert.IsNull(progress.CurrentRecordName);
        Assert.IsNull(progress.CurrentPath);
    }

    [TestMethod]
    public void Progress_Completion_ClampsToExpectedRange()
    {
        Assert.AreEqual(0.5, new TweakXLExportProgress { RequestedRecords = 10, ProcessedRecords = 5 }.Completion);
        Assert.AreEqual(1, new TweakXLExportProgress { RequestedRecords = 10, ProcessedRecords = 12 }.Completion);
        Assert.AreEqual(0, new TweakXLExportProgress { RequestedRecords = 10, ProcessedRecords = -1 }.Completion);
    }

    [TestMethod]
    public void Report_Defaults_AreSafeAndPredictable()
    {
        var report = new TweakXLExportReport();

        Assert.AreEqual("", report.SourceTweakDbPath);
        Assert.AreEqual("", report.ExportRoot);
        Assert.AreEqual(default, report.StartedAt);
        Assert.IsNull(report.FinishedAt);
        Assert.AreEqual(TimeSpan.Zero, report.Elapsed);
        Assert.AreEqual(0, report.RequestedRecords);
        Assert.AreEqual(0, report.ExportedRecords);
        Assert.AreEqual(0, report.FailedRecords);
        Assert.AreEqual(0, report.SkippedRecords);
        Assert.IsFalse(report.Canceled);
        Assert.AreEqual(0, report.SkippedInlineRecords);
        Assert.AreEqual(0, report.SkippedUnresolvedRecords);
        Assert.AreEqual(0, report.PathCollisions);
        Assert.AreEqual(0, report.ExistingFileSkips);
        Assert.AreEqual(0, report.FallbackValueTypes.Count);
        Assert.AreEqual(0, report.UnsupportedValueTypes.Count);
        Assert.AreEqual(0, report.SkippedInlineRecordDetails.Count);
        Assert.AreEqual(0, report.SkippedUnresolvedRecordDetails.Count);
        Assert.AreEqual(0, report.PathCollisionDetails.Count);
        Assert.AreEqual(0, report.ExistingFileSkipDetails.Count);
        Assert.AreEqual(0, report.Failures.Count);
    }

    [TestMethod]
    public void Report_SerializesWithExpectedYamlFieldNames()
    {
        var report = new TweakXLExportReport
        {
            SourceTweakDbPath = "source.bin",
            ExportRoot = "r6/tweaks",
            StartedAt = new DateTimeOffset(2026, 5, 2, 12, 0, 0, TimeSpan.Zero),
            FinishedAt = new DateTimeOffset(2026, 5, 2, 12, 1, 0, TimeSpan.Zero),
            Elapsed = TimeSpan.FromMinutes(1),
            RequestedRecords = 2,
            ExportedRecords = 1,
            FailedRecords = 1,
            SkippedRecords = 3,
            Canceled = true,
            SkippedInlineRecords = 1,
            SkippedUnresolvedRecords = 2,
            PathCollisions = 1,
            ExistingFileSkips = 1,
            FallbackValueTypes = ["CVariant"],
            UnsupportedValueTypes = ["UnknownRedType"],
            SkippedInlineRecordDetails =
            [
                new TweakXLExportRecordReference
                {
                    RecordId = "0x0000000000000002",
                    RecordName = "UnitTests_inline0.Record",
                    RelativePath = null,
                    Reason = "inline record"
                }
            ],
            SkippedUnresolvedRecordDetails =
            [
                new TweakXLExportRecordReference
                {
                    RecordId = "0x0000000000000003",
                    Reason = "unresolved record name"
                }
            ],
            PathCollisionDetails =
            [
                new TweakXLExportRecordReference
                {
                    RecordId = "0x0000000000000004",
                    RecordName = "unitTests.Record",
                    RelativePath = "unitTests/unitTests.Record_0x0000000000000004.yaml",
                    Reason = "path collision"
                }
            ],
            ExistingFileSkipDetails =
            [
                new TweakXLExportRecordReference
                {
                    RecordId = "0x0000000000000005",
                    RecordName = "UnitTests.Exists",
                    RelativePath = "UnitTests/UnitTests.Exists.yaml",
                    Reason = "existing file"
                }
            ],
            Failures =
            [
                new TweakXLExportFailure
                {
                    RecordId = "0x0000000000000001",
                    RecordName = "UnitTests.Record",
                    Error = "Failed"
                }
            ]
        };

        var yaml = new SerializerBuilder().Build().Serialize(report);

        StringAssert.Contains(yaml, "sourceTweakDbPath:");
        StringAssert.Contains(yaml, "exportRoot:");
        StringAssert.Contains(yaml, "startedAt:");
        StringAssert.Contains(yaml, "finishedAt:");
        StringAssert.Contains(yaml, "elapsed:");
        StringAssert.Contains(yaml, "requestedRecords:");
        StringAssert.Contains(yaml, "exportedRecords:");
        StringAssert.Contains(yaml, "failedRecords:");
        StringAssert.Contains(yaml, "skippedRecords:");
        StringAssert.Contains(yaml, "canceled:");
        StringAssert.Contains(yaml, "skippedInlineRecords:");
        StringAssert.Contains(yaml, "skippedUnresolvedRecords:");
        StringAssert.Contains(yaml, "pathCollisions:");
        StringAssert.Contains(yaml, "existingFileSkips:");
        StringAssert.Contains(yaml, "fallbackValueTypes:");
        StringAssert.Contains(yaml, "unsupportedValueTypes:");
        StringAssert.Contains(yaml, "skippedInlineRecordDetails:");
        StringAssert.Contains(yaml, "skippedUnresolvedRecordDetails:");
        StringAssert.Contains(yaml, "pathCollisionDetails:");
        StringAssert.Contains(yaml, "existingFileSkipDetails:");
        StringAssert.Contains(yaml, "failures:");
        StringAssert.Contains(yaml, "recordId:");
        StringAssert.Contains(yaml, "recordName:");
        StringAssert.Contains(yaml, "relativePath:");
        StringAssert.Contains(yaml, "reason:");
        StringAssert.Contains(yaml, "error:");
    }

    [TestMethod]
    public void Failure_Defaults_AreSafeAndPredictable()
    {
        var failure = new TweakXLExportFailure();

        Assert.AreEqual("", failure.RecordId);
        Assert.IsNull(failure.RecordName);
        Assert.AreEqual("", failure.Error);
    }

    [TestMethod]
    public void RecordReference_Defaults_AreSafeAndPredictable()
    {
        var reference = new TweakXLExportRecordReference();

        Assert.AreEqual("", reference.RecordId);
        Assert.IsNull(reference.RecordName);
        Assert.IsNull(reference.RelativePath);
        Assert.AreEqual("", reference.Reason);
    }
}
