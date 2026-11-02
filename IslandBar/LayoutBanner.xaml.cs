using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Animation;

namespace IslandBar;

/// <summary>
/// Düzenleme modunun kartı: ne olduğunu ve kaç uygulamanın düzene gireceğini söyler, kaydet ve vazgeç buradan.
/// Etkinleşmez: tıklamak dizilen pencerelerin sırasını ve odağını bozmasın.
/// </summary>
public partial class LayoutBanner : Window
{
    private const int ExStyle = -20;
    private const int ExToolWindow = 0x80;
    private const int ExNoActivate = 0x08000000;
    private const double SlideDistance = 14;
    private const int SlideMs = 220;

    private readonly bool _atBottom;

    /// <param name="atBottom">Çubuk üstteyse kart altta durur, çubuğun önünü kapatmasın.</param>
    public LayoutBanner(bool atBottom)
    {
        _atBottom = atBottom;
        InitializeComponent();
        SourceInitialized += (_, _) =>
        {
            IntPtr handle = new WindowInteropHelper(this).Handle;
            SetWindowLong(handle, ExStyle, GetWindowLong(handle, ExStyle) | ExToolWindow | ExNoActivate);
        };
        SizeChanged += (_, _) => Place();
    }

    public event Action? SaveRequested;

    public event Action? CancelRequested;

    /// <summary>Kaç uygulamanın düzene gireceği; bu sırada açılan ve küçültülenler hemen yansısın diye sık sorulur.</summary>
    public void ShowCount(int count)
    {
        Detail.Text = count > 0
            ? $"Uygulamaları aç ve diz · {count} uygulama düzene girecek, küçültülenler girmez"
            : "Uygulamaları aç ve istediğin gibi diz · küçültülenler düzene girmez";
    }

    public void ShowNothingToSave()
    {
        Detail.Text = "Ekranda düzene girecek pencere yok; uygulamaları açıp diz";
    }

    public void ShowSaved(string name)
    {
        Buttons.Visibility = Visibility.Collapsed;
        Heading.Text = "Düzen kaydedildi";
        Detail.Text = $"{name} · Düzenler'de, tıklayınca aynı haliyle açılır";
    }

    public void Appear()
    {
        Show();
        Animate(from: _atBottom ? SlideDistance : -SlideDistance, to: 0, fromOpacity: 0, toOpacity: 1, then: null);
    }

    public void Disappear()
    {
        Animate(from: 0, to: _atBottom ? SlideDistance : -SlideDistance, fromOpacity: Opacity, toOpacity: 0, then: Close);
    }

    private void Animate(double from, double to, double fromOpacity, double toOpacity, Action? then)
    {
        var duration = TimeSpan.FromMilliseconds(SystemParameters.ClientAreaAnimation ? SlideMs : 0);
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var fade = new DoubleAnimation(fromOpacity, toOpacity, duration) { EasingFunction = ease };
        if (then != null)
        {
            fade.Completed += (_, _) => then();
        }
        Slide.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty, new DoubleAnimation(from, to, duration) { EasingFunction = ease });
        BeginAnimation(OpacityProperty, fade);
    }

    /// <summary>Ana ekranın çalışma alanında, ortada; gölge payı kenar boşluğu da olur.</summary>
    private void Place()
    {
        Rect area = SystemParameters.WorkArea;
        Left = area.Left + (area.Width - ActualWidth) / 2;
        Top = _atBottom ? area.Bottom - ActualHeight : area.Top;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        SaveRequested?.Invoke();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        CancelRequested?.Invoke();
    }

    [DllImport("user32")]
    private static extern int GetWindowLong(IntPtr window, int index);

    [DllImport("user32")]
    private static extern int SetWindowLong(IntPtr window, int index, int value);
}
