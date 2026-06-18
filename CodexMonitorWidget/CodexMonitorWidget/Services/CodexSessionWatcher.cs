using System.IO;
using CodexMonitorWidget.Models;

namespace CodexMonitorWidget.Services;

public sealed class CodexSessionWatcher : IDisposable
{
    private readonly object _lock = new();
    private FileSystemWatcher? _directoryWatcher;
    private FileSystemWatcher? _fileWatcher;
    private string? _watchedFilePath;
    private CancellationTokenSource? _debounceCts;
    private Action<CodexWatchEvent>? _onChange;
    private string? _watchDirectoryPath;

    public void Stop()
    {
        lock (_lock)
        {
            CancelDebounce();
            _directoryWatcher?.Dispose();
            _directoryWatcher = null;
            _fileWatcher?.Dispose();
            _fileWatcher = null;
            _watchedFilePath = null;
            _watchDirectoryPath = null;
        }
    }

    public void ReplaceDirectory(string path, Action<CodexWatchEvent> onChange)
    {
        lock (_lock)
        {
            _onChange = onChange;
            _directoryWatcher?.Dispose();

            _watchDirectoryPath = path;
            _directoryWatcher = new FileSystemWatcher(path)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName |
                               NotifyFilters.DirectoryName |
                               NotifyFilters.LastWrite |
                               NotifyFilters.Size
            };

            _directoryWatcher.Created += OnDirectoryEvent;
            _directoryWatcher.Deleted += OnDirectoryEvent;
            _directoryWatcher.Renamed += OnDirectoryRenamed;
            _directoryWatcher.Changed += OnDirectoryEvent;
            _directoryWatcher.EnableRaisingEvents = true;
        }
    }

    public void WatchActiveFileIfNeeded(string? filePath, Action<CodexWatchEvent> onChange)
    {
        lock (_lock)
        {
            if (string.IsNullOrEmpty(filePath))
                return;

            if (_watchedFilePath == filePath)
                return;

            _onChange = onChange;
            _watchedFilePath = filePath;
            _fileWatcher?.Dispose();

            var directory = Path.GetDirectoryName(filePath);
            var fileName = Path.GetFileName(filePath);
            if (string.IsNullOrEmpty(directory) || string.IsNullOrEmpty(fileName))
                return;

            _fileWatcher = new FileSystemWatcher(directory, fileName)
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName
            };

            _fileWatcher.Changed += OnActiveFileChanged;
            _fileWatcher.Deleted += OnActiveFileReplaced;
            _fileWatcher.Renamed += OnActiveFileRenamed;
            _fileWatcher.EnableRaisingEvents = true;
        }
    }

    private void OnDirectoryEvent(object sender, FileSystemEventArgs e)
    {
        if (!ShouldRefreshDirectoryEvent(e))
            return;

        ScheduleRefresh(CodexWatchEvent.SessionDirectoryChanged);
    }

    private void OnDirectoryRenamed(object sender, RenamedEventArgs e)
    {
        if (!ShouldRefreshDirectoryEvent(e))
            return;

        ScheduleRefresh(CodexWatchEvent.SessionDirectoryChanged);
    }

    private bool ShouldRefreshDirectoryEvent(FileSystemEventArgs e)
    {
        if (e.Name != null && e.Name.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase))
            return true;

        if (!string.IsNullOrEmpty(_watchDirectoryPath) &&
            e.FullPath.StartsWith(_watchDirectoryPath, StringComparison.OrdinalIgnoreCase))
        {
            return e.ChangeType is WatcherChangeTypes.Created
                or WatcherChangeTypes.Deleted
                or WatcherChangeTypes.Renamed;
        }

        return false;
    }

    private void OnActiveFileChanged(object sender, FileSystemEventArgs e) =>
        ScheduleRefresh(CodexWatchEvent.ActiveSessionUpdated);

    private void OnActiveFileReplaced(object sender, FileSystemEventArgs e) =>
        ScheduleRefresh(CodexWatchEvent.ActiveSessionReplaced);

    private void OnActiveFileRenamed(object sender, RenamedEventArgs e) =>
        ScheduleRefresh(CodexWatchEvent.ActiveSessionReplaced);

    private void ScheduleRefresh(CodexWatchEvent watchEvent)
    {
        Action<CodexWatchEvent>? handler;
        lock (_lock)
        {
            handler = _onChange;
            CancelDebounce();
            _debounceCts = new CancellationTokenSource();
            var token = _debounceCts.Token;
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(150, token);
                    if (!token.IsCancellationRequested)
                        handler?.Invoke(watchEvent);
                }
                catch (TaskCanceledException)
                {
                }
            }, token);
        }
    }

    private void CancelDebounce()
    {
        _debounceCts?.Cancel();
        _debounceCts?.Dispose();
        _debounceCts = null;
    }

    public void Dispose() => Stop();
}
