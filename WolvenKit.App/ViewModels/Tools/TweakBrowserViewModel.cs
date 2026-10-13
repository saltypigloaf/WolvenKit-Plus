using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WolvenKit.App.Factories;
using WolvenKit.App.Helpers;
using WolvenKit.App.Interaction;
using WolvenKit.App.Models;
using WolvenKit.App.Services;
using WolvenKit.App.ViewModels.Shell;
using WolvenKit.Common.Services;
using WolvenKit.Core.Extensions;
using WolvenKit.Core.Interfaces;
using WolvenKit.Core.Services;
using WolvenKit.RED4.Types;

namespace WolvenKit.App.ViewModels.Tools;

public partial class TweakBrowserViewModel : ToolViewModel
{
    #region fields

    /// <summary>
    /// Identifies the <see ref="ContentId"/> of this tool window.
    /// </summary>
    public const string ToolContentId = "TweakBrowser_Tool";

    /// <summary>
    /// Identifies the caption string used for this tool window.
    /// </summary>
    public const string ToolTitle = "Tweak Browser";

    private readonly AppViewModel _appViewModel;
    private readonly IChunkViewmodelFactory _chunkViewmodelFactory;
    private readonly ISettingsManager _settingsManager;
    private readonly INotificationService _notificationService;
    private readonly IProjectManager _projectManager;
    private readonly ILoggerService _loggerService;
    private readonly ITweakDBService _tweakDB;
    private readonly ITweakXLRecordOverrideService _tweakXLRecordOverrideService;
    private readonly ITweakXLBulkExportService _tweakXLBulkExportService;
    private readonly IProgressService<double> _progressService;

    private List<TweakEntry> _loadedRecordEntries = new();
    private CancellationTokenSource? _bulkExportCancellationTokenSource;

    public string Extension { get; set; } = "tweak";

    [ObservableProperty] private bool _isLoading = true;

    [ObservableProperty] private string _searchText = string.Empty;
    [ObservableProperty] private bool _showNonResolvableEntries;
    [ObservableProperty] private bool _showInlineEntries;
    [ObservableProperty] private string _selectedRecordType = "";

    [ObservableProperty] private TweakEntry? _selectedRecordEntry;
    [ObservableProperty] private TweakEntry? _selectedFlatEntry;
    [ObservableProperty] private TweakEntry? _selectedQueryEntry;
    [ObservableProperty] private TweakEntry? _selectedGroupTagEntry;

    [ObservableProperty] private bool _bulkExportIncludeInlineRecords;
    [ObservableProperty] private bool _bulkExportOverwriteExisting;
    [ObservableProperty] private bool _isBulkExporting;
    [ObservableProperty] private string _bulkExportStatusText = "";

    #endregion fields

    #region constructors

    public TweakBrowserViewModel(
        AppViewModel appViewModel,
        IChunkViewmodelFactory chunkViewmodelFactory,
        ISettingsManager settingsManager,
        INotificationService notificationService,
        IProjectManager projectManager,
        ILoggerService loggerService,
        ITweakDBService tweakDbService,
        ITweakXLRecordOverrideService tweakXLRecordOverrideService,
        ITweakXLBulkExportService tweakXLBulkExportService,
        IProgressService<double> progressService
    ) : base(ToolTitle)
    {
        _appViewModel = appViewModel;
        _chunkViewmodelFactory = chunkViewmodelFactory;
        _settingsManager = settingsManager;
        _notificationService = notificationService;
        _projectManager = projectManager;
        _loggerService = loggerService;
        _tweakDB = tweakDbService;
        _tweakXLRecordOverrideService = tweakXLRecordOverrideService;
        _tweakXLBulkExportService = tweakXLBulkExportService;
        _progressService = progressService;
        _tweakDB.Loaded += Load;

        PropertyChanged += InternalPropertyChanged;
    }

    partial void OnIsLoadingChanged(bool value) => NotifyBulkExportCanExecuteChanged();

    partial void OnIsBulkExportingChanged(bool value) => NotifyBulkExportCanExecuteChanged();

    private void InternalPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(SearchText):
            case nameof(ShowNonResolvableEntries):
            case nameof(ShowInlineEntries):
            case nameof(SelectedRecordType):
                Refresh();
                break;

            case nameof(SelectedRecordEntry):
            {
                SelectedRecord.Clear();
                if (SelectedRecordEntry != null && _tweakDB.IsLoaded)
                {
                    var vm = _chunkViewmodelFactory.ChunkViewModel(
                        TweakDBService.GetRecord(SelectedRecordEntry.Item).NotNull(), SelectedRecordEntry.DisplayName, _appViewModel,
                        null, true);
                    vm.IsExpanded = true;
                    SelectedRecord.Add(vm);
                }
                OnPropertyChanged(nameof(SelectedRecord));
                break;
            }

            case nameof(SelectedFlatEntry):
            {
                SelectedFlat.Clear();
                if (SelectedFlatEntry != null && _tweakDB.IsLoaded)
                {
                    var flat = TweakDBService.GetFlat(SelectedFlatEntry.Item);
                    ArgumentNullException.ThrowIfNull(flat);

                    var vm = _chunkViewmodelFactory.ChunkViewModel(flat, flat.GetType().Name, _appViewModel, null, true);
                    vm.IsExpanded = true;

                    SelectedFlat.Add(vm);
                }
                OnPropertyChanged(nameof(SelectedFlat));
                break;
            }

            case nameof(SelectedQueryEntry):
            {
                SelectedQuery.Clear();
                if (SelectedQueryEntry != null && _tweakDB.IsLoaded)
                {
                    var arr = new CArray<TweakDBID>();
                    foreach (var query in TweakDBService.GetQuery(SelectedQueryEntry.Item).NotNull())
                    {
                        arr.Add(query);
                    }

                    var vm = _chunkViewmodelFactory.ChunkViewModel(arr, nameof(CArray<TweakDBID>), _appViewModel, null, true);
                    vm.IsExpanded = true;

                    SelectedQuery.Add(vm);
                }
                OnPropertyChanged(nameof(SelectedQuery));
                break;
            }

            case nameof(SelectedGroupTagEntry):
            {
                if (SelectedGroupTagEntry != null && _tweakDB.IsLoaded)
                {
                    var u = TweakDBService.GetGroupTagAsFlag(SelectedGroupTagEntry.Item);

                    SelectedGroupTag =
                        _chunkViewmodelFactory.ChunkViewModel((CBitField<InternalEnums.EGroupTag>)u, "Tags", _appViewModel, null, true);
                }
                else
                {
                    SelectedGroupTag = null;
                }
                OnPropertyChanged(nameof(SelectedGroupTag));
                break;
            }

            default:
                break;
        }
    }

    #endregion constructors

    #region Properties

    [ObservableProperty] private ICollectionView _records = new CollectionView(new List<object>());
    [ObservableProperty] private ICollectionView _flats = new CollectionView(new List<object>());
    [ObservableProperty] private ICollectionView _queries = new CollectionView(new List<object>());
    [ObservableProperty] private ICollectionView _groupTags = new CollectionView(new List<object>());

    [ObservableProperty] private List<string> _recordTypes = new();

    public string RecordsHeader => $"Records ({Records.Cast<object>().Count()})";
    public string FlatsHeader => $"Flats ({Flats.Cast<object>().Count()})";
    public string QueriesHeader => $"Queries ({Queries.Cast<object>().Count()})";
    public string GroupTagsHeader => $"GroupTags ({GroupTags.Cast<object>().Count()})";

    public ObservableCollection<ChunkViewModel> SelectedRecord { get; set; } = new();
    public ObservableCollection<ChunkViewModel> SelectedFlat { get; set; } = new();
    public ObservableCollection<ChunkViewModel> SelectedQuery { get; set; } = new();
    [ObservableProperty] private ChunkViewModel? _selectedGroupTag;

    public bool CanEditBulkExportOptions => !IsLoading && !IsBulkExporting;

    #endregion

    #region Methods

    public void LoadTweakDB()
    {
        if (_tweakDB.IsLoaded)
        {
            if (IsLoading)
            {
                Load(_tweakDB, EventArgs.Empty);
            }

            return;
        }

        var dbPath = Path.Combine(_settingsManager.GetRED4GameRootDir(), "r6", "cache", "tweakdb_ep1.bin");
        if (!File.Exists(dbPath))
        {
            dbPath = Path.Combine(_settingsManager.GetRED4GameRootDir(), "r6", "cache", "tweakdb.bin");
        }

        _tweakDB.LoadDB(dbPath);
    }

    private void Load(object? sender, EventArgs eventArgs)
    {
        var records = PrepareList(TweakDBService.GetRecords(), true);
        var flats = PrepareList(TweakDBService.GetFlats());
        var queries = PrepareList(TweakDBService.GetQueries());
        var groupTags = PrepareList(TweakDBService.GetGroupTags());

        var classes = new List<string> { "" };
        foreach (var record in records)
        {
            if (!classes.Contains(record.RecordTypeName.NotNull()))
            {
                classes.Add(record.RecordTypeName);
            }
        }
        classes.Sort();

        DispatcherHelper.RunOnMainThread(() =>
        {
            _loadedRecordEntries = records;
            RecordTypes = classes;

            Records = CollectionViewSource.GetDefaultView(records);
            Records.Filter = Filter;

            Flats = CollectionViewSource.GetDefaultView(flats);
            Flats.Filter = Filter;

            Queries = CollectionViewSource.GetDefaultView(queries);
            Queries.Filter = Filter;

            GroupTags = CollectionViewSource.GetDefaultView(groupTags);
            GroupTags.Filter = Filter;

            Refresh();

            IsLoading = false;
            NotifyBulkExportCanExecuteChanged();
            _notificationService.Success($"Tweak Browser is initialized");
        });

        List<TweakEntry> PrepareList(List<TweakDBID> tweaks, bool isRecord = false)
        {
            var tmpRecords = new ConcurrentQueue<TweakEntry>();
            Parallel.ForEach(tweaks, record => tmpRecords.Enqueue(new TweakEntry(record, isRecord)));
            return tmpRecords.AsParallel().OrderBy(x => x.DisplayName).ToList();
        }
    }

    private void Refresh()
    {
        SelectedRecordEntry = null;

        Records.Refresh();
        OnPropertyChanged(nameof(Records));
        OnPropertyChanged(nameof(RecordsHeader));

        Flats.Refresh();
        OnPropertyChanged(nameof(FlatsHeader));

        Queries.Refresh();
        OnPropertyChanged(nameof(QueriesHeader));

        GroupTags.Refresh();
        OnPropertyChanged(nameof(GroupTagsHeader));

        if (Records.OfType<TweakEntry>().Count() == 1)
        {
            SelectedRecordEntry = Records.OfType<TweakEntry>().First();
        }
    }

    private bool Filter(object obj)
    {
        var entry = (TweakEntry)obj;

        if (!ShowNonResolvableEntries && !entry.IsResolved)
        {
            return false;
        }

        if (!ShowInlineEntries && entry.IsInlineRecord)
        {
            return false;
        }

        if (SelectedRecordType != "" && entry.RecordTypeName != SelectedRecordType)
        {
            return false;
        }

        if (string.IsNullOrEmpty(SearchText))
        {
            return true;
        }

        if (ulong.TryParse(SearchText, out var u1) && entry.Item == u1)
        {
            return true;
        }

        if (ulong.TryParse(SearchText, NumberStyles.HexNumber, CultureInfo.CurrentCulture, out var u2) && entry.Item == u2)
        {
            return true;
        }

        if (entry.IsResolved && entry.DisplayName.Contains(SearchText, StringComparison.InvariantCultureIgnoreCase))
        {
            return true;
        }

        return false;
    }

    private RelayCommand? convertToYAML;
    public ICommand ConvertToYAML => convertToYAML ??= new RelayCommand(ExecuteConvertToYAML, () => true);

    private void ExecuteConvertToYAML()
    {
        if (SelectedRecordEntry is null)
        {
            return;
        }
        if (_projectManager.ActiveProject is null)
        {
            return;
        }

        var txl = _tweakXLRecordOverrideService.BuildRecordOverride(SelectedRecordEntry.Item);
        var tweakDirectory =
            _projectManager.ActiveProject.GetResourceTweakDirectory(_settingsManager.UseAuthorNameAsSubfolder, true);

        var path = Path.Combine(
            tweakDirectory,
            $"{SelectedRecordEntry.DisplayName}.yaml");

        try
        {
            var yaml = _tweakXLRecordOverrideService.SerializeRecordOverride(txl);
            File.WriteAllText(path, yaml);

            _loggerService.Success($"{SelectedRecordEntry.DisplayName} TweakXL written to: {path}");
        }
        catch (Exception ex)
        {
            _loggerService.Error($"Failed to create TweakXL yaml. Error: {ex}");
            throw;
        }

    }

    private AsyncRelayCommand? _bulkExportToYAML;
    public IAsyncRelayCommand BulkExportToYAML => _bulkExportToYAML ??=
        new AsyncRelayCommand(ExecuteBulkExportToYAMLAsync, CanExecuteBulkExportToYAML);

    private bool CanExecuteBulkExportToYAML() =>
        !IsLoading && !IsBulkExporting && _tweakDB.IsLoaded && _loadedRecordEntries.Count > 0;

    private async Task ExecuteBulkExportToYAMLAsync()
    {
        if (!CanExecuteBulkExportToYAML())
        {
            return;
        }

        var exportRoot = PromptForBulkExportRoot();
        if (string.IsNullOrWhiteSpace(exportRoot))
        {
            return;
        }

        var recordIds = _loadedRecordEntries.Select(entry => entry.Item).ToList();
        using var cancellationTokenSource = new CancellationTokenSource();
        _bulkExportCancellationTokenSource = cancellationTokenSource;

        var options = new TweakXLExportOptions
        {
            ExportRoot = exportRoot,
            RecordIds = recordIds,
            IncludeInlineRecords = BulkExportIncludeInlineRecords,
            OverwriteExisting = BulkExportOverwriteExisting,
            WriteReport = true
        };

        var progress = new Progress<TweakXLExportProgress>(ReportBulkExportProgress);

        try
        {
            IsBulkExporting = true;
            BulkExportStatusText = $"Exporting TweakXL YAML 0/{recordIds.Count}";
            _progressService.Report(0);

            var report = await Task.Run(
                () => _tweakXLBulkExportService.ExportAsync(options, progress, cancellationTokenSource.Token),
                cancellationTokenSource.Token);

            await ShowBulkExportSummaryAsync(report);
        }
        catch (OperationCanceledException) when (cancellationTokenSource.IsCancellationRequested)
        {
            _notificationService.Warning("TweakXL bulk export canceled.");
            _loggerService.Warning("TweakXL bulk export canceled before it could finish.");
        }
        catch (Exception ex)
        {
            _notificationService.Error("TweakXL bulk export failed. See the log for details.");
            _loggerService.Error($"Failed to bulk export TweakXL YAML. Error: {ex}");

            await Interactions.ShowMessageBoxAsync(
                $"TweakXL bulk export failed.{Environment.NewLine}{ex.Message}",
                "TweakXL bulk export",
                WMessageBoxButtons.Ok,
                WMessageBoxImage.Error);
        }
        finally
        {
            _bulkExportCancellationTokenSource = null;
            IsBulkExporting = false;
            _progressService.Completed();
        }
    }

    private string? PromptForBulkExportRoot()
    {
        string? defaultPath = null;
        if (_projectManager.ActiveProject is not null)
        {
            defaultPath = _projectManager.ActiveProject.GetResourceTweakDirectory(
                _settingsManager.UseAuthorNameAsSubfolder,
                true);
        }

        var dialog = new FolderPicker
        {
            Title = "Select TweakXL bulk export root",
            OkButtonLabel = "Export",
            ForceFileSystem = true,
            InputPath = Directory.Exists(defaultPath) ? defaultPath : null
        };

        if (dialog.ShowDialog() != true || string.IsNullOrWhiteSpace(dialog.ResultPath))
        {
            return null;
        }

        return dialog.ResultPath;
    }

    private void ReportBulkExportProgress(TweakXLExportProgress progress)
    {
        _progressService.Report(progress.Completion);

        BulkExportStatusText = progress.RequestedRecords <= 0
            ? "Preparing TweakXL YAML export..."
            : $"Exporting TweakXL YAML {progress.ProcessedRecords}/{progress.RequestedRecords}";
    }

    private async Task ShowBulkExportSummaryAsync(TweakXLExportReport report)
    {
        var reportPath = TweakXLExportPathHelper.GetReportPath(report.ExportRoot);
        var caption = report.Canceled ? "TweakXL bulk export canceled" : "TweakXL bulk export complete";
        var message =
            $"Exported: {report.ExportedRecords}{Environment.NewLine}" +
            $"Failed: {report.FailedRecords}{Environment.NewLine}" +
            $"Skipped: {report.SkippedRecords}{Environment.NewLine}" +
            $"Report: {reportPath}";

        BulkExportStatusText = caption;
        if (report.Canceled)
        {
            _notificationService.Warning("TweakXL bulk export canceled.");
            _loggerService.Warning($"{caption}. {message}");
        }
        else if (report.FailedRecords > 0)
        {
            _notificationService.Warning("TweakXL bulk export finished with failures.");
            _loggerService.Warning($"{caption}. {message}");
        }
        else
        {
            _notificationService.Success("TweakXL bulk export complete.");
            _loggerService.Success($"{caption}. {message}");
        }

        await Interactions.ShowMessageBoxAsync(
            message,
            caption,
            WMessageBoxButtons.Ok,
            report.FailedRecords > 0 ? WMessageBoxImage.Warning : WMessageBoxImage.Information);
    }

    private RelayCommand? _cancelBulkExport;
    public ICommand CancelBulkExport => _cancelBulkExport ??=
        new RelayCommand(ExecuteCancelBulkExport, () => IsBulkExporting);

    private void ExecuteCancelBulkExport()
    {
        _bulkExportCancellationTokenSource?.Cancel();
        BulkExportStatusText = "Canceling TweakXL YAML export...";
    }

    private void NotifyBulkExportCanExecuteChanged()
    {
        OnPropertyChanged(nameof(CanEditBulkExportOptions));
        _bulkExportToYAML?.NotifyCanExecuteChanged();
        _cancelBulkExport?.NotifyCanExecuteChanged();
    }

    private RelayCommand? _copyName;
    public ICommand CopyName => _copyName ??= new RelayCommand(ExecuteCopyName, () => true);

    private void ExecuteCopyName()
    {
        if (SelectedRecordEntry?.DisplayName is string s && s != "")
        {
            Clipboard.SetDataObject(s);
        }
    }

    #endregion


    public class TweakEntry
    {
        private static readonly Regex s_inlineRegex = new("_inline[0-9]+");

        public TweakDBID Item { get; }

        public uint CRC32 => (uint)(Item & 0xFFFFFFFF);
        public uint Length => (uint)(Item >> 32);

        public string DisplayName { get; }
        public bool IsResolved { get; }

        public bool IsInlineRecord { get; }
        public string? RecordTypeName { get; }

        public TweakEntry(TweakDBID item, bool isRecord = false)
        {
            Item = item;

            DisplayName = $"<TDBID:{CRC32:X8}:{Length:X2}>";
            if (Item.ResolvedText != null)
            {
                DisplayName = Item.ResolvedText;
                IsResolved = true;
            }

            if (isRecord)
            {
                if (Item.ResolvedText != null)
                {
                    IsInlineRecord = s_inlineRegex.IsMatch(Item.ResolvedText);
                }

                if (TweakDBService.TryGetType(Item, out var type))
                {
                    RecordTypeName = type.Name[8..^7];
                }

            }
        }
    }
}
