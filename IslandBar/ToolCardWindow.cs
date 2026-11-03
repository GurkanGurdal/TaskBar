using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace IslandBar;

/// <summary>
/// Tekerlekten seçilen aracın kartı, tekerleğin açıldığı yerde (kullanıcının isteği: araç seçenekleri orada açılsın).
/// Adanın kartlarıyla aynı cam (<see cref="GlassCard"/>); içerik Araçlar kartının o bölümü ya da hatırlatıcılar için
/// takvim. Veriler ortak, çubuktaki kartlarla aynı listeler. Dışarı tıklamak, Esc ya da araçta bir iş yapmak kapatır;
/// içinden sürüklerken kart görünmez olur ki bırakılacak yeri kapatmasın, bırakılmazsa geri gelir.
/// </summary>
internal sealed class ToolCardWindow : Window
{
    private const double ShadowMargin = 20;
    private const double ScreenGap = 8;
    private const int OpenMs = 170;
    private const int DragFadeMs = 120;
    private const uint NoSize = 0x0001;
    private const uint NoZOrder = 0x0004;

    private readonly System.Drawing.Point _center;
    private readonly GlassCard _card;
    private readonly ScaleTransform _scale = new(0.92, 0.92);
    private bool _dragging;
    private bool _closing;

    public ToolCardWindow(ToolKind kind, System.Drawing.Point center)
    {
        _center = center;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        Topmost = true;
        SizeToContent = SizeToContent.WidthAndHeight;

        FrameworkElement content;
        double width;
        if (kind == ToolKind.Reminders)
        {
            var calendar = new CalendarPanel();
            calendar.ShowToday();
            calendar.Opened += Dismiss;
            content = calendar;
            width = 300;
        }
        else
        {
            var tools = new ToolsPanel { Only = kind };
            tools.Opened += Dismiss;
            tools.DragStarted += () =>
            {
                _dragging = true;
                _card!.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(DragFadeMs)));
            };
            tools.DragFinished += dropped =>
            {
                _dragging = false;
                _card!.BeginAnimation(OpacityProperty, null);
                _card.Opacity = dropped ? 0 : 1;
                if (dropped)
                {
                    Dismiss();
                }
            };
            content = tools;
            width = tools.Width;
        }

        _card = new GlassCard
        {
            Width = width + 32,
            Padding = new Thickness(16, 14, 16, 12),
            CornerRadius = new CornerRadius(16),
            BlurRadius = 24,
            AllowDrop = false,
            Content = content,
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = _scale
        };
        Content = new Border { Padding = new Thickness(ShadowMargin), Child = _card };

        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                Dismiss();
            }
        };
        Deactivated += (_, _) =>
        {
            if (!_dragging)
            {
                Dismiss();
            }
        };
        SizeChanged += (_, _) => Place();
    }

    public void Dismiss()
    {
        if (_closing)
        {
            return;
        }

        _closing = true;
        Close();
    }

    /// <summary>Tekerleğin merkezine ortalı, ekranın çalışma alanına sığdırılmış.</summary>
    private void Place()
    {
        if (PresentationSource.FromVisual(this) is not HwndSource source)
        {
            return;
        }

        double scale = source.CompositionTarget.TransformToDevice.M11;
        int width = (int)Math.Round(ActualWidth * scale);
        int height = (int)Math.Round(ActualHeight * scale);
        System.Drawing.Rectangle area = System.Windows.Forms.Screen.FromPoint(_center).WorkingArea;
        int gap = (int)Math.Round((ScreenGap - ShadowMargin) * scale);
        int x = Math.Clamp(_center.X - width / 2, area.Left + gap, Math.Max(area.Left + gap, area.Right - width - gap));
        int y = Math.Clamp(_center.Y - height / 2, area.Top + gap, Math.Max(area.Top + gap, area.Bottom - height - gap));
        SetWindowPos(source.Handle, IntPtr.Zero, x, y, 0, 0, NoSize | NoZOrder);
    }

    protected override async void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        Place();
        Activate();

        // Cam arkası hazır olunca belirir; açılırken kart görünmez, kendini görüntüye katmaz.
        await _card.RefreshGlassAsync(animateTint: false, isHidden: true);
        if (_closing)
        {
            return;
        }

        if (!SystemParameters.ClientAreaAnimation)
        {
            _card.Opacity = 1;
            _scale.ScaleX = _scale.ScaleY = 1;
            return;
        }

        var duration = TimeSpan.FromMilliseconds(OpenMs);
        var grow = new DoubleAnimation(1, duration) { EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.3 } };
        _scale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
        _scale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
        _card.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, duration));
    }

    [DllImport("user32")]
    private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
}
