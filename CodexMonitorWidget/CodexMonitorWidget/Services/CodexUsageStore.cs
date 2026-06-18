using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Threading;
using CodexMonitorWidget.Models;

namespace CodexMonitorWidget.Services;

public sealed class CodexUsageStore : INotifyPropertyChanged
{
    private const string AlwaysOnTopKey = "alwaysOnTop";

    private readonly CodexFolderAccess _folderAccess;
    private readonly Dispatcher _dispatcher;
    private int _refreshGeneration;
    private bool _monitoringStarted;

    private CodexUsageSnapshot? _snapshot;
    private string _statusMessage = "로딩 중...";
    private DateTime? _lastUpdated;
    private bool _isWatching;
    private bool _alwaysOnTop;

    public event PropertyChangedEventHandler? PropertyChanged;

    public CodexUsageStore(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        _folderAccess = new CodexFolderAccess();
        _alwaysOnTop = LoadAlwaysOnTop();
        _folderAccess.RestoreSavedAccess();
        StartIfReady();
    }

    public CodexFolderAccess FolderAccess => _folderAccess;

    public CodexUsageSnapshot? Snapshot
    {
        get => _snapshot;
        private set => SetProperty(ref _snapshot, value);
    }

    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    public DateTime? LastUpdated
    {
        get => _lastUpdated;
        private set => SetProperty(ref _lastUpdated, value);
    }

    public bool IsWatching
    {
        get => _isWatching;
        private set => SetProperty(ref _isWatching, value);
    }

    public bool HasFolderAccess => _folderAccess.HasAccess;

    public string MenuBarSummary =>
        Snapshot == null ? "—" : $"{(int)Math.Round(Snapshot.FiveHourRemainingPercent)}%";

    public bool AlwaysOnTop
    {
        get => _alwaysOnTop;
        set
        {
            if (!SetProperty(ref _alwaysOnTop, value))
                return;

            SaveAlwaysOnTop(value);
        }
    }

    public void Shutdown()
    {
        StopMonitoring();
        _folderAccess.Shutdown();
    }

    public void StartIfReady()
    {
        if (!_folderAccess.HasAccess)
        {
            StopMonitoring();
            _monitoringStarted = false;
            Snapshot = null;
            StatusMessage = "Codex 폴더 접근 권한이 필요합니다";
            NotifyUiStateChanged();
            return;
        }

        NotifyUiStateChanged();
        Refresh();
    }

    public void RequestFolderAccess()
    {
        _folderAccess.RequestDefaultFolderAccess();
        StartIfReady();
        NotifyUiStateChanged();
    }

    public void RevealExpectedFolderInExplorer()
    {
        _folderAccess.RevealExpectedFolderInExplorer();
        NotifyUiStateChanged();
    }

    public void Refresh()
    {
        var sessionsPath = _folderAccess.SessionsPath;
        if (sessionsPath == null)
        {
            Snapshot = null;
            StatusMessage = "Codex 폴더 접근 권한이 필요합니다";
            return;
        }

        var generation = Interlocked.Increment(ref _refreshGeneration);

        _ = Task.Run(() =>
        {
            try
            {
                return CodexSessionParser.LatestSnapshot(sessionsPath);
            }
            catch
            {
                return null;
            }
        }).ContinueWith(task =>
        {
            if (generation != Volatile.Read(ref _refreshGeneration))
                return;

            var result = task.IsFaulted ? null : task.Result;
            _dispatcher.Invoke(() =>
            {
                ApplyRefreshResult(result, sessionsPath);
                EnsureMonitoringStarted(sessionsPath);
            });
        }, TaskScheduler.Default);
    }

    private void EnsureMonitoringStarted(string sessionsPath)
    {
        if (_monitoringStarted || !Directory.Exists(sessionsPath))
            return;

        _monitoringStarted = true;
        StartMonitoring();
    }

    private void ApplyRefreshResult(CodexUsageSnapshot? snapshot, string sessionsPath)
    {
        Snapshot = snapshot;
        LastUpdated = DateTime.Now;

        if (snapshot != null)
            StatusMessage = "이벤트 감시 중";
        else if (Directory.Exists(sessionsPath))
            StatusMessage = "token_count 데이터 없음";
        else
            StatusMessage = "sessions 폴더를 찾을 수 없습니다";

        UpdateActiveFileWatch(snapshot, sessionsPath);
        NotifyPropertyChanged(nameof(MenuBarSummary));
    }

    private void StartMonitoring()
    {
        var sessionsPath = _folderAccess.SessionsPath;
        if (sessionsPath == null || !Directory.Exists(sessionsPath))
        {
            StopMonitoring();
            return;
        }

        _folderAccess.Lifecycle.BeginMonitoring();
        var onChange = MakeChangeHandler();
        _folderAccess.Lifecycle.Watcher.ReplaceDirectory(sessionsPath, onChange);
        _folderAccess.Lifecycle.Watcher.WatchActiveFileIfNeeded(
            CodexSessionParser.LatestSessionFilePath(sessionsPath),
            onChange);

        IsWatching = true;
    }

    private void StopMonitoring()
    {
        _folderAccess.Lifecycle.EndMonitoring();
        _folderAccess.Lifecycle.Watcher.Stop();
        IsWatching = false;
        _monitoringStarted = false;
    }

    private void UpdateActiveFileWatch(CodexUsageSnapshot? snapshot, string sessionsPath)
    {
        var candidate = snapshot?.SourceFile ?? CodexSessionParser.LatestSessionFilePath(sessionsPath);
        _folderAccess.Lifecycle.Watcher.WatchActiveFileIfNeeded(candidate, MakeChangeHandler());
    }

    private Action<CodexWatchEvent> MakeChangeHandler() => watchEvent =>
    {
        _dispatcher.Invoke(() =>
        {
            if (watchEvent == CodexWatchEvent.ActiveSessionReplaced)
                StatusMessage = "새 세션 이벤트 감지됨";

            Refresh();
        });
    };

    private static bool LoadAlwaysOnTop()
    {
        var path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "CodexMonitorWidget",
            "settings.json");

        if (!File.Exists(path))
            return false;

        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
            if (doc.RootElement.TryGetProperty("alwaysOnTop", out var prop) &&
                prop.ValueKind == System.Text.Json.JsonValueKind.True)
                return true;
        }
        catch
        {
        }

        return false;
    }

    private static void SaveAlwaysOnTop(bool value)
    {
        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "CodexMonitorWidget");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "settings.json");

        var settings = new Dictionary<string, object?>();
        if (File.Exists(path))
        {
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
                foreach (var property in doc.RootElement.EnumerateObject())
                    settings[property.Name] = property.Value.Clone();
            }
            catch
            {
            }
        }

        settings["alwaysOnTop"] = value;
        var json = System.Text.Json.JsonSerializer.Serialize(settings);
        File.WriteAllText(path, json);
    }

    private bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;

        field = value;
        NotifyPropertyChanged(propertyName);
        return true;
    }

    private void NotifyUiStateChanged()
    {
        NotifyPropertyChanged(nameof(HasFolderAccess));
        NotifyPropertyChanged(nameof(FolderAccess));
    }

    private void NotifyPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
