using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using WolvenKit.App.Helpers;
using WolvenKit.App.Models;
using WolvenKit.App.Services;
using WolvenKit.Common.Services;
using WolvenKit.RED4.Types;

namespace WolvenKit.UnitTests;

[TestClass]
public class TweakXLBulkExportServiceTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ExportAsync_CancellationWithFailure_ReportsBothEvenOnLastRecord(bool hasLaterRecord)
    {
        var exportRoot = CreateTempDirectory();
        try
        {
            using var cancellation = new CancellationTokenSource();
            TweakDBID[] records = hasLaterRecord
                ? ["UnitTests.CanceledFailure", "UnitTests.Unvisited"]
                : ["UnitTests.CanceledFailure"];
            var service = CreateService(records, out _, out var overrides);
            var destination = GetRecordPath(exportRoot, "UnitTests.CanceledFailure");
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            await File.WriteAllTextAsync(destination, "old complete record");
            // Model the helper's cancellation + cleanup error at the per-record exception boundary.
            var failure = new IOException("Temporary file cleanup failed for fixture.tmp",
                new AggregateException(new OperationCanceledException(cancellation.Token), new IOException("cleanup error")));
            overrides.Setup(x => x.SerializeRecordOverride(It.IsAny<TweakXL>(), It.IsAny<TweakXLSerializationDiagnostics>()))
                .Returns((TweakXL _, TweakXLSerializationDiagnostics _) =>
                {
                    cancellation.Cancel();
                    throw failure;
                });

            var report = await service.ExportAsync(new TweakXLExportOptions
            {
                ExportRoot = exportRoot,
                OverwriteExisting = true
            }, cancellationToken: cancellation.Token);

            Assert.IsTrue(report.Canceled);
            Assert.AreEqual(1, report.FailedRecords);
            Assert.AreEqual(0, report.ExportedRecords);
            Assert.AreEqual(failure.Message, report.Failures[0].Error);
            AssertFileContent(exportRoot, "UnitTests.CanceledFailure", "old complete record");
            Assert.IsFalse(File.Exists(GetRecordPath(exportRoot, "UnitTests.Unvisited")));
            overrides.Verify(x => x.BuildRecordOverride(It.IsAny<TweakDBID>()), Times.Once);
            var saved = await File.ReadAllTextAsync(Path.Combine(exportRoot, TweakXLExportPathHelper.ReportFileName));
            StringAssert.Contains(saved, "canceled: true");
            StringAssert.Contains(saved, "failedRecords: 1");
            StringAssert.Contains(saved, "fixture.tmp");
            Assert.AreEqual(0, Directory.GetFiles(exportRoot, "*.tmp", SearchOption.AllDirectories).Length);
        }
        finally
        {
            DeleteTempDirectory(exportRoot);
        }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ExportAsync_CancellationBeforeWrite_PreservesDestinationAndWritesCanceledReport(bool existing)
    {
        var exportRoot = CreateTempDirectory();
        try
        {
            using var cancellation = new CancellationTokenSource();
            var service = CreateService(["UnitTests.Canceled", "UnitTests.Unvisited"], out _, out var overrides);
            var destination = GetRecordPath(exportRoot, "UnitTests.Canceled");
            if (existing)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                await File.WriteAllTextAsync(destination, "old complete record");
            }
            var reportPath = Path.Combine(exportRoot, TweakXLExportPathHelper.ReportFileName);
            await File.WriteAllTextAsync(reportPath, "old report");
            overrides.Setup(x => x.SerializeRecordOverride(It.IsAny<TweakXL>(), It.IsAny<TweakXLSerializationDiagnostics>()))
                .Returns((TweakXL _, TweakXLSerializationDiagnostics _) =>
                {
                    cancellation.Cancel();
                    return "canceled record";
                });

            var report = await service.ExportAsync(new TweakXLExportOptions
            {
                ExportRoot = exportRoot,
                OverwriteExisting = true
            }, cancellationToken: cancellation.Token);

            Assert.IsTrue(report.Canceled);
            Assert.AreEqual(0, report.ExportedRecords);
            Assert.AreEqual(0, report.FailedRecords);
            if (existing)
            {
                AssertFileContent(exportRoot, "UnitTests.Canceled", "old complete record");
            }
            else
            {
                Assert.IsFalse(File.Exists(destination));
            }
            Assert.IsFalse(File.Exists(GetRecordPath(exportRoot, "UnitTests.Unvisited")));
            StringAssert.Contains(await File.ReadAllTextAsync(reportPath), "canceled: true");
            Assert.AreEqual(0, Directory.GetFiles(exportRoot, "*.tmp", SearchOption.AllDirectories).Length);
        }
        finally
        {
            DeleteTempDirectory(exportRoot);
        }
    }

    [TestMethod]
    public async Task ExportAsync_ReplacementFailure_PreservesRecordAndContinues()
    {
        var exportRoot = CreateTempDirectory();
        try
        {
            var service = CreateService(["UnitTests.Locked", "UnitTests.Next"]);
            var destination = GetRecordPath(exportRoot, "UnitTests.Locked");
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            await File.WriteAllTextAsync(destination, "old complete record");
            TweakXLExportReport report;
            // Readers without delete sharing prevent Windows from replacing this file.
            using (var locked = new FileStream(destination, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                report = await service.ExportAsync(new TweakXLExportOptions
                {
                    ExportRoot = exportRoot,
                    OverwriteExisting = true
                });
            }

            Assert.AreEqual(1, report.ExportedRecords);
            Assert.AreEqual(1, report.FailedRecords);
            Assert.AreEqual("UnitTests.Locked", report.Failures[0].RecordName);
            AssertFileContent(exportRoot, "UnitTests.Locked", "old complete record");
            AssertFileContent(exportRoot, "UnitTests.Next", "yaml:UnitTests.Next");
            Assert.IsTrue(File.Exists(Path.Combine(exportRoot, TweakXLExportPathHelper.ReportFileName)));
            Assert.AreEqual(0, Directory.GetFiles(exportRoot, "*.tmp", SearchOption.AllDirectories).Length);
        }
        finally
        {
            DeleteTempDirectory(exportRoot);
        }
    }

    [TestMethod]
    public async Task ExportAsync_NoOverwriteRace_PreservesNewlyCreatedRecord()
    {
        var exportRoot = CreateTempDirectory();
        try
        {
            var service = CreateService(["UnitTests.Race"], out _, out var overrides);
            var destination = GetRecordPath(exportRoot, "UnitTests.Race");
            overrides.Setup(x => x.SerializeRecordOverride(It.IsAny<TweakXL>(), It.IsAny<TweakXLSerializationDiagnostics>()))
                .Returns((TweakXL _, TweakXLSerializationDiagnostics _) =>
                {
                    File.WriteAllText(destination, "competing record");
                    return "our record";
                });

            var report = await service.ExportAsync(new TweakXLExportOptions { ExportRoot = exportRoot });

            Assert.AreEqual(0, report.ExportedRecords);
            Assert.AreEqual(1, report.FailedRecords);
            AssertFileContent(exportRoot, "UnitTests.Race", "competing record");
            Assert.AreEqual(0, Directory.GetFiles(exportRoot, "*.tmp", SearchOption.AllDirectories).Length);
        }
        finally
        {
            DeleteTempDirectory(exportRoot);
        }
    }

    [TestMethod]
    public async Task ExportAsync_ReportReplacementFailure_PreservesPreviousReport()
    {
        var exportRoot = CreateTempDirectory();
        try
        {
            var service = CreateService(["UnitTests.ReportFailure"]);
            var reportPath = Path.Combine(exportRoot, TweakXLExportPathHelper.ReportFileName);
            await File.WriteAllTextAsync(reportPath, "old complete report");
            using (var locked = new FileStream(reportPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                Exception? writeError = null;
                try
                {
                    await service.ExportAsync(new TweakXLExportOptions { ExportRoot = exportRoot });
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    writeError = ex;
                }
                Assert.IsNotNull(writeError, "A locked report must prevent publication.");
            }

            Assert.AreEqual("old complete report", await File.ReadAllTextAsync(reportPath));
            AssertFileContent(exportRoot, "UnitTests.ReportFailure", "yaml:UnitTests.ReportFailure");
            Assert.AreEqual(0, Directory.GetFiles(exportRoot, "*.tmp", SearchOption.AllDirectories).Length);
        }
        finally
        {
            DeleteTempDirectory(exportRoot);
        }
    }

    [TestMethod]
    public async Task ExportAsync_IncludesInlineRecordsWhenEnabled()
    {
        var exportRoot = CreateTempDirectory();
        try
        {
            var service = CreateService(["Items.Parent", "Items.Parent_inline0", "Items.Parent_inline0_inline1"]);
            var report = await service.ExportAsync(new TweakXLExportOptions
            {
                ExportRoot = exportRoot,
                IncludeInlineRecords = true,
                WriteReport = false
            });

            AssertFileContent(exportRoot, "Items.Parent", "yaml:Items.Parent");
            AssertFileContent(exportRoot, "Items.Parent_inline0", "yaml:Items.Parent_inline0");
            AssertFileContent(exportRoot, "Items.Parent_inline0_inline1", "yaml:Items.Parent_inline0_inline1");
            Assert.AreEqual(3, report.ExportedRecords);
            Assert.AreEqual(0, report.FailedRecords);
            Assert.AreEqual(0, report.SkippedRecords);
            Assert.AreEqual(0, report.SkippedInlineRecords);
        }
        finally
        {
            DeleteTempDirectory(exportRoot);
        }
    }

    [TestMethod]
    public async Task ExportAsync_ExportsTwoResolvedRecordsIntoExpectedFolders()
    {
        var exportRoot = CreateTempDirectory();

        try
        {
            TweakDBID[] records = ["UnitTests.First", "Items.Second"];
            var service = CreateService(records);

            var report = await service.ExportAsync(new TweakXLExportOptions
            {
                ExportRoot = exportRoot,
                WriteReport = false
            });

            AssertFileContent(exportRoot, "UnitTests.First", "yaml:UnitTests.First");
            AssertFileContent(exportRoot, "Items.Second", "yaml:Items.Second");
            Assert.AreEqual(2, report.RequestedRecords);
            Assert.AreEqual(2, report.ExportedRecords);
            Assert.AreEqual(0, report.FailedRecords);
            Assert.AreEqual(0, report.SkippedRecords);
        }
        finally
        {
            DeleteTempDirectory(exportRoot);
        }
    }

    [TestMethod]
    public async Task ExportAsync_PreservesFullRecordNameInFilename()
    {
        var exportRoot = CreateTempDirectory();

        try
        {
            TweakDBID[] records = ["Character.ServiceSpecialistMale"];
            var service = CreateService(records);

            await service.ExportAsync(new TweakXLExportOptions
            {
                ExportRoot = exportRoot,
                WriteReport = false
            });

            var expectedPath = Path.Combine(
                exportRoot,
                "Character",
                "Character.ServiceSpecialistMale.yaml");
            Assert.IsTrue(File.Exists(expectedPath), expectedPath);
        }
        finally
        {
            DeleteTempDirectory(exportRoot);
        }
    }

    [TestMethod]
    public async Task ExportAsync_SkipsExistingFilesWhenOverwriteExistingIsFalse()
    {
        var exportRoot = CreateTempDirectory();

        try
        {
            TweakDBID[] records = ["UnitTests.Exists"];
            var service = CreateService(records, out _, out var recordOverrideService);
            var existingPath = GetRecordPath(exportRoot, "UnitTests.Exists");
            Directory.CreateDirectory(Path.GetDirectoryName(existingPath)!);
            await File.WriteAllTextAsync(existingPath, "existing");

            var report = await service.ExportAsync(new TweakXLExportOptions
            {
                ExportRoot = exportRoot,
                WriteReport = false
            });

            Assert.AreEqual("existing", await File.ReadAllTextAsync(existingPath));
            Assert.AreEqual(0, report.ExportedRecords);
            Assert.AreEqual(1, report.SkippedRecords);
            Assert.AreEqual(1, report.ExistingFileSkips);
            Assert.AreEqual(1, report.ExistingFileSkipDetails.Count);
            recordOverrideService.Verify(x => x.BuildRecordOverride(It.IsAny<TweakDBID>()), Times.Never);
        }
        finally
        {
            DeleteTempDirectory(exportRoot);
        }
    }

    [TestMethod]
    public async Task ExportAsync_OverwritesExistingFilesWhenOverwriteExistingIsTrue()
    {
        var exportRoot = CreateTempDirectory();

        try
        {
            TweakDBID[] records = ["UnitTests.Exists"];
            var service = CreateService(records);
            var existingPath = GetRecordPath(exportRoot, "UnitTests.Exists");
            Directory.CreateDirectory(Path.GetDirectoryName(existingPath)!);
            await File.WriteAllTextAsync(existingPath, "existing");

            var report = await service.ExportAsync(new TweakXLExportOptions
            {
                ExportRoot = exportRoot,
                OverwriteExisting = true,
                WriteReport = false
            });

            Assert.AreEqual("yaml:UnitTests.Exists", await File.ReadAllTextAsync(existingPath));
            Assert.AreEqual(1, report.ExportedRecords);
            Assert.AreEqual(0, report.ExistingFileSkips);
        }
        finally
        {
            DeleteTempDirectory(exportRoot);
        }
    }

    [TestMethod]
    public async Task ExportAsync_ReportsPerRecordFailureWithoutAborting()
    {
        var exportRoot = CreateTempDirectory();

        try
        {
            TweakDBID failingRecord = "UnitTests.Fails";
            TweakDBID successfulRecord = "UnitTests.Success";
            var service = CreateService([failingRecord, successfulRecord], out _, out var recordOverrideService);
            recordOverrideService
                .Setup(x => x.BuildRecordOverride(It.Is<TweakDBID>(id => id.Equals(failingRecord))))
                .Throws(new InvalidOperationException("record failed"));

            var report = await service.ExportAsync(new TweakXLExportOptions
            {
                ExportRoot = exportRoot,
                WriteReport = false
            });

            Assert.IsFalse(File.Exists(GetRecordPath(exportRoot, "UnitTests.Fails")));
            AssertFileContent(exportRoot, "UnitTests.Success", "yaml:UnitTests.Success");
            Assert.AreEqual(1, report.ExportedRecords);
            Assert.AreEqual(1, report.FailedRecords);
            Assert.AreEqual(1, report.Failures.Count);
            Assert.AreEqual("UnitTests.Fails", report.Failures[0].RecordName);
            StringAssert.Contains(report.Failures[0].Error, "record failed");
        }
        finally
        {
            DeleteTempDirectory(exportRoot);
        }
    }

    [TestMethod]
    public async Task ExportAsync_HonorsCancellation()
    {
        var exportRoot = CreateTempDirectory();

        try
        {
            TweakDBID[] records = ["UnitTests.First", "UnitTests.Second"];
            var service = CreateService(records);
            using var cancellationTokenSource = new CancellationTokenSource();
            var progress = new DelegateProgress<TweakXLExportProgress>(value =>
            {
                if (value.ExportedRecords == 1)
                {
                    cancellationTokenSource.Cancel();
                }
            });

            var report = await service.ExportAsync(
                new TweakXLExportOptions
                {
                    ExportRoot = exportRoot,
                    WriteReport = false
                },
                progress,
                cancellationTokenSource.Token);

            Assert.IsTrue(report.Canceled);
            Assert.AreEqual(1, report.ExportedRecords);
            Assert.IsTrue(File.Exists(GetRecordPath(exportRoot, "UnitTests.First")));
            Assert.IsFalse(File.Exists(GetRecordPath(exportRoot, "UnitTests.Second")));
        }
        finally
        {
            DeleteTempDirectory(exportRoot);
        }
    }

    [TestMethod]
    public async Task ExportAsync_WritesExportReportYaml()
    {
        var exportRoot = CreateTempDirectory();

        try
        {
            TweakDBID[] records = ["UnitTests.Report"];
            var service = CreateService(records);

            await service.ExportAsync(new TweakXLExportOptions
            {
                ExportRoot = exportRoot
            });

            var reportPath = Path.Combine(exportRoot, TweakXLExportPathHelper.ReportFileName);
            Assert.IsTrue(File.Exists(reportPath), reportPath);

            var yaml = await File.ReadAllTextAsync(reportPath);
            StringAssert.Contains(yaml, "requestedRecords: 1");
            StringAssert.Contains(yaml, "exportedRecords: 1");
        }
        finally
        {
            DeleteTempDirectory(exportRoot);
        }
    }

    [TestMethod]
    public async Task ExportAsync_WhenCanceledAndWriteReportIsTrue_WritesCanceledReport()
    {
        var exportRoot = CreateTempDirectory();

        try
        {
            TweakDBID[] records = ["UnitTests.First", "UnitTests.Second"];
            var service = CreateService(records);
            using var cancellationTokenSource = new CancellationTokenSource();
            var progress = new DelegateProgress<TweakXLExportProgress>(value =>
            {
                if (value.ExportedRecords == 1)
                {
                    cancellationTokenSource.Cancel();
                }
            });

            var report = await service.ExportAsync(
                new TweakXLExportOptions
                {
                    ExportRoot = exportRoot,
                    WriteReport = true
                },
                progress,
                cancellationTokenSource.Token);

            var reportPath = Path.Combine(exportRoot, TweakXLExportPathHelper.ReportFileName);
            Assert.IsTrue(report.Canceled);
            Assert.IsTrue(File.Exists(reportPath), reportPath);

            var yaml = await File.ReadAllTextAsync(reportPath);
            StringAssert.Contains(yaml, "canceled: true");
            StringAssert.Contains(yaml, "exportedRecords: 1");
        }
        finally
        {
            DeleteTempDirectory(exportRoot);
        }
    }

    [TestMethod]
    public async Task ExportAsync_AlwaysOverwritesExportReportRegardlessOfOverwriteExisting()
    {
        var exportRoot = CreateTempDirectory();

        try
        {
            TweakDBID[] records = ["UnitTests.Exists"];
            var service = CreateService(records);
            var existingRecordPath = GetRecordPath(exportRoot, "UnitTests.Exists");
            Directory.CreateDirectory(Path.GetDirectoryName(existingRecordPath)!);
            await File.WriteAllTextAsync(existingRecordPath, "existing record yaml");

            var reportPath = Path.Combine(exportRoot, TweakXLExportPathHelper.ReportFileName);
            await File.WriteAllTextAsync(reportPath, "stale report");

            var report = await service.ExportAsync(new TweakXLExportOptions
            {
                ExportRoot = exportRoot,
                OverwriteExisting = false,
                WriteReport = true
            });

            Assert.AreEqual("existing record yaml", await File.ReadAllTextAsync(existingRecordPath));
            Assert.AreEqual(0, report.ExportedRecords);
            Assert.AreEqual(1, report.ExistingFileSkips);

            var yaml = await File.ReadAllTextAsync(reportPath);
            Assert.AreNotEqual("stale report", yaml);
            StringAssert.Contains(yaml, "existingFileSkips: 1");
        }
        finally
        {
            DeleteTempDirectory(exportRoot);
        }
    }

    [TestMethod]
    public async Task ExportAsync_SkipsInlineAndUnresolvedRecordsByDefault()
    {
        var exportRoot = CreateTempDirectory();

        try
        {
            TweakDBID inlineRecord = "UnitTests_inline0.Record";
            TweakDBID unresolvedRecord = 0x0000000200000001UL;
            TweakDBID resolvedRecord = "UnitTests.Visible";
            var service = CreateService([inlineRecord, unresolvedRecord, resolvedRecord]);

            var report = await service.ExportAsync(new TweakXLExportOptions
            {
                ExportRoot = exportRoot,
                WriteReport = false
            });

            Assert.AreEqual(1, report.ExportedRecords);
            Assert.AreEqual(2, report.SkippedRecords);
            Assert.AreEqual(1, report.SkippedInlineRecords);
            Assert.AreEqual(1, report.SkippedUnresolvedRecords);
            Assert.AreEqual(1, report.SkippedInlineRecordDetails.Count);
            Assert.AreEqual(1, report.SkippedUnresolvedRecordDetails.Count);
            Assert.IsFalse(File.Exists(GetRecordPath(exportRoot, "UnitTests_inline0.Record")));
            AssertFileContent(exportRoot, "UnitTests.Visible", "yaml:UnitTests.Visible");
        }
        finally
        {
            DeleteTempDirectory(exportRoot);
        }
    }

    private static TweakXLBulkExportService CreateService(IReadOnlyList<TweakDBID> recordIds) =>
        CreateService(recordIds, out _, out _);

    private static TweakXLBulkExportService CreateService(
        IReadOnlyList<TweakDBID> recordIds,
        out Mock<ITweakDBService> tweakDbService,
        out Mock<ITweakXLRecordOverrideService> recordOverrideService)
    {
        tweakDbService = new Mock<ITweakDBService>();
        tweakDbService.SetupGet(x => x.SourcePath).Returns("source.bin");
        tweakDbService.Setup(x => x.GetRecords()).Returns(() => ToHashes(recordIds));

        foreach (var recordId in recordIds)
        {
            tweakDbService
                .Setup(x => x.GetString((ulong)recordId))
                .Returns(recordId.GetResolvedText());
        }

        recordOverrideService = new Mock<ITweakXLRecordOverrideService>();
        recordOverrideService
            .Setup(x => x.BuildRecordOverride(It.IsAny<TweakDBID>()))
            .Returns((TweakDBID id) => new TweakXL { ID = id });
        recordOverrideService
            .Setup(x => x.SerializeRecordOverride(It.IsAny<TweakXL>(), It.IsAny<TweakXLSerializationDiagnostics>()))
            .Returns((TweakXL txl, TweakXLSerializationDiagnostics _) => $"yaml:{txl.ID.GetResolvedText()}");

        return new TweakXLBulkExportService(tweakDbService.Object, recordOverrideService.Object);
    }

    private static IReadOnlyList<ulong> ToHashes(IEnumerable<TweakDBID> recordIds)
    {
        var result = new List<ulong>();
        foreach (var recordId in recordIds)
        {
            result.Add(recordId);
        }

        return result;
    }

    private static void AssertFileContent(string exportRoot, string recordName, string expectedContent)
    {
        var path = GetRecordPath(exportRoot, recordName);

        Assert.IsTrue(File.Exists(path), path);
        Assert.AreEqual(expectedContent, File.ReadAllText(path));
    }

    private static string GetRecordPath(string exportRoot, string recordName) =>
        TweakXLExportPathHelper.GetAbsolutePath(exportRoot, TweakXLExportPathHelper.GetRelativePath(recordName));

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"wolvenkit_tweakxl_bulk_{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteTempDirectory(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, true);
        }
    }

    private sealed class DelegateProgress<T> : IProgress<T>
    {
        private readonly Action<T> _onReport;

        public DelegateProgress(Action<T> onReport)
        {
            _onReport = onReport;
        }

        public void Report(T value) => _onReport(value);
    }
}
