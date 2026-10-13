using System;
using System.Threading;
using System.Threading.Tasks;
using WolvenKit.App.Models;

namespace WolvenKit.App.Services;

public interface ITweakXLBulkExportService
{
    Task<TweakXLExportReport> ExportAsync(
        TweakXLExportOptions options,
        IProgress<TweakXLExportProgress>? progress = null,
        CancellationToken cancellationToken = default);
}
