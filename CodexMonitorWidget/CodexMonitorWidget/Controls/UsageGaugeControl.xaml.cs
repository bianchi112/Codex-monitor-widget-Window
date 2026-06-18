using System.Windows;
using System.Windows.Media;

namespace CodexMonitorWidget.Controls;

public partial class UsageGaugeControl
{
    public static readonly DependencyProperty TitleProperty =
        DependencyProperty.Register(nameof(Title), typeof(string), typeof(UsageGaugeControl),
            new PropertyMetadata(""));

    public static readonly DependencyProperty RemainingPercentProperty =
        DependencyProperty.Register(nameof(RemainingPercent), typeof(double), typeof(UsageGaugeControl),
            new PropertyMetadata(0.0, OnGaugeChanged));

    public static readonly DependencyProperty UsedPercentProperty =
        DependencyProperty.Register(nameof(UsedPercent), typeof(double), typeof(UsageGaugeControl),
            new PropertyMetadata(0.0));

    public static readonly DependencyProperty ResetAtProperty =
        DependencyProperty.Register(nameof(ResetAt), typeof(DateTime), typeof(UsageGaugeControl),
            new PropertyMetadata(DateTime.MinValue));

    public static readonly DependencyProperty CompactProperty =
        DependencyProperty.Register(nameof(Compact), typeof(bool), typeof(UsageGaugeControl),
            new PropertyMetadata(false, OnGaugeChanged));

    public UsageGaugeControl()
    {
        InitializeComponent();
        UpdateGauge();
    }

    public string Title
    {
        get => (string)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public double RemainingPercent
    {
        get => (double)GetValue(RemainingPercentProperty);
        set => SetValue(RemainingPercentProperty, value);
    }

    public double UsedPercent
    {
        get => (double)GetValue(UsedPercentProperty);
        set => SetValue(UsedPercentProperty, value);
    }

    public DateTime ResetAt
    {
        get => (DateTime)GetValue(ResetAtProperty);
        set => SetValue(ResetAtProperty, value);
    }

    public bool Compact
    {
        get => (bool)GetValue(CompactProperty);
        set => SetValue(CompactProperty, value);
    }

    private static void OnGaugeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is UsageGaugeControl control)
            control.UpdateGauge();
    }

    private void UpdateGauge()
    {
        var remaining = Math.Clamp(RemainingPercent, 0, 100);
        var color = remaining switch
        {
            >= 30 => new SolidColorBrush(Color.FromRgb(52, 199, 89)),
            >= 10 => new SolidColorBrush(Color.FromRgb(255, 149, 0)),
            _ => new SolidColorBrush(Color.FromRgb(255, 59, 48))
        };

        if (GaugeTrack.ActualWidth > 0)
            GaugeFill.Width = Math.Max(0, GaugeTrack.ActualWidth * (remaining / 100.0));

        GaugeFill.Background = color;
        RemainingText.Foreground = color;

        ResetText.Visibility = Compact ? Visibility.Collapsed : Visibility.Visible;
        ResetText.Text = $"{UsedPercent:0}% 사용 · 리셋: {ResetAt:MMM d HH:mm}";
    }

    private void OnGaugeTrackSizeChanged(object sender, SizeChangedEventArgs e) => UpdateGauge();
}
