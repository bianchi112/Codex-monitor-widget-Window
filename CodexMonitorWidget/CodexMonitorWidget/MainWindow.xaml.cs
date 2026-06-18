using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using CodexMonitorWidget.Models;
using CodexMonitorWidget.Services;

namespace CodexMonitorWidget;

public partial class MainWindow
{
    private readonly CodexUsageStore _store;
    private bool _isCompact;

    public MainWindow()
    {
        InitializeComponent();
        _store = App.Store;
        DataContext = _store;

        _store.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(CodexUsageStore.AlwaysOnTop))
            {
                AlwaysOnTopCheckBox.IsChecked = _store.AlwaysOnTop;
                Topmost = _store.AlwaysOnTop;
            }

            UpdateUi();
        };
        AlwaysOnTopCheckBox.IsChecked = _store.AlwaysOnTop;
        Topmost = _store.AlwaysOnTop;

        UpdateUi();
        UpdateCompactLayout();
    }

    private void UpdateUi()
    {
        RefreshButton.Visibility = _store.HasFolderAccess ? Visibility.Visible : Visibility.Collapsed;
        PermissionPanel.Visibility = _store.HasFolderAccess ? Visibility.Collapsed : Visibility.Visible;
        WatchingPanel.Visibility = _store.HasFolderAccess ? Visibility.Visible : Visibility.Collapsed;
        AlwaysOnTopCheckBox.Visibility = _store.HasFolderAccess ? Visibility.Visible : Visibility.Collapsed;

        if (!_store.HasFolderAccess)
        {
            UsagePanel.Visibility = Visibility.Collapsed;
            EmptyPanel.Visibility = Visibility.Collapsed;
            UpdatePermissionPanel();
            return;
        }

        UpdateWatchingPanel();

        if (_store.Snapshot != null)
        {
            UsagePanel.Visibility = Visibility.Visible;
            EmptyPanel.Visibility = Visibility.Collapsed;
            UpdateUsagePanel(_store.Snapshot);
        }
        else
        {
            UsagePanel.Visibility = Visibility.Collapsed;
            EmptyPanel.Visibility = Visibility.Visible;
            EmptyPathText.Text = _store.FolderAccess.DisplayPath;
        }
    }

    private void UpdatePermissionPanel()
    {
        var folderAccess = _store.FolderAccess;
        ExpectedCodexPathText.Text = folderAccess.ExpectedCodexPath;
        ExpectedSessionsPathText.Text = folderAccess.ExpectedSessionsPath;
        ExpectedSessionsPathText.Visibility = _isCompact ? Visibility.Collapsed : Visibility.Visible;
        OpenExplorerButton.Visibility = _isCompact ? Visibility.Collapsed : Visibility.Visible;

        if (folderAccess.IsExpectedFolderAvailable)
        {
            FolderStatusIcon.Text = "✓";
            FolderStatusIcon.Foreground = new SolidColorBrush(Color.FromRgb(52, 199, 89));
            FolderStatusText.Text = "`.codex` 폴더를 찾았습니다.";
        }
        else
        {
            FolderStatusIcon.Text = "⚠";
            FolderStatusIcon.Foreground = Brushes.Orange;
            FolderStatusText.Text = "Codex 실행 후 다시 시도해 주세요.";
        }

        AccessErrorText.Text = folderAccess.AccessError ?? "";
        AccessErrorText.Visibility = string.IsNullOrEmpty(folderAccess.AccessError)
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    private void UpdateWatchingPanel()
    {
        WatchIndicator.Fill = _store.IsWatching ? Brushes.LimeGreen : Brushes.Orange;
        WatchStatusText.Text = _store.IsWatching ? "자동 감시 중" : "감시 중지";
        LastUpdatedText.Text = _store.LastUpdated?.ToString("HH:mm:ss") ?? "";
    }

    private void UpdateUsagePanel(CodexUsageSnapshot snapshot)
    {
        TokenSectionTitle.Visibility = _isCompact ? Visibility.Collapsed : Visibility.Visible;
        PlanTypeText.Text = _isCompact
            ? $"플랜 {snapshot.PlanType.ToUpperInvariant()}"
            : $"플랜: {snapshot.PlanType.ToUpperInvariant()}";

        if (snapshot.HasLastUserTurn)
        {
            LastUserPreviewText.Text = $"\"{snapshot.LastUserMessagePreview}\"";
            PopulateMetricsGrid(LastTurnMetricsGrid, new (string, string)[]
            {
                ("모델", snapshot.LastTurnModelDisplay),
                ("추론", snapshot.LastTurnReasoningEffortDisplay),
                ("총", snapshot.FormattedLastTurnTotalTokens),
                ("출력", CodexUsageSnapshot.Format(snapshot.LastTurnOutputTokens)),
                ("입력", CodexUsageSnapshot.Format(snapshot.LastTurnInputTokens)),
                ("캐시", CodexUsageSnapshot.Format(snapshot.LastTurnCachedInputTokens)),
                ("신규", CodexUsageSnapshot.Format(snapshot.LastTurnNewInputTokens)),
                snapshot.LastTurnReasoningTokens > 0
                    ? ("추론", CodexUsageSnapshot.Format(snapshot.LastTurnReasoningTokens))
                    : ("", "")
            });
        }
        else
        {
            LastUserPreviewText.Text = "아직 입력 기록이 없습니다.";
            LastTurnMetricsGrid.Children.Clear();
        }

        PopulateMetricsGrid(SessionMetricsGrid, new (string, string)[]
        {
            ("총", snapshot.FormattedTotalTokens),
            ("출력", CodexUsageSnapshot.Format(snapshot.OutputTokens)),
            ("입력", CodexUsageSnapshot.Format(snapshot.InputTokens)),
            ("캐시", CodexUsageSnapshot.Format(snapshot.CachedInputTokens))
        });

        UpdateBreakdown(snapshot.LastTurnInputBreakdown);

        FiveHourGauge.RemainingPercent = snapshot.FiveHourRemainingPercent;
        FiveHourGauge.UsedPercent = snapshot.FiveHourUsedPercent;
        FiveHourGauge.ResetAt = snapshot.FiveHourResetAt;
        FiveHourGauge.Compact = _isCompact;

        WeeklyGauge.RemainingPercent = snapshot.WeeklyRemainingPercent;
        WeeklyGauge.UsedPercent = snapshot.WeeklyUsedPercent;
        WeeklyGauge.ResetAt = snapshot.WeeklyResetAt;
        WeeklyGauge.Compact = _isCompact;
    }

    private void UpdateBreakdown(CodexInputBreakdown? breakdown)
    {
        BreakdownPanel.Visibility = breakdown != null ? Visibility.Visible : Visibility.Collapsed;
        if (breakdown == null)
            return;

        PopulateMetricsGrid(BreakdownExactGrid, new (string, string)[]
        {
            ("캐시(정확)", CodexUsageSnapshot.Format(breakdown.ExactCachedTokens)),
            ("신규(정확)", CodexUsageSnapshot.Format(breakdown.ExactNewInputTokens))
        });

        BreakdownComponentsPanel.Children.Clear();
        if (!breakdown.HasComponents)
            return;

        foreach (var item in breakdown.Components)
        {
            var row = new Grid { Margin = new Thickness(0, 0, 0, 2) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var label = new TextBlock
            {
                Text = item.Label,
                FontSize = 11,
                Foreground = (Brush)FindResource("SecondaryTextBrush"),
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            var value = new TextBlock
            {
                Text = item.FormattedTokens,
                FontSize = 11,
                FontFamily = new FontFamily("Consolas"),
                Foreground = Brushes.White
            };

            Grid.SetColumn(label, 0);
            Grid.SetColumn(value, 1);
            row.Children.Add(label);
            row.Children.Add(value);
            BreakdownComponentsPanel.Children.Add(row);
        }
    }

    private static void PopulateMetricsGrid(System.Windows.Controls.Panel grid, (string Title, string Value)[] metrics)
    {
        if (grid is not UniformGrid uniformGrid)
            return;

        uniformGrid.Children.Clear();
        foreach (var (title, value) in metrics)
        {
            if (string.IsNullOrEmpty(title))
                continue;

            uniformGrid.Children.Add(CreateMetricBlock(title, value));
        }
    }

    private static StackPanel CreateMetricBlock(string title, string value)
    {
        return new StackPanel
        {
            Margin = new Thickness(0, 0, 8, 6),
            Children =
            {
                new TextBlock
                {
                    Text = title,
                    FontSize = 11,
                    Foreground = new SolidColorBrush(Color.FromArgb(153, 255, 255, 255))
                },
                new TextBlock
                {
                    Text = value,
                    FontSize = 12,
                    FontFamily = new FontFamily("Consolas"),
                    Foreground = Brushes.White
                }
            }
        };
    }

    private void UpdateCompactLayout()
    {
        _isCompact = ActualWidth < 260 || ActualHeight < 220;
        TitleText.FontSize = _isCompact ? 13 : 16;
        PermissionIntro.FontSize = _isCompact ? 12 : 14;

        if (_store.Snapshot != null)
            UpdateUsagePanel(_store.Snapshot);

        if (!_store.HasFolderAccess)
            UpdatePermissionPanel();
    }

    private void OnWindowSizeChanged(object sender, SizeChangedEventArgs e) => UpdateCompactLayout();

    private void OnWindowClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        e.Cancel = true;
        Hide();
    }

    private void OnDragWindow(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left)
            return;

        if (e.OriginalSource is DependencyObject source &&
            FindAncestor<Button>(source) != null)
            return;

        DragMove();
    }

    private static T? FindAncestor<T>(DependencyObject current) where T : DependencyObject
    {
        while (current != null)
        {
            if (current is T match)
                return match;

            current = VisualTreeHelper.GetParent(current);
        }

        return null;
    }

    private void OnRefreshClick(object sender, RoutedEventArgs e) => _store.Refresh();

    private void OnSelectFolderClick(object sender, RoutedEventArgs e) => _store.RequestFolderAccess();

    private void OnOpenExplorerClick(object sender, RoutedEventArgs e) => _store.RevealExpectedFolderInExplorer();

    private void OnAlwaysOnTopChanged(object sender, RoutedEventArgs e)
    {
        _store.AlwaysOnTop = AlwaysOnTopCheckBox.IsChecked == true;
        Topmost = _store.AlwaysOnTop;
    }
}
