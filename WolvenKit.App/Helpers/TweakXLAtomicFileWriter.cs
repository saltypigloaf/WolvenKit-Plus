using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace WolvenKit.App.Helpers;

internal static class TweakXLAtomicFileWriter
{
    // The callback must finish and dispose any text/encoding buffers, leaving the stream open.
    internal static async Task WriteAsync(
        string destinationPath,
        bool overwriteExisting,
        Func<Stream, CancellationToken, Task> writeContent,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(writeContent);
        cancellationToken.ThrowIfCancellationRequested();

        var destination = Path.GetFullPath(destinationPath);
        var directory = Path.GetDirectoryName(destination)!;
        // A sibling avoids cross-volume copy/delete and does not extend a long record filename.
        var temporaryPath = Path.Combine(directory, $".wolvenkit-tweakxl-{Guid.NewGuid():N}.tmp");
        var ownsTemporaryFile = false;

        try
        {
            await using (var stream = new FileStream(
                temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, useAsync: true))
            {
                ownsTemporaryFile = true;
                await writeContent(stream, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                stream.Flush(flushToDisk: true);
            }

            // This is the last cancellation boundary. Once the rename succeeds the file is committed.
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryPath, destination, overwriteExisting);
        }
        catch (Exception writeError)
        {
            if (ownsTemporaryFile)
            {
                try
                {
                    File.Delete(temporaryPath);
                }
                catch (Exception cleanupError) when (cleanupError is IOException or UnauthorizedAccessException)
                {
                    // Keep both causes and the leftover path visible instead of silently orphaning a file.
                    throw new IOException(
                        $"Export failed and temporary file cleanup failed for '{temporaryPath}'.",
                        new AggregateException(writeError, cleanupError));
                }
            }

            throw;
        }
    }
}
