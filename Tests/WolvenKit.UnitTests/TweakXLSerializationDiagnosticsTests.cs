using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using WolvenKit.App.Helpers;
using WolvenKit.App.Models;
using WolvenKit.App.Services;
using WolvenKit.Common.Services;
using WolvenKit.RED4.Types;
using YamlDotNet.RepresentationModel;
using YamlDotNet.Serialization;

namespace WolvenKit.UnitTests;

[TestClass]
public class TweakXLSerializationDiagnosticsTests
{
    [TestMethod]
    public void SupportedValues_PreserveYamlWithoutDiagnostics()
    {
        var record = Record("UnitTests.Supported");
        record.Properties.Add("enabled", (CBool)true);
        record.Properties.Add("flags", new CArray<CBool> { false, true });
        record.Properties.Add("names", new CArray<CName> { "before", CName.Empty, "after" });
        record.Properties.Add("text", (CString)"plain");
        record.Properties.Add("texts", new CArray<CString> { "one", "two" });
        var diagnostics = new TweakXLSerializationDiagnostics();

        var yaml = Serializer().SerializeRecordOverride(record, diagnostics).Replace("\r\n", "\n");

        Assert.AreEqual("""
            UnitTests.Supported:
              enabled: True
              flags:
                - False
                - True
              names:
                - before
                - None
                - after
              text: plain
              texts:
                - one
                - two
              $type: gamedataTest_Record
            """ + "\n", yaml);
        Assert.AreEqual(0, diagnostics.FallbackValueTypes.Count);
        Assert.AreEqual(0, diagnostics.UnsupportedValueTypes.Count);
    }

    [TestMethod]
    public void StandaloneFlat_ReportsFallbackWithoutAddingRecordWrapper()
    {
        var flat = new TweakXL { ID = "UnitTests.Flat", Value = new TextValue() };
        var diagnostics = new TweakXLSerializationDiagnostics();

        var yaml = Serializer().SerializeRecordOverride(flat, diagnostics);

        Assert.AreEqual("UnitTests.Flat: fixture scalar\n", yaml.Replace("\r\n", "\n"));
        CollectionAssert.AreEqual(new[] { typeof(TextValue).FullName! }, diagnostics.FallbackValueTypes.ToArray());
        Assert.AreEqual(0, diagnostics.UnsupportedValueTypes.Count);
    }

    [TestMethod]
    [DataRow("override", false)]
    [DataRow("override", true)]
    [DataRow("generic-overload", false)]
    [DataRow("generic-overload", true)]
    [DataRow("hidden-inherited", false)]
    [DataRow("hidden-inherited", true)]
    public void FallbackScalarAndArray_PreserveTextAndDeduplicateTypes(string kind, bool array)
    {
        IRedType value = kind switch
        {
            "override" => new TextValue(),
            "generic-overload" => new GenericOverloadTextValue(),
            "hidden-inherited" => new HiddenInheritedTextValue(),
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
        var record = Record("UnitTests.Fallback");
        record.Properties.Add("example", array
            ? new CArray<IRedType> { value, value }
            : value);
        var diagnostics = new TweakXLSerializationDiagnostics();

        var properties = Properties(Serializer().SerializeRecordOverride(record, diagnostics), record);
        var yamlValue = properties.Children[new YamlScalarNode("example")];
        if (array)
        {
            var sequence = (YamlSequenceNode)yamlValue;
            Assert.AreEqual(2, sequence.Children.Count);
            Assert.AreEqual("fixture scalar", ((YamlScalarNode)sequence.Children[0]).Value);
            Assert.AreEqual("fixture scalar", ((YamlScalarNode)sequence.Children[1]).Value);
        }
        else
        {
            Assert.AreEqual("fixture scalar", ((YamlScalarNode)yamlValue).Value);
        }
        CollectionAssert.AreEqual(new[] { value.GetType().FullName! }, diagnostics.FallbackValueTypes.ToArray());
        Assert.AreEqual(0, diagnostics.UnsupportedValueTypes.Count);
    }

    [TestMethod]
    [DataRow("no-override", false)]
    [DataRow("no-override", true)]
    [DataRow("null", false)]
    [DataRow("null", true)]
    [DataRow("null-text", false)]
    [DataRow("null-text", true)]
    [DataRow("throwing-text", false)]
    [DataRow("throwing-text", true)]
    [DataRow("nested-array", true)]
    [DataRow("hidden-class", false)]
    [DataRow("hidden-class", true)]
    [DataRow("hidden-struct", false)]
    [DataRow("hidden-struct", true)]
    [DataRow("generic-only", false)]
    [DataRow("generic-only", true)]
    public void UnsupportedValues_FailAndIdentifyType(string kind, bool array)
    {
        IRedType? value = kind switch
        {
            "no-override" => new CVariant { Value = (CInt32)42 },
            "null" => null,
            "null-text" => new NullTextValue(),
            "throwing-text" => new ThrowingTextValue(),
            "nested-array" => new CArray<CInt32> { 42 },
            "hidden-class" => new HiddenTextValue(),
            "hidden-struct" => new HiddenStructTextValue(),
            "generic-only" => new GenericOnlyTextValue(),
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
        var record = Record("UnitTests.Unsupported");
        // CArray.Add ignores null; use an occupied slot to exercise a stored null element.
        var values = new CArray<IRedType>(1) { [0] = value! };
        record.Properties.Add("example", array ? values : value!);
        var diagnostics = new TweakXLSerializationDiagnostics();

        var exception = Assert.ThrowsException<NotSupportedException>(() =>
            Serializer().SerializeRecordOverride(record, diagnostics));

        var expectedType = value?.GetType().FullName ?? "<null>";
        StringAssert.Contains(exception.Message, expectedType);
        CollectionAssert.AreEqual(new[] { expectedType }, diagnostics.UnsupportedValueTypes.ToArray());
        Assert.AreEqual(0, diagnostics.FallbackValueTypes.Count);
        if (kind == "throwing-text")
        {
            Assert.IsInstanceOfType<InvalidOperationException>(exception.InnerException);
        }
    }

    [TestMethod]
    public void UnsupportedTopLevelItem_IsNotSilentlyDropped()
    {
        var diagnostics = new TweakXLSerializationDiagnostics();
        var serializer = new SerializerBuilder()
            .WithTypeConverter(new TweakXLYamlTypeConverter(
                new Mock<ILocKeyService>().Object, new Mock<ITweakDBService>().Object, diagnostics))
            .Build();

        Assert.ThrowsException<NotSupportedException>(() =>
            serializer.Serialize(new TweakXLFile { new TweakXLSequence { ID = "UnitTests.Sequence" } }));

        CollectionAssert.AreEqual(new[] { typeof(TweakXLSequence).FullName! }, diagnostics.UnsupportedValueTypes.ToArray());
    }

    [TestMethod]
    public async Task BulkExport_AggregatesDiagnosticsAcrossFailuresAndWritesReport()
    {
        var root = TempDirectory();
        try
        {
            var failed = Record("UnitTests.Failed");
            failed.Properties.Add("aFallback", new TextValue());
            failed.Properties.Add("zUnsupported", new CVariant());
            var successful = Record("UnitTests.Success");
            successful.Properties.Add("values", new CArray<IRedType> { new AnotherTextValue(), new AnotherTextValue() });
            var service = BulkService(failed, successful);

            var report = await service.ExportAsync(new TweakXLExportOptions { ExportRoot = root });

            Assert.AreEqual(1, report.FailedRecords);
            Assert.AreEqual(1, report.ExportedRecords);
            Assert.AreEqual("UnitTests.Failed", report.Failures.Single().RecordName);
            Assert.IsFalse(File.Exists(RecordPath(root, failed)));
            Assert.IsTrue(File.Exists(RecordPath(root, successful)));
            var expectedFallbacks = new[] { typeof(AnotherTextValue).FullName!, typeof(TextValue).FullName! };
            CollectionAssert.AreEqual(expectedFallbacks, report.FallbackValueTypes.ToArray());
            CollectionAssert.AreEqual(new[] { typeof(CVariant).FullName! }, report.UnsupportedValueTypes.ToArray());
            var saved = Mapping(await File.ReadAllTextAsync(Path.Combine(root, TweakXLExportPathHelper.ReportFileName)));
            CollectionAssert.AreEqual(expectedFallbacks, SequenceValues(saved, "fallbackValueTypes"));
            CollectionAssert.AreEqual(report.UnsupportedValueTypes.ToArray(), SequenceValues(saved, "unsupportedValueTypes"));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [TestMethod]
    public async Task BulkExport_CancellationReportsOnlyAttemptedValues_AndNextRunIsIsolated()
    {
        var root = TempDirectory();
        try
        {
            var first = Record("UnitTests.First");
            first.Properties.Add("example", new TextValue());
            var unvisited = Record("UnitTests.Unvisited");
            unvisited.Properties.Add("example", new CVariant());
            var clean = Record("UnitTests.Clean");
            clean.Properties.Add("example", (CString)"supported");
            var service = BulkService(first, unvisited, clean);
            using var cancellation = new CancellationTokenSource();
            var report = await service.ExportAsync(
                new TweakXLExportOptions { ExportRoot = root },
                new CancelAfterFirstExport(cancellation), cancellation.Token);

            Assert.IsTrue(report.Canceled);
            Assert.AreEqual(1, report.ExportedRecords);
            Assert.AreEqual(0, report.FailedRecords);
            CollectionAssert.AreEqual(new[] { typeof(TextValue).FullName! }, report.FallbackValueTypes.ToArray());
            Assert.AreEqual(0, report.UnsupportedValueTypes.Count);
            Assert.IsFalse(File.Exists(RecordPath(root, unvisited)));
            var saved = Mapping(await File.ReadAllTextAsync(Path.Combine(root, TweakXLExportPathHelper.ReportFileName)));
            Assert.AreEqual("true", ((YamlScalarNode)saved.Children[new YamlScalarNode("canceled")]).Value);
            CollectionAssert.AreEqual(report.FallbackValueTypes.ToArray(), SequenceValues(saved, "fallbackValueTypes"));
            Assert.AreEqual(0, SequenceValues(saved, "unsupportedValueTypes").Length);

            var next = await service.ExportAsync(new TweakXLExportOptions
            {
                ExportRoot = root,
                RecordIds = [clean.ID],
                WriteReport = false
            });
            Assert.AreEqual(1, next.ExportedRecords);
            Assert.AreEqual(0, next.FallbackValueTypes.Count);
            Assert.AreEqual(0, next.UnsupportedValueTypes.Count);
            Assert.AreEqual(1, report.FallbackValueTypes.Count, "The previous report must remain detached.");
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [TestMethod]
    public async Task BulkExport_SkippedExistingValuesAreNotDiagnosed_AndUnsupportedDoesNotOverwrite()
    {
        var root = TempDirectory();
        try
        {
            var record = Record("UnitTests.Existing");
            record.Properties.Add("example", new CVariant());
            var path = RecordPath(root, record);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, "preserve me");
            var service = BulkService(record);

            var skipped = await service.ExportAsync(new TweakXLExportOptions { ExportRoot = root, WriteReport = false });
            Assert.AreEqual(1, skipped.ExistingFileSkips);
            Assert.AreEqual(0, skipped.UnsupportedValueTypes.Count);
            var failed = await service.ExportAsync(new TweakXLExportOptions
            {
                ExportRoot = root,
                OverwriteExisting = true,
                WriteReport = false
            });
            Assert.AreEqual(1, failed.FailedRecords);
            CollectionAssert.AreEqual(new[] { typeof(CVariant).FullName! }, failed.UnsupportedValueTypes.ToArray());
            Assert.AreEqual("preserve me", await File.ReadAllTextAsync(path));

            record.Properties["example"] = (CString)"supported";
            var next = await service.ExportAsync(new TweakXLExportOptions
            {
                ExportRoot = root,
                OverwriteExisting = true,
                WriteReport = false
            });
            Assert.AreEqual(1, next.ExportedRecords);
            Assert.AreEqual(0, next.UnsupportedValueTypes.Count, "Failed-run diagnostics must not leak.");
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private static TweakXL Record(string name) => new() { ID = name, Type = "gamedataTest_Record" };

    private static TweakXLRecordOverrideService Serializer() =>
        new(new Mock<ILocKeyService>().Object, new Mock<ITweakDBService>().Object);

    private static TweakXLBulkExportService BulkService(params TweakXL[] records)
    {
        var database = new Mock<ITweakDBService>();
        database.Setup(x => x.GetRecords()).Returns(records.Select(record => (ulong)record.ID).ToArray());
        database.Setup(x => x.GetString(It.IsAny<ulong>())).Returns((ulong id) => ((TweakDBID)id).GetResolvedText());
        var overrides = new Mock<ITweakXLRecordOverrideService>();
        overrides.Setup(x => x.BuildRecordOverride(It.IsAny<TweakDBID>()))
            .Returns((TweakDBID id) => records.Single(record => record.ID == id));
        var serializer = Serializer();
        overrides.Setup(x => x.SerializeRecordOverride(It.IsAny<TweakXL>(), It.IsAny<TweakXLSerializationDiagnostics>()))
            .Returns((TweakXL record, TweakXLSerializationDiagnostics diagnostics) => serializer.SerializeRecordOverride(record, diagnostics));
        return new TweakXLBulkExportService(database.Object, overrides.Object);
    }

    private static YamlMappingNode Properties(string yaml, TweakXL record) =>
        (YamlMappingNode)Mapping(yaml).Children[new YamlScalarNode(record.ID.GetResolvedText())];

    private static YamlMappingNode Mapping(string yaml)
    {
        var stream = new YamlStream();
        using var reader = new StringReader(yaml);
        stream.Load(reader);
        return (YamlMappingNode)stream.Documents[0].RootNode;
    }

    private static string[] SequenceValues(YamlMappingNode mapping, string property) =>
        ((YamlSequenceNode)mapping.Children[new YamlScalarNode(property)]).Children
            .Select(value => ((YamlScalarNode)value).Value!).ToArray();

    private static string RecordPath(string root, TweakXL record) =>
        TweakXLExportPathHelper.GetAbsolutePath(root, TweakXLExportPathHelper.GetRelativePath(record.ID.GetResolvedText()!));

    private static string TempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"wolvenkit_tweakxl_diagnostics_{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private class TextValue : IRedType
    {
        public override string ToString() => "fixture scalar";
    }

    private sealed class GenericOverloadTextValue : TextValue
    {
        public string ToString<T>() => "generic overload must not be used";
    }

    private sealed class HiddenInheritedTextValue : TextValue
    {
        public new string ToString() => "hidden method must not be used";
    }

    private sealed class HiddenTextValue : IRedType
    {
        public new string ToString() => "hidden method must not be used";
    }

    private struct HiddenStructTextValue : IRedType
    {
        public new string ToString() => "hidden method must not be used";
    }

    private sealed class GenericOnlyTextValue : IRedType
    {
        public string ToString<T>() => "generic overload must not be used";
    }

    private sealed class AnotherTextValue : IRedType
    {
        public override string ToString() => "another scalar";
    }

    private sealed class NullTextValue : IRedType
    {
        public override string? ToString() => null;
    }

    private sealed class ThrowingTextValue : IRedType
    {
        public override string ToString() => throw new InvalidOperationException("fixture conversion failure");
    }

    private sealed class CancelAfterFirstExport(CancellationTokenSource cancellation) : IProgress<TweakXLExportProgress>
    {
        public void Report(TweakXLExportProgress value)
        {
            if (value.ExportedRecords == 1)
            {
                cancellation.Cancel();
            }
        }
    }
}
