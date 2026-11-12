using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Path = Avalonia.Controls.Shapes.Path;

namespace PortHub;

public sealed partial class MainWindow : Window
{
    private readonly GitHubApi _github = new();
    private readonly List<SavedRepository> _repositories = new();
    private readonly List<DownloadRecord> _downloads = new();
    private readonly Dictionary<string, RepositoryOverview> _overview = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _syncErrors = new(StringComparer.OrdinalIgnoreCase);
    private readonly ObservableCollection<RepositoryCard> _cards = new();
    private readonly ObservableCollection<DownloadRow> _downloadRows = new();
    private readonly ObservableCollection<UpdateRow> _updateRows = new();
    private readonly ObservableCollection<AssetRow> _assetRows = new();
    private readonly DispatcherTimer _noticeTimer = new() { Interval = TimeSpan.FromSeconds(4) };
    private CancellationTokenSource? _activeDownload;
    private bool _isRefreshing;
    private bool _updatingSearch;
    private string _query = "";
    private string? _selectedRepository;
    // %APPDATA% is the roaming application-data folder on Windows.
    private static readonly string DefaultDownloadDirectory = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "porthub", "apps");
    private static readonly string PreviousDefaultDownloadDirectory = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "PortHub");
    private string _downloadDirectory = DefaultDownloadDirectory;

    private readonly Border _surface;
    private readonly Grid _resizeLayer;
    private readonly ExperimentalAcrylicBorder _glassLayer;
    private readonly Path _maximizeGlyph;
    private readonly Button _homeNav;
    private readonly Button _libraryNav;
    private readonly Button _downloadsNav;
    private readonly Button _updatesNav;
    private readonly Button _settingsNav;
    private readonly ScrollViewer _homePage;
    private readonly ScrollViewer _libraryPage;
    private readonly ScrollViewer _downloadsPage;
    private readonly ScrollViewer _updatesPage;
    private readonly ScrollViewer _settingsPage;
    private readonly Border _searchBox;
    private readonly Grid _heroLayout;
    private readonly Canvas _heroArt;
    private readonly ItemsControl _homeCards;
    private readonly ItemsControl _libraryCards;
    private readonly ItemsControl _downloadItems;
    private readonly ItemsControl _updateItems;
    private readonly ItemsControl _releaseAssets;
    private readonly TextBox _searchInput;
    private readonly TextBox _librarySearchInput;
    private readonly TextBox _repoUrlInput;
    private readonly TextBox _downloadFolderInput;
    private readonly TextBlock _repoErrorText;
    private readonly TextBlock _libraryCountText;
    private readonly TextBlock _downloadCountText;
    private readonly TextBlock _updateCountText;
    private readonly TextBlock _noResultsText;
    private readonly TextBlock _libraryEmptyText;
    private readonly TextBlock _syncStatusText;
    private readonly TextBlock _downloadStatusText;
    private readonly TextBlock _downloadsEmptyText;
    private readonly TextBlock _updatesEmptyText;
    private readonly TextBlock _releaseTitle;
    private readonly TextBlock _releaseSubtitle;
    private readonly TextBlock _releaseEmptyText;
    private readonly ProgressBar _downloadProgressBar;
    private readonly Button _cancelDownloadButton;
    private readonly Button _confirmAddButton;
    private readonly Border _addRepoOverlay;
    private readonly Border _releaseOverlay;
    private readonly Border _noticeSurface;
    private readonly TextBlock _noticeText;
    private Button _activeNavigationButton;

    private static readonly string StateDirectory = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PortHub");
    private static readonly string SavedRepositoriesFile = System.IO.Path.Combine(StateDirectory, "repositories.json");
    private static readonly string DownloadsFile = System.IO.Path.Combine(StateDirectory, "downloads.json");
    private static readonly string SettingsFile = System.IO.Path.Combine(StateDirectory, "settings.json");

    private const int DwmWindowCornerPreference = 33;
    private const int DwmBorderColor = 34;
    private const int DwmCornerRound = 2;
    private const int DwmCornerDoNotRound = 1;
    private const int DwmColorNone = unchecked((int)0xFFFFFFFE);

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int valueSize);

    public MainWindow()
    {
        InitializeComponent();

        _surface = Get<Border>("WindowSurface");
        _resizeLayer = Get<Grid>("ResizeLayer");
        _glassLayer = Get<ExperimentalAcrylicBorder>("GlassLayer");
        _maximizeGlyph = Get<Path>("MaximizeGlyph");
        _homeNav = Get<Button>("HomeNavButton");
        _libraryNav = Get<Button>("LibraryNavButton");
        _downloadsNav = Get<Button>("DownloadsNavButton");
        _updatesNav = Get<Button>("UpdatesNavButton");
        _settingsNav = Get<Button>("SettingsNavButton");
        _homePage = Get<ScrollViewer>("HomePage");
        _libraryPage = Get<ScrollViewer>("LibraryPage");
        _downloadsPage = Get<ScrollViewer>("DownloadsPage");
        _updatesPage = Get<ScrollViewer>("UpdatesPage");
        _settingsPage = Get<ScrollViewer>("SettingsPage");
        _searchBox = Get<Border>("SearchBox");
        _heroLayout = Get<Grid>("HeroLayout");
        _heroArt = Get<Canvas>("HeroArt");
        _homeCards = Get<ItemsControl>("HomeCards");
        _libraryCards = Get<ItemsControl>("LibraryCards");
        _downloadItems = Get<ItemsControl>("DownloadItems");
        _updateItems = Get<ItemsControl>("UpdateItems");
        _releaseAssets = Get<ItemsControl>("ReleaseAssets");
        _searchInput = Get<TextBox>("SearchInput");
        _librarySearchInput = Get<TextBox>("LibrarySearchInput");
        _repoUrlInput = Get<TextBox>("RepoUrlInput");
        _downloadFolderInput = Get<TextBox>("DownloadFolderInput");
        _repoErrorText = Get<TextBlock>("RepoErrorText");
        _libraryCountText = Get<TextBlock>("LibraryCountText");
        _downloadCountText = Get<TextBlock>("DownloadCountText");
        _updateCountText = Get<TextBlock>("UpdateCountText");
        _noResultsText = Get<TextBlock>("NoResultsText");
        _libraryEmptyText = Get<TextBlock>("LibraryEmptyText");
        _syncStatusText = Get<TextBlock>("SyncStatusText");
        _downloadStatusText = Get<TextBlock>("DownloadStatusText");
        _downloadsEmptyText = Get<TextBlock>("DownloadsEmptyText");
        _updatesEmptyText = Get<TextBlock>("UpdatesEmptyText");
        _releaseTitle = Get<TextBlock>("ReleaseTitleText");
        _releaseSubtitle = Get<TextBlock>("ReleaseSubtitleText");
        _releaseEmptyText = Get<TextBlock>("ReleaseEmptyText");
        _downloadProgressBar = Get<ProgressBar>("DownloadProgressBar");
        _cancelDownloadButton = Get<Button>("CancelDownloadButton");
        _confirmAddButton = Get<Button>("ConfirmAddButton");
        _addRepoOverlay = Get<Border>("AddRepoOverlay");
        _releaseOverlay = Get<Border>("ReleaseOverlay");
        _noticeSurface = Get<Border>("NoticeSurface");
        _noticeText = Get<TextBlock>("NoticeText");
        _activeNavigationButton = _homeNav;

        _homeCards.ItemsSource = _cards;
        _libraryCards.ItemsSource = _cards;
        _downloadItems.ItemsSource = _downloadRows;
        _updateItems.ItemsSource = _updateRows;
        _releaseAssets.ItemsSource = _assetRows;
        _homePage.SizeChanged += (_, _) => UpdateResponsiveHome();

        LoadState();
        try { Directory.CreateDirectory(_downloadDirectory); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // A failed folder creation must not prevent the application from opening.
            // Download actions will report any later I/O failure to the user.
        }
        _downloadFolderInput.Text = _downloadDirectory;
        UpdateContent();
        KeyDown += HandleWindowKeyDown;
        _noticeTimer.Tick += (_, _) =>
        {
            _noticeTimer.Stop();
            _noticeSurface.IsVisible = false;
        };
        PropertyChanged += (_, change) =>
        {
            if (change.Property == WindowStateProperty)
                UpdateWindowStateAppearance();
        };
        UpdateWindowStateAppearance();
    }

    private T Get<T>(string name) where T : Control =>
        this.FindControl<T>(name)
        ?? throw new InvalidOperationException($"Не найден элемент интерфейса: {name}");

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        ApplyNativeWindowAppearance();
        UpdateResponsiveHome();
        if (_repositories.Count > 0)
            _ = RefreshAllRepositoriesAsync();
    }

    private void UpdateResponsiveHome()
    {
        double width = _homePage.Bounds.Width;
        if (width <= 0) return;
        bool compact = width < 740;
        _searchBox.Width = compact ? 178 : 244;
        _heroArt.IsVisible = !compact;
        _heroLayout.ColumnDefinitions[1].Width = new GridLength(compact ? 0 : 220);
        if (_homeCards.ItemsPanelRoot is UniformGrid homeGrid)
            homeGrid.Columns = compact ? 2 : 3;
        if (_libraryCards.ItemsPanelRoot is UniformGrid libraryGrid)
            libraryGrid.Columns = compact ? 2 : 3;
    }

    private void Navigation_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button button) SetPage(button);
    }

    private void SetPage(Button selected)
    {
        _activeNavigationButton.Classes.Remove("active");
        selected.Classes.Add("active");
        _activeNavigationButton = selected;
        _homePage.IsVisible = ReferenceEquals(selected, _homeNav);
        _libraryPage.IsVisible = ReferenceEquals(selected, _libraryNav);
        _downloadsPage.IsVisible = ReferenceEquals(selected, _downloadsNav);
        _updatesPage.IsVisible = ReferenceEquals(selected, _updatesNav);
        _settingsPage.IsVisible = ReferenceEquals(selected, _settingsNav);
        _noticeSurface.IsVisible = false;
        if (ReferenceEquals(selected, _libraryNav))
            Dispatcher.UIThread.Post(UpdateResponsiveHome);
    }

    private void OpenLibrary_Click(object? sender, RoutedEventArgs e) => SetPage(_libraryNav);
    private void OpenSettings_Click(object? sender, RoutedEventArgs e) => SetPage(_settingsNav);

    private void SearchInput_TextChanged(object? sender, TextChangedEventArgs e)
    {
        if (_updatingSearch || sender is not TextBox source ||
            _searchInput is null || _librarySearchInput is null) return;
        _updatingSearch = true;
        _query = source.Text?.Trim() ?? "";
        if (!ReferenceEquals(source, _searchInput)) _searchInput.Text = source.Text;
        if (!ReferenceEquals(source, _librarySearchInput)) _librarySearchInput.Text = source.Text;
        _updatingSearch = false;
        UpdateContent();
    }

    private static string RepositoryKey(string owner, string name) => $"{owner}/{name}";
    private string? LatestDownloadedTag(string key) => _downloads
        .Where(d => d.RepositoryKey.Equals(key, StringComparison.OrdinalIgnoreCase) && File.Exists(d.FilePath))
        .OrderByDescending(d => d.DownloadedAt).Select(d => d.TagName).FirstOrDefault();

    private bool HasUpdate(string key, GitHubRelease release)
    {
        // Compare against the most recently downloaded release, not any older archive.
        DownloadRecord? latest = _downloads
            .Where(d => d.RepositoryKey.Equals(key, StringComparison.OrdinalIgnoreCase)
                && File.Exists(d.FilePath))
            .OrderByDescending(d => d.DownloadedAt)
            .FirstOrDefault();
        return latest is not null && latest.ReleaseId != release.Id;
    }

    private void UpdateContent()
    {
        if (_homeCards is null) return;
        var displayed = new List<RepositoryCard>();
        foreach (SavedRepository repo in _repositories)
        {
            string key = RepositoryKey(repo.Owner, repo.Name);
            if (!key.Contains(_query, StringComparison.OrdinalIgnoreCase))
            {
                if (!_overview.TryGetValue(key, out var match) ||
                    !match.Repository.Description.Contains(_query, StringComparison.OrdinalIgnoreCase))
                    continue;
            }
            bool loaded = _overview.TryGetValue(key, out var info);
            bool failed = _syncErrors.TryGetValue(key, out var error);
            GitHubRelease? release = loaded ? info!.LatestRelease : null;
            string badge = failed ? "ОШИБКА СИНХРОНИЗАЦИИ" : !loaded ? "ЗАГРУЗКА" :
                release is null ? "БЕЗ РЕЛИЗОВ" : $"РЕЛИЗ · {release.TagName}";
            string description = failed ? error! : !loaded ? "Получение данных GitHub…" :
                string.IsNullOrWhiteSpace(info!.Repository.Description)
                    ? "Описание отсутствует в GitHub." : info.Repository.Description;
            displayed.Add(new RepositoryCard(info?.Repository.Name ?? repo.Name,
                info?.Repository.FullName ?? key, description,
                (info?.Repository.Name ?? repo.Name)[..1].ToUpperInvariant(), badge,
                release is { Assets.Count: > 0 } ? "Скачать" : "GitHub", key));
        }
        _cards.Clear();
        foreach (var card in displayed) _cards.Add(card);
        _libraryCountText.Text = _repositories.Count.ToString();
        _downloadCountText.Text = _downloads.Count(d => File.Exists(d.FilePath)).ToString();
        int updateCount = _repositories.Count(repo =>
        {
            string key = RepositoryKey(repo.Owner, repo.Name);
            return _overview.TryGetValue(key, out var info)
                && info.LatestRelease is not null && HasUpdate(key, info.LatestRelease);
        });
        _updateCountText.Text = _syncErrors.Count > 0 ? "?" : updateCount.ToString();
        _noResultsText.Text = _repositories.Count == 0
            ? "Библиотека пуста. Добавь ссылку на публичный репозиторий GitHub."
            : "По запросу ничего не найдено.";
        _noResultsText.IsVisible = _cards.Count == 0;
        _libraryEmptyText.Text = _noResultsText.Text;
        _libraryEmptyText.IsVisible = _cards.Count == 0;
        UpdateDownloads();
        UpdateUpdates();
    }

    private async Task RefreshAllRepositoriesAsync()
    {
        if (_isRefreshing) return;
        _isRefreshing = true;
        _syncStatusText.Text = "Синхронизация с GitHub…";
        foreach (SavedRepository repo in _repositories.ToArray())
        {
            string key = RepositoryKey(repo.Owner, repo.Name);
            try
            {
                var info = await _github.GetOverviewAsync(repo.Owner, repo.Name);
                _overview[key] = info;
                _syncErrors.Remove(key);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException)
            {
                _overview.Remove(key);
                _syncErrors[key] = ex.Message;
            }
            UpdateContent();
        }
        _isRefreshing = false;
        _syncStatusText.Text = _syncErrors.Count == 0 ? "Синхронизация завершена" :
            $"Не удалось обновить: {_syncErrors.Count}";
        UpdateContent();
    }

    private async void RefreshRepositories_Click(object? sender, RoutedEventArgs e) =>
        await RefreshAllRepositoriesAsync();

    private void ShowAddRepository_Click(object? sender, RoutedEventArgs e)
    {
        _repoUrlInput.Text = "";
        _repoErrorText.Text = "";
        _repoErrorText.IsVisible = false;
        _addRepoOverlay.IsVisible = true;
        _repoUrlInput.Focus();
    }

    private void CancelAddRepository_Click(object? sender, RoutedEventArgs e)
    {
        if (_confirmAddButton.IsEnabled) _addRepoOverlay.IsVisible = false;
    }

    private async void ConfirmAddRepository_Click(object? sender, RoutedEventArgs e) =>
        await ConfirmAddRepositoryAsync();

    private async Task ConfirmAddRepositoryAsync()
    {
        if (!_confirmAddButton.IsEnabled) return;
        if (!TryReadGitHubRepository(_repoUrlInput.Text, out var repo))
        {
            ShowRepoError("Введите прямую ссылку https://github.com/владелец/репозиторий");
            return;
        }
        string key = RepositoryKey(repo.Owner, repo.Name);
        if (_repositories.Any(x => RepositoryKey(x.Owner, x.Name).Equals(key, StringComparison.OrdinalIgnoreCase)))
        {
            ShowRepoError("Репозиторий уже добавлен.");
            return;
        }
        _confirmAddButton.IsEnabled = false;
        ShowRepoError("Проверяю репозиторий через GitHub…");
        try
        {
            RepositoryOverview info = await _github.GetOverviewAsync(repo.Owner, repo.Name);
            var saved = new SavedRepository(info.Repository.Owner, info.Repository.Name);
            string actualKey = RepositoryKey(saved.Owner, saved.Name);
            if (_repositories.Any(x => RepositoryKey(x.Owner, x.Name).Equals(actualKey, StringComparison.OrdinalIgnoreCase)))
            {
                ShowRepoError("Репозиторий уже добавлен.");
                return;
            }
            _repositories.Add(saved);
            if (!SaveJson(SavedRepositoriesFile, _repositories))
            {
                _repositories.Remove(saved);
                ShowRepoError("Не получилось сохранить библиотеку в AppData.");
                return;
            }
            _overview[actualKey] = info;
            _syncErrors.Remove(actualKey);
            _addRepoOverlay.IsVisible = false;
            _query = "";
            _searchInput.Text = "";
            _librarySearchInput.Text = "";
            SetPage(_libraryNav);
            UpdateContent();
            ShowNotice("Репозиторий проверен и добавлен в библиотеку.");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException)
        {
            ShowRepoError(ex.Message);
        }
        finally { _confirmAddButton.IsEnabled = true; }
    }

    private void ShowRepoError(string message)
    {
        _repoErrorText.Text = message;
        _repoErrorText.IsVisible = true;
    }

    private void RepositoryCard_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string key }) return;
        if (_overview.TryGetValue(key, out var info) && info.LatestRelease is { Assets.Count: > 0 })
            ShowRelease(key);
        else
            OpenUrl(_overview.TryGetValue(key, out var repo) ? repo.Repository.HtmlUrl.ToString() : $"https://github.com/{key}");
    }

    private void ShowRelease(string key)
    {
        if (!_overview.TryGetValue(key, out var info) || info.LatestRelease is null) return;
        _selectedRepository = key;
        _releaseTitle.Text = $"{info.Repository.Name} · {info.LatestRelease.TagName}";
        _releaseSubtitle.Text = $"Последний опубликованный стабильный релиз · {info.LatestRelease.Assets.Count} файлов";
        _assetRows.Clear();
        foreach (ReleaseAsset asset in info.LatestRelease.Assets)
            _assetRows.Add(new AssetRow(asset, asset.Name, FormatBytes(asset.Size)));
        _releaseEmptyText.IsVisible = _assetRows.Count == 0;
        _releaseOverlay.IsVisible = true;
    }

    private void CloseRelease_Click(object? sender, RoutedEventArgs e) => _releaseOverlay.IsVisible = false;

    private void OpenReleasePage_Click(object? sender, RoutedEventArgs e)
    {
        if (_selectedRepository is not null && _overview.TryGetValue(_selectedRepository, out var info))
            OpenUrl(info.LatestRelease?.HtmlUrl.ToString() ?? info.Repository.HtmlUrl.ToString());
    }

    private async void DownloadAsset_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: ReleaseAsset asset } || _selectedRepository is null) return;
        if (_activeDownload is not null)
        {
            ShowNotice("Уже выполняется загрузка. Дождись её завершения или отмени.");
            return;
        }
        if (!_overview.TryGetValue(_selectedRepository, out var overview) || overview.LatestRelease is null)
            return;
        _releaseOverlay.IsVisible = false;
        SetPage(_downloadsNav);
        await DownloadAssetAsync(_selectedRepository, overview.LatestRelease, asset);
    }

    private async Task DownloadAssetAsync(string key, GitHubRelease release, ReleaseAsset asset)
    {
        if (!IsValidFilename(asset.Name) || asset.DownloadUrl.Scheme != Uri.UriSchemeHttps ||
            !asset.DownloadUrl.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase))
        {
            ShowNotice("GitHub вернул небезопасное имя или адрес файла. Загрузка отменена.");
            return;
        }
        string directory = System.IO.Path.Combine(_downloadDirectory,
            SafeSegment(key.Split('/')[0]), SafeSegment(key.Split('/')[1]), SafeSegment(release.TagName));
        string destination = System.IO.Path.Combine(directory, asset.Name);
        string partial = destination + "." + Guid.NewGuid().ToString("N") + ".part";
        _activeDownload = new CancellationTokenSource();
        _cancelDownloadButton.IsVisible = true;
        _downloadProgressBar.IsVisible = true;
        _downloadProgressBar.Value = 0;
        _downloadStatusText.Text = $"Загрузка: {asset.Name}";
        try
        {
            Directory.CreateDirectory(directory);
            using var response = await _github.Downloads.GetAsync(asset.DownloadUrl,
                HttpCompletionOption.ResponseHeadersRead, _activeDownload.Token);
            response.EnsureSuccessStatusCode();
            long total = response.Content.Headers.ContentLength ?? asset.Size;
            await using (var input = await response.Content.ReadAsStreamAsync(_activeDownload.Token))
            await using (var output = new FileStream(partial, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 81920, true))
            {
                var buffer = new byte[81920];
                long completed = 0;
                int read;
                while ((read = await input.ReadAsync(buffer, _activeDownload.Token)) > 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, read), _activeDownload.Token);
                    completed += read;
                    if (total > 0)
                    {
                        double percentage = Math.Min(100, completed * 100.0 / total);
                        _downloadProgressBar.Value = percentage;
                        _downloadStatusText.Text = $"{asset.Name} · {percentage:0}% ({FormatBytes(completed)} / {FormatBytes(total)})";
                    }
                    else
                        _downloadStatusText.Text = $"{asset.Name} · {FormatBytes(completed)}";
                }
            }
            if (asset.Size > 0 && new FileInfo(partial).Length != asset.Size)
                throw new IOException("Размер файла не совпадает с размером, указанным в GitHub Releases.");
            File.Move(partial, destination, true);
            _downloads.RemoveAll(d => d.RepositoryKey.Equals(key, StringComparison.OrdinalIgnoreCase)
                && d.FilePath.Equals(destination, StringComparison.OrdinalIgnoreCase));
            _downloads.Add(new DownloadRecord(key, release.Id, release.TagName,
                asset.Name, destination, DateTimeOffset.Now));
            if (!SaveJson(DownloadsFile, _downloads))
                ShowNotice("Файл скачан, но не удалось сохранить историю загрузок.");
            _downloadStatusText.Text = $"Скачано: {asset.Name}";
            ShowNotice("Файл загружен. Открыть его папку можно в разделе «Загрузки».");
        }
        catch (OperationCanceledException)
        {
            _downloadStatusText.Text = "Загрузка отменена.";
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException)
        {
            _downloadStatusText.Text = $"Ошибка загрузки: {ex.Message}";
        }
        finally
        {
            try { if (File.Exists(partial)) File.Delete(partial); } catch (IOException) { }
            _activeDownload.Dispose();
            _activeDownload = null;
            _cancelDownloadButton.IsVisible = false;
            _downloadProgressBar.IsVisible = false;
            UpdateContent();
        }
    }

    private void CancelDownload_Click(object? sender, RoutedEventArgs e) => _activeDownload?.Cancel();

    private static bool IsValidFilename(string filename) =>
        !string.IsNullOrWhiteSpace(filename) && filename is not "." and not ".."
        && !filename.Any(ch => char.IsControl(ch) || System.IO.Path.GetInvalidFileNameChars().Contains(ch))
        && !filename.Contains('/') && !filename.Contains('\\')
        && filename.Length <= 180;

    private static string SafeSegment(string value)
    {
        string clean = new string(value.Where(ch => char.IsLetterOrDigit(ch) || ch is '.' or '_' or '-').ToArray());
        return string.IsNullOrWhiteSpace(clean) || clean is "." or ".." ? "release" : clean[..Math.Min(clean.Length, 100)];
    }

    private static string FormatBytes(long value) => value >= 1024L * 1024 * 1024
        ? $"{value / (1024.0 * 1024 * 1024):0.0} ГБ" : value >= 1024 * 1024
        ? $"{value / (1024.0 * 1024):0.0} МБ" : $"{value / 1024.0:0.0} КБ";

    private void UpdateDownloads()
    {
        _downloadRows.Clear();
        foreach (var item in _downloads.Where(d => File.Exists(d.FilePath)).OrderByDescending(d => d.DownloadedAt))
            _downloadRows.Add(new DownloadRow(item.FileName,
                $"{item.RepositoryKey} · {item.TagName} · {item.DownloadedAt:dd.MM.yyyy HH:mm}", item.FilePath));
        _downloadsEmptyText.IsVisible = _downloadRows.Count == 0;
    }

    private void DownloadRow_OpenFolder(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string filePath } && File.Exists(filePath))
        {
            try
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{filePath}\"") { UseShellExecute = true });
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                ShowNotice("Не удалось открыть папку с файлом.");
            }
        }
        else ShowNotice("Файл уже удалён или перемещён.");
    }

    private void UpdateUpdates()
    {
        _updateRows.Clear();
        foreach (SavedRepository repo in _repositories)
        {
            string key = RepositoryKey(repo.Owner, repo.Name);
            if (_overview.TryGetValue(key, out var info) && info.LatestRelease is { } release && HasUpdate(key, release))
                _updateRows.Add(new UpdateRow(key, info.Repository.Name,
                    $"Скачана версия {LatestDownloadedTag(key)} · доступна {release.TagName}"));
        }
        _updatesEmptyText.Text = _syncErrors.Count > 0
            ? "Не все репозитории удалось проверить. Повтори синхронизацию."
            : "Новых версий для ранее скачанных файлов не обнаружено.";
        _updatesEmptyText.IsVisible = _updateRows.Count == 0;
    }

    private void UpdateRow_OpenRelease(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string key }) ShowRelease(key);
    }

    private void LibraryRemove_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string key }) return;
        SavedRepository? removed = _repositories.FirstOrDefault(r =>
            RepositoryKey(r.Owner, r.Name).Equals(key, StringComparison.OrdinalIgnoreCase));
        if (removed is null) return;
        _repositories.Remove(removed);
        if (!SaveJson(SavedRepositoriesFile, _repositories))
        {
            _repositories.Add(removed);
            ShowNotice("Не удалось обновить файл библиотеки.");
            return;
        }
        _overview.Remove(key);
        _syncErrors.Remove(key);
        UpdateContent();
        ShowNotice("Репозиторий убран из библиотеки. Скачанные файлы не удалены.");
    }

    private async void PickDownloadFolder_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            var folders = await StorageProvider.OpenFolderPickerAsync(
                new FolderPickerOpenOptions { Title = "Папка для загрузок PortHub", AllowMultiple = false });
            if (folders.Count > 0 && folders[0].Path.IsFile)
                _downloadFolderInput.Text = folders[0].Path.LocalPath;
        }
        catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException)
        {
            ShowNotice("Системный выбор папки сейчас недоступен.");
        }
    }

    private void SaveSettings_Click(object? sender, RoutedEventArgs e)
    {
        string target = _downloadFolderInput.Text?.Trim() ?? "";
        try
        {
            if (!System.IO.Path.IsPathFullyQualified(target))
            {
                ShowNotice("Укажи полный путь к папке загрузок.");
                return;
            }
            Directory.CreateDirectory(target);
            if (!SaveJson(SettingsFile, new AppSettings(target)))
            {
                ShowNotice("Не удалось сохранить настройки.");
                return;
            }
            _downloadDirectory = target;
            ShowNotice("Папка загрузок сохранена.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            ShowNotice($"Не удалось использовать папку: {ex.Message}");
        }
    }

    private void OpenDownloadsFolder_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(_downloadDirectory);
            Process.Start(new ProcessStartInfo("explorer.exe", _downloadDirectory) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            ShowNotice("Не удалось открыть папку загрузок.");
        }
    }

    private void ShowNotice(string message)
    {
        _noticeText.Text = message;
        _noticeSurface.IsVisible = true;
        _noticeTimer.Stop();
        _noticeTimer.Start();
    }

    private void OpenUrl(string address)
    {
        try { Process.Start(new ProcessStartInfo(address) { UseShellExecute = true }); }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        { ShowNotice("Не удалось открыть ссылку в браузере."); }
    }

    private void HandleWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            if (_addRepoOverlay.IsVisible) _addRepoOverlay.IsVisible = false;
            else if (_releaseOverlay.IsVisible) _releaseOverlay.IsVisible = false;
            else return;
            e.Handled = true;
        }
        else if (e.Key == Key.K && e.KeyModifiers.HasFlag(KeyModifiers.Control)
                 && (_homePage.IsVisible || _libraryPage.IsVisible))
        {
            var input = _libraryPage.IsVisible ? _librarySearchInput : _searchInput;
            input.Focus();
            input.SelectAll();
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && _addRepoOverlay.IsVisible && _repoUrlInput.IsFocused)
        {
            _ = ConfirmAddRepositoryAsync();
            e.Handled = true;
        }
    }

    private static bool TryReadGitHubRepository(string? raw, out SavedRepository repo)
    {
        repo = new SavedRepository("", "");
        if (!Uri.TryCreate(raw?.Trim(), UriKind.Absolute, out Uri? uri)
            || uri.Scheme != Uri.UriSchemeHttps
            || !uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
            || !uri.IsDefaultPort || uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0)
            return false;
        string[] segments = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length != 2) return false;
        string name = segments[1].EndsWith(".git", StringComparison.OrdinalIgnoreCase)
            ? segments[1][..^4] : segments[1];
        if (!Regex.IsMatch(segments[0], @"^[a-zA-Z0-9][a-zA-Z0-9-]{0,38}$")
            || !Regex.IsMatch(name, @"^[a-zA-Z0-9_.-]{1,100}$") || name is "." or "..")
            return false;
        repo = new SavedRepository(segments[0], name);
        return true;
    }

    private void LoadState()
    {
        try
        {
            if (File.Exists(SavedRepositoriesFile))
            {
                var saved = JsonSerializer.Deserialize<List<SavedRepository>>(File.ReadAllText(SavedRepositoriesFile));
                if (saved is not null)
                {
                    foreach (var repo in saved)
                        if (TryReadGitHubRepository($"https://github.com/{repo.Owner}/{repo.Name}", out var clean)
                            && !_repositories.Any(r => RepositoryKey(r.Owner, r.Name).Equals(RepositoryKey(clean.Owner, clean.Name), StringComparison.OrdinalIgnoreCase)))
                            _repositories.Add(clean);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
        try
        {
            if (File.Exists(DownloadsFile))
            {
                var saved = JsonSerializer.Deserialize<List<DownloadRecord>>(File.ReadAllText(DownloadsFile)) ?? [];
                _downloads.AddRange(saved.Where(d =>
                    !string.IsNullOrWhiteSpace(d.RepositoryKey) &&
                    !string.IsNullOrWhiteSpace(d.FilePath) &&
                    !string.IsNullOrWhiteSpace(d.FileName) &&
                    !string.IsNullOrWhiteSpace(d.TagName)));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
        try
        {
            if (File.Exists(SettingsFile))
            {
                var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsFile));
                if (settings is not null && !string.IsNullOrWhiteSpace(settings.DownloadDirectory)
                    && System.IO.Path.IsPathFullyQualified(settings.DownloadDirectory))
                {
                    // Only migrate the application's former built-in default.
                    // Never override a folder explicitly chosen by the user.
                    var storedPath = System.IO.Path.TrimEndingDirectorySeparator(settings.DownloadDirectory);
                    var formerDefault = System.IO.Path.TrimEndingDirectorySeparator(PreviousDefaultDownloadDirectory);
                    if (!string.Equals(storedPath, formerDefault, StringComparison.OrdinalIgnoreCase))
                        _downloadDirectory = settings.DownloadDirectory;
                    else
                        SaveJson(SettingsFile, new AppSettings(DefaultDownloadDirectory));
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException) { }
    }

    private static bool SaveJson<T>(string file, T value)
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(file)!);
            string temp = file + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temp, file, true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    private void Minimize_Click(object? sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void Maximize_Click(object? sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void Close_Click(object? sender, RoutedEventArgs e) => Close();

    private void UpdateWindowStateAppearance()
    {
        bool maximized = WindowState == WindowState.Maximized;
        _surface.CornerRadius = maximized ? new CornerRadius(0) : new CornerRadius(18);
        _glassLayer.CornerRadius = maximized ? new CornerRadius(0) : new CornerRadius(18);
        _resizeLayer.IsVisible = !maximized;
        _maximizeGlyph.Data = Geometry.Parse(maximized
            ? "M 6 4 L 14 4 L 14 12 M 4 6 L 12 6 L 12 14 L 4 14 Z"
            : "M 4.5 4.5 L 13.5 4.5 L 13.5 13.5 L 4.5 13.5 Z");
        ApplyNativeWindowAppearance();
    }

    private void ApplyNativeWindowAppearance()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000)) return;
        IntPtr hwnd = TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        if (hwnd == IntPtr.Zero) return;
        int corner = WindowState == WindowState.Maximized ? DwmCornerDoNotRound : DwmCornerRound;
        int noBorder = DwmColorNone;
        try
        {
            DwmSetWindowAttribute(hwnd, DwmWindowCornerPreference, ref corner, sizeof(int));
            DwmSetWindowAttribute(hwnd, DwmBorderColor, ref noBorder, sizeof(int));
        }
        catch (DllNotFoundException) { }
        catch (EntryPointNotFoundException) { }
    }
}

public sealed record SavedRepository(string Owner, string Name);
public sealed record AppSettings(string DownloadDirectory);
public sealed record DownloadRecord(string RepositoryKey, long ReleaseId, string TagName,
    string FileName, string FilePath, DateTimeOffset DownloadedAt);
public sealed record RepositoryCard(string Title, string RepositoryLabel, string Description,
    string Symbol, string Badge, string ActionLabel, string RepositoryKey);
public sealed record AssetRow(ReleaseAsset Asset, string Name, string SizeLabel);
public sealed record DownloadRow(string FileName, string Detail, string FilePath);
public sealed record UpdateRow(string RepositoryKey, string Name, string Detail);
