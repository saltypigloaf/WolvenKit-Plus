using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WolvenKit.App.Helpers;

namespace WolvenKit.UnitTests;

[TestClass]
public class TweakXLAtomicFileWriterTests
{
    [TestMethod]
    [DataRow(false, false)]
    [DataRow(false, true)]
    [DataRow(true, true)]
    public async Task WriteAsync_PublishesOnlyCompleteBytes(bool existing, bool overwrite)
    {
        using var fixture = new ExportFixture(existing);
        var bytes = Encoding.UTF8.GetBytes("record: café 🚀\n" + new string('x', 20000));
        var unrelated = Path.Combine(fixture.Directory, ".wolvenkit-tweakxl-unrelated.tmp");
        await File.WriteAllTextAsync(unrelated, "unrelated");

        await TweakXLAtomicFileWriter.WriteAsync(fixture.Destination, overwrite, async (stream, token) =>
        {
            var temporaryPath = ((FileStream)stream).Name;
            Assert.AreEqual(fixture.Directory, Path.GetDirectoryName(temporaryPath));
            Assert.AreNotEqual(fixture.Destination, temporaryPath);
            StringAssert.EndsWith(temporaryPath, ".tmp");
            await stream.WriteAsync(bytes.AsMemory(0, 10000), token);
            fixture.AssertDestinationUnchanged();
            await stream.WriteAsync(bytes.AsMemory(10000), token);
            fixture.AssertDestinationUnchanged();
        }, CancellationToken.None);

        CollectionAssert.AreEqual(bytes, await File.ReadAllBytesAsync(fixture.Destination));
        Assert.AreEqual("unrelated", await File.ReadAllTextAsync(unrelated));
        CollectionAssert.AreEquivalent(new[] { fixture.Destination, unrelated }, System.IO.Directory.GetFiles(fixture.Directory));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task WriteAsync_PreCanceled_DoesNotCreateTemporaryFileOrInvokeWriter(bool existing)
    {
        using var fixture = new ExportFixture(existing);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var invoked = false;

        await Assert.ThrowsExceptionAsync<OperationCanceledException>(() =>
            TweakXLAtomicFileWriter.WriteAsync(fixture.Destination, true, (_, _) =>
            {
                invoked = true;
                return Task.CompletedTask;
            }, cancellation.Token));

        Assert.IsFalse(invoked);
        fixture.AssertDestinationUnchanged();
        fixture.AssertNoTemporaryFiles();
    }

    [TestMethod]
    [DataRow(false, false)]
    [DataRow(true, false)]
    [DataRow(false, true)]
    [DataRow(true, true)]
    public async Task WriteAsync_CancellationBeforePublication_PreservesDestinationAndCleansTemporaryFile(
        bool existing, bool completeCallback)
    {
        using var fixture = new ExportFixture(existing);
        using var cancellation = new CancellationTokenSource();

        var error = await Assert.ThrowsExceptionAsync<OperationCanceledException>(() =>
            TweakXLAtomicFileWriter.WriteAsync(fixture.Destination, true, async (stream, token) =>
            {
                await stream.WriteAsync(Encoding.UTF8.GetBytes("partial or completed content"), token);
                cancellation.Cancel();
                if (!completeCallback)
                {
                    token.ThrowIfCancellationRequested();
                }
            }, cancellation.Token));

        Assert.AreEqual(cancellation.Token, error.CancellationToken);
        fixture.AssertDestinationUnchanged();
        fixture.AssertNoTemporaryFiles();
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task WriteAsync_PartialWriteFailure_PreservesDestinationAndOriginalError(bool existing)
    {
        using var fixture = new ExportFixture(existing);
        var expected = new IOException("Injected disk-write failure");

        var error = await Assert.ThrowsExceptionAsync<IOException>(() =>
            TweakXLAtomicFileWriter.WriteAsync(fixture.Destination, true, async (stream, token) =>
            {
                await stream.WriteAsync(Encoding.UTF8.GetBytes("partial"), token);
                throw expected;
            }, CancellationToken.None));

        Assert.AreSame(expected, error);
        fixture.AssertDestinationUnchanged();
        fixture.AssertNoTemporaryFiles();
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task WriteAsync_FinalizationFailure_DoesNotPublish(bool existing)
    {
        using var fixture = new ExportFixture(existing);

        await Assert.ThrowsExceptionAsync<ObjectDisposedException>(() =>
            TweakXLAtomicFileWriter.WriteAsync(fixture.Destination, true, async (stream, token) =>
            {
                await stream.WriteAsync(Encoding.UTF8.GetBytes("complete content"), token);
                // Deliberately close the stream to inject a failure in the helper's final flush.
                await stream.DisposeAsync();
            }, CancellationToken.None));

        fixture.AssertDestinationUnchanged();
        fixture.AssertNoTemporaryFiles();
    }

    [TestMethod]
    public async Task WriteAsync_NoOverwriteRace_PreservesCompetingDestination()
    {
        using var fixture = new ExportFixture(existing: false);

        await Assert.ThrowsExceptionAsync<IOException>(() =>
            TweakXLAtomicFileWriter.WriteAsync(fixture.Destination, false, async (stream, token) =>
            {
                await stream.WriteAsync(Encoding.UTF8.GetBytes("our complete content"), token);
                await File.WriteAllTextAsync(fixture.Destination, "competing content", token);
            }, CancellationToken.None));

        Assert.AreEqual("competing content", await File.ReadAllTextAsync(fixture.Destination));
        fixture.AssertNoTemporaryFiles();
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task WriteAsync_CleanupFailure_ReportsLeftoverPathAndBothErrors(bool canceled)
    {
        using var fixture = new ExportFixture(existing: true);
        using var cancellation = new CancellationTokenSource();
        FileStream? temporaryLock = null;
        string? temporaryPath = null;
        Exception originalError = canceled
            ? new OperationCanceledException(cancellation.Token)
            : new IOException("Injected write failure before cleanup");
        try
        {
            var error = await Assert.ThrowsExceptionAsync<IOException>(() =>
                TweakXLAtomicFileWriter.WriteAsync(fixture.Destination, true, async (stream, _) =>
                {
                    temporaryPath = ((FileStream)stream).Name;
                    // Take a separate lock after closing the writer to make cleanup fail deterministically.
                    await stream.DisposeAsync();
                    temporaryLock = new FileStream(temporaryPath, FileMode.Open, FileAccess.Read, FileShare.None);
                    if (canceled)
                    {
                        cancellation.Cancel();
                    }
                    throw originalError;
                }, cancellation.Token));

            StringAssert.Contains(error.Message, temporaryPath!);
            var causes = (AggregateException)error.InnerException!;
            Assert.AreEqual(2, causes.InnerExceptions.Count);
            Assert.AreSame(originalError, causes.InnerExceptions[0]);
            Assert.IsInstanceOfType<IOException>(causes.InnerExceptions[1]);
            Assert.IsTrue(File.Exists(temporaryPath));
            fixture.AssertDestinationUnchanged();
        }
        finally
        {
            temporaryLock?.Dispose();
        }
    }

    private sealed class ExportFixture : IDisposable
    {
        private readonly bool _existing;
        private static readonly byte[] s_original = [0, 1, 2, 255, 10];

        public ExportFixture(bool existing)
        {
            _existing = existing;
            Directory = Path.Combine(Path.GetTempPath(), $"wolvenkit_tweakxl_atomic_{Guid.NewGuid():N}");
            System.IO.Directory.CreateDirectory(Directory);
            Destination = Path.Combine(Directory, "record.yaml");
            if (existing)
            {
                File.WriteAllBytes(Destination, s_original);
            }
        }

        public string Directory { get; }
        public string Destination { get; }

        public void AssertDestinationUnchanged()
        {
            if (_existing)
            {
                CollectionAssert.AreEqual(s_original, File.ReadAllBytes(Destination));
            }
            else
            {
                Assert.IsFalse(File.Exists(Destination));
            }
        }

        public void AssertNoTemporaryFiles() =>
            Assert.AreEqual(0, System.IO.Directory.GetFiles(Directory, "*.tmp").Length);

        public void Dispose() => System.IO.Directory.Delete(Directory, recursive: true);
    }
}
