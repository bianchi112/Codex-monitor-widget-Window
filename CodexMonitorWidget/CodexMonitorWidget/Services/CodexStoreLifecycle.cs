namespace CodexMonitorWidget.Services;

public sealed class CodexStoreLifecycle
{
    public CodexSessionWatcher Watcher { get; } = new();

    public void BeginMonitoring()
    {
        // macOS ProcessInfo.beginActivity 대응 — Windows에서는 FileSystemWatcher만 사용
    }

    public void EndMonitoring()
    {
    }

    public void Shutdown()
    {
        EndMonitoring();
        Watcher.Stop();
    }
}
