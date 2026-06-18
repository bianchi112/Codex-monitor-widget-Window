using System.Drawing;
using System.Windows.Forms;
using System.Windows;
using CodexMonitorWidget.Services;

namespace CodexMonitorWidget.Tray;

public sealed class TrayIconService : IDisposable
{
    private readonly CodexUsageStore _store;
    private readonly NotifyIcon _notifyIcon;
    private readonly ContextMenuStrip _contextMenu;

    public TrayIconService(CodexUsageStore store)
    {
        _store = store;
        _contextMenu = BuildContextMenu();
        _notifyIcon = new NotifyIcon
        {
            Icon = LoadAppIcon(),
            Visible = true,
            Text = "Codex Monitor",
            ContextMenuStrip = _contextMenu
        };

        _notifyIcon.DoubleClick += (_, _) => ShowMainWindow();
        _store.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(CodexUsageStore.MenuBarSummary) or nameof(CodexUsageStore.Snapshot))
                UpdateTrayText();
        };

        UpdateTrayText();
        UpdateMenuItems();
        _store.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(CodexUsageStore.HasFolderAccess)
                or nameof(CodexUsageStore.Snapshot)
                or nameof(CodexUsageStore.IsWatching)
                or nameof(CodexUsageStore.StatusMessage))
            {
                UpdateMenuItems();
            }
        };
    }

    private static Icon LoadAppIcon()
    {
        try
        {
            var uri = new Uri("pack://application:,,,/Assets/app-icon.ico", UriKind.Absolute);
            var stream = System.Windows.Application.GetResourceStream(uri)?.Stream;
            if (stream != null)
                return new Icon(stream);
        }
        catch
        {
        }

        return SystemIcons.Application;
    }

    private ContextMenuStrip BuildContextMenu()
    {
        var menu = new ContextMenuStrip();

        menu.Opening += (_, _) => UpdateMenuItems();
        menu.Items.Add("위젯 열기", null, (_, _) => ShowMainWindow());
        menu.Items.Add(new ToolStripSeparator());

        return menu;
    }

    private void UpdateTrayText()
    {
        if (_store.HasFolderAccess && _store.Snapshot != null)
            _notifyIcon.Text = $"Codex Monitor — 5시간 {_store.MenuBarSummary} 남음";
        else
            _notifyIcon.Text = "Codex Monitor";
    }

    private void UpdateMenuItems()
    {
        while (_contextMenu.Items.Count > 2)
            _contextMenu.Items.RemoveAt(2);

        if (_store.Snapshot != null)
        {
            var snapshot = _store.Snapshot;
            _contextMenu.Items.Add(
                $"5시간 {(int)Math.Round(snapshot.FiveHourRemainingPercent)}% 남음");
            _contextMenu.Items.Add(
                $"주간 {(int)Math.Round(snapshot.WeeklyRemainingPercent)}% 남음");
            _contextMenu.Items.Add(
                $"세션 {snapshot.FormattedTotalTokens} · 방금 {snapshot.FormattedLastTurnTotalTokens}");
        }
        else
        {
            _contextMenu.Items.Add(_store.StatusMessage);
        }

        if (_store.IsWatching)
            _contextMenu.Items.Add("백그라운드 감시 중");

        _contextMenu.Items.Add(new ToolStripSeparator());

        if (_store.HasFolderAccess)
        {
            _contextMenu.Items.Add("새로고침", null, (_, _) => _store.Refresh());
        }
        else
        {
            _contextMenu.Items.Add("~/.codex 선택하기", null, (_, _) => _store.RequestFolderAccess());
            _contextMenu.Items.Add("탐색기에서 .codex 열기", null, (_, _) => _store.RevealExpectedFolderInExplorer());
        }

        _contextMenu.Items.Add(new ToolStripSeparator());

        var alwaysOnTopItem = new ToolStripMenuItem("항상 위에 표시")
        {
            Checked = _store.AlwaysOnTop,
            CheckOnClick = true
        };
        alwaysOnTopItem.CheckedChanged += (_, _) => _store.AlwaysOnTop = alwaysOnTopItem.Checked;
        _contextMenu.Items.Add(alwaysOnTopItem);

        _contextMenu.Items.Add(new ToolStripSeparator());
        _contextMenu.Items.Add("종료", null, (_, _) =>
        {
            _notifyIcon.Visible = false;
            System.Windows.Application.Current.Shutdown();
        });
    }

    private static void ShowMainWindow()
    {
        if (System.Windows.Application.Current.MainWindow is { } window)
        {
            window.Show();
            window.WindowState = WindowState.Normal;
            window.Activate();
        }
    }

    public void Dispose()
    {
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _contextMenu.Dispose();
    }
}
