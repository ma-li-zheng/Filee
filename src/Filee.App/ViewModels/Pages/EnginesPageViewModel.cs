// Conversion engines: optional downloads and the folder they are stored in, availability and priority order.

using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Filee.App.Services;
using Filee.Core.Conversion;
using Filee.Core.Localization;
using Filee.Core.Settings;
using Filee.Engines.Infrastructure;

namespace Filee.App.ViewModels.Pages;

public sealed partial class EngineItemViewModel(IConverter converter, EngineStatus status, ILocalizer loc) : ObservableObject
{
    /// <summary>Being dragged to a new place in the priority order.</summary>
    [ObservableProperty] private bool _isDragging;

    public IConverter Converter { get; } = converter;
    public bool IsTranslator => Converter.Id == "translator";
    /// <summary>Localized descriptive name, retaining the engine's technical name for unregistered engines.</summary>
    public string Name
    {
        get
        {
            var key = $"engine.{Converter.Id}.name";
            return loc[key] is var text && text != key ? text : Converter.DisplayName;
        }
    }
    public bool IsAvailable { get; } = status.IsAvailable;
    public string StatusText { get; } = status.IsAvailable ? loc["engines.available"] : loc[status.ReasonKey ?? "engines.unavailable"];

    /// <summary>Path of the program or other technical detail, shown as a tooltip.</summary>
    public string? Detail { get; } = status.Detail;

    /// <summary>"Markdig 1.4.0", "0.8.6" — what the engine reports; empty for engines that aren't installed.</summary>
    public string? Version { get; } = status.Version;

    /// <summary>What the engine converts (engine.&lt;id&gt;.description), or null when there is no text for it.</summary>
    public string? Description { get; } = Describe(loc, converter.Id);

    public string Conversions { get; } = loc.Format("engines.conversions", converter.Edges.Count);

    private static string? Describe(ILocalizer loc, string id)
    {
        var key = $"engine.{id}.description";
        var text = loc[key];
        return text == key ? null : text;
    }
}

public sealed partial class EnginesPageViewModel : ObservableObject, IDisposable
{
    private readonly UserDataStore _store;
    private readonly ConverterCatalog _catalog;
    private readonly ILocalizer _loc;
    private readonly EngineDownloadService _downloads;
    private readonly JobQueue? _queue;

    public EnginesPageViewModel(UserDataStore store, ConverterCatalog catalog, ILocalizer loc, EngineDownloadService downloads,
        JobQueue? queue = null)
    {
        _store = store;
        _catalog = catalog;
        _loc = loc;
        _downloads = downloads;
        _queue = queue;
        _libreOfficeForSlides = store.Settings.LibreOfficeForSlides;
        _libreOfficeForHwp = store.Settings.LibreOfficeForHwp;
        _libreOfficeForDocuments = store.Settings.LibreOfficeForDocuments;
        _libreOfficeForSheets = store.Settings.LibreOfficeForSheets;
        _catalog.Changed += OnCatalogChanged;
        Reload();
    }

    /// <summary>Presentations → PDF through LibreOffice when it is installed (App.ApplySettings re-checks the engines).</summary>
    [ObservableProperty] private bool _libreOfficeForSlides;

    /// <summary>HWP / HWPX → PDF through LibreOffice and H2Orestart instead of rhwp.</summary>
    [ObservableProperty] private bool _libreOfficeForHwp;

    /// <summary>Word documents → PDF through LibreOffice instead of the built-in route.</summary>
    [ObservableProperty] private bool _libreOfficeForDocuments;

    /// <summary>Spreadsheets → PDF through LibreOffice instead of the built-in route.</summary>
    [ObservableProperty] private bool _libreOfficeForSheets;

    partial void OnLibreOfficeForSlidesChanged(bool value)
    {
        _store.Settings.LibreOfficeForSlides = value;
        _store.SaveSettings();
    }

    partial void OnLibreOfficeForHwpChanged(bool value)
    {
        _store.Settings.LibreOfficeForHwp = value;
        _store.SaveSettings();
    }

    partial void OnLibreOfficeForDocumentsChanged(bool value)
    {
        _store.Settings.LibreOfficeForDocuments = value;
        _store.SaveSettings();
    }

    partial void OnLibreOfficeForSheetsChanged(bool value)
    {
        _store.Settings.LibreOfficeForSheets = value;
        _store.SaveSettings();
    }

    /// <summary>Optional engines that are downloaded on demand.</summary>
    public IReadOnlyList<EnginePackageState> Packages => _downloads.Packages;

    public ObservableCollection<EngineItemViewModel> Engines { get; } = [];

    // Any number of rows can be clicked; the service queues them and installs one at a time (#819).
    [RelayCommand(AllowConcurrentExecutions = true)]
    private Task Install(EnginePackageState package) => _downloads.InstallAsync(package);

    [RelayCommand]
    private void Cancel(EnginePackageState package) => _downloads.Cancel(package);

    /// <summary>Removes a package (after the view asked for confirmation). False when its files are in use.</summary>
    public bool Remove(EnginePackageState package) => _downloads.Uninstall(package);

    /// <summary>
    /// Uses the user's own copy of a package from <paramref name="path"/> (a program they picked). Returns false when
    /// that folder doesn't have the package's programs; nothing is saved then.
    /// </summary>
    public bool UseOwnCopy(EnginePackageState package, string path)
    {
        if (EngineEnvironment.FolderWithPrograms(package.Package.Id, path) is null)
            return false;
        _store.Settings.EngineOwnCopies[package.Package.Id] = path;
        _store.SaveSettings(); // App.ApplySettings re-checks the engines
        return true;
    }

    /// <summary>Goes back to Filee's download (or no engine) for <paramref name="package"/>.</summary>
    public void StopUsingOwnCopy(EnginePackageState package)
    {
        if (_store.Settings.EngineOwnCopies.Remove(package.Package.Id))
            _store.SaveSettings();
    }

    /// <summary>Where the optional engines are downloaded to (Settings → Engines).</summary>
    public string EngineFolder => EngineEnvironment.DownloadRoot;

    /// <summary>The user chose the engine folder (it isn't the default).</summary>
    public bool HasCustomEngineFolder => !string.IsNullOrWhiteSpace(_store.Settings.EngineFolder);

    /// <summary>The engines are being moved to another folder.</summary>
    [ObservableProperty] private bool _isMovingEngines;

    /// <summary>Progress of the move, 0 to 100.</summary>
    [ObservableProperty] private double _moveProgress;

    /// <summary>
    /// Why the engines can't move to <paramref name="target"/> (null for the default folder), as a text for the user;
    /// null when they can.
    /// </summary>
    public string? CheckEngineFolder(string? target)
    {
        if (_downloads.IsBusy || _queue?.IsBusy == true || IsMovingEngines)
            return _loc["engines.folder_busy"];
        return EngineFolderCheckOf(target) switch
        {
            EngineFolderCheck.Nested => _loc["engines.folder_nested"],
            EngineFolderCheck.NotEmpty => _loc["engines.folder_not_empty"],
            EngineFolderCheck.Invalid => _loc["engines.folder_invalid"],
            _ => null,
        };
    }

    /// <summary>True when <paramref name="target"/> is the folder the engines are in already (nothing to move).</summary>
    public bool IsCurrentEngineFolder(string? target) => EngineFolderCheckOf(target) == EngineFolderCheck.Same;

    private static EngineFolderCheck EngineFolderCheckOf(string? target) =>
        Filee.Engines.Infrastructure.EngineFolder.Check(EngineEnvironment.DownloadRoot, EngineEnvironment.ResolveDownloadRoot(target));

    /// <summary>
    /// Moves the engines to <paramref name="target"/> (null: back to the default folder) and saves it as the engine
    /// folder; App.ApplySettings then points the engines there. Returns the error to show, or null when it worked.
    /// </summary>
    public async Task<string?> ChangeEngineFolderAsync(string? target)
    {
        if (CheckEngineFolder(target) is { } problem)
            return problem;
        var folder = EngineEnvironment.ResolveDownloadRoot(target);
        IsMovingEngines = true;
        MoveProgress = 0;
        try
        {
            if (!IsCurrentEngineFolder(target))
            {
                var progress = new Progress<double>(p => MoveProgress = p * 100);
                await Filee.Engines.Infrastructure.EngineFolder.MoveAsync(EngineEnvironment.DownloadRoot, folder, progress, CancellationToken.None);
            }
            _store.Settings.EngineFolder = target is null ? null : folder;
            _store.SaveSettings(); // App.ApplySettings sets EngineEnvironment.DownloadRoot and re-checks the engines
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return _loc.Format("engines.folder_failed", ex.Message);
        }
        finally
        {
            IsMovingEngines = false;
            OnPropertyChanged(nameof(EngineFolder));
            OnPropertyChanged(nameof(HasCustomEngineFolder));
        }
    }

    // Installing or removing an engine changes the list below (status, supported conversions).
    private void OnCatalogChanged(object? sender, EventArgs e) => Dispatcher.UIThread.Post(Reload);

    public void Dispose() => _catalog.Changed -= OnCatalogChanged;

    private void Reload()
    {
        Engines.Clear();
        foreach (var converter in _catalog.Ordered)
            Engines.Add(new EngineItemViewModel(converter, _catalog.StatusOf(converter), _loc));
    }

    [RelayCommand]
    private void MoveUp(EngineItemViewModel item) => Move(item, -1);

    [RelayCommand]
    private void MoveDown(EngineItemViewModel item) => Move(item, +1);

    [RelayCommand]
    private void Recheck()
    {
        EngineEnvironment.ForgetSystemCopies(); // a tool installed meanwhile is found
        _downloads.RefreshStatus();
        Filee.Engines.Translation.TranslatorConverter.PerformCheck();
        _catalog.Refresh();
    }

    private void Move(EngineItemViewModel item, int delta)
    {
        if (MoveTo(item, Engines.IndexOf(item) + delta))
            SavePriority();
    }

    /// <summary>Moves <paramref name="item"/> to <paramref name="index"/> in the list (while dragging); false when it stays.</summary>
    public bool MoveTo(EngineItemViewModel item, int index)
    {
        var from = Engines.IndexOf(item);
        if (from < 0 || index < 0 || index >= Engines.Count || index == from)
            return false;
        Engines.Move(from, index);
        return true;
    }

    /// <summary>Saves the order of the list as the engine priority.</summary>
    public void SavePriority()
    {
        _store.Settings.EnginePriority = Engines.Select(e => e.Converter.Id).ToList();
        _store.SaveSettings();
    }
}
