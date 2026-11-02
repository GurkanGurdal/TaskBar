using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace IslandBar;

/// <summary>
/// Ses tuşlarının göstergesi: Windows'unkinin yerine adanın camından kart. Yeri çubuğa göre (BarWindow.PlaceVolumeChip),
/// çubuk açılıp kapanınca yeni yerine kayar. Odak almaz, tıklanmaz. Çizgi yayla gider, basılı tutunca sayı anında değişir.
/// </summary>
public partial class VolumeChip : UserControl
{
    public const double CardWidth = 220;

    private const int HoldMs = 1500;
    private const int RevealMs = 110;
    private const int HideMs = 180;
    private const int MoveMs = 240;
    private const double SlideDistance = 8;
    private const double LevelStiffness = 700;
    private const double LevelDamping = 46;
    private const double MaxStepSeconds = 1d / 30;
    private const double RestLevel = 0.001;
    private const double MutedFillOpacity = 0.35;

    private readonly DispatcherTimer _holdTimer = new() { Interval = TimeSpan.FromMilliseconds(HoldMs) };
    private readonly Stopwatch _clock = new();
    private Spring _level;
    private Vector _towardBar = new(0, 1);
    private bool _moving;
    private bool _shown;
    private bool _hiding;

    public VolumeChip()
    {
        InitializeComponent();
        _holdTimer.Tick += HoldTimer_Tick;
    }

    public event Action? Dismissed;

    /// <summary>Yeni yerine kaydı, arkası yeniden alınıyor.</summary>
    public event Action? Slid;

    public bool IsHiding => _hiding;

    public void SetThickness(double thickness, double cornerRadius)
    {
        Card.Height = thickness;
        Card.CornerRadius = new CornerRadius(cornerRadius);
    }

    /// <summary>Basışı gösterir. Kart zaten açıksa yerinde kalır, süresi baştan başlar.</summary>
    internal void Present(AudioEndpoint audio)
    {
        bool muted = audio.Muted;
        float level = audio.Level;

        Glyph.Glyph = QuickSettingsPanel.VolumeGlyphFor(audio);
        Number.Text = Math.Round(level * 100).ToString(CultureInfo.CurrentCulture);
        Fill.Opacity = muted ? MutedFillOpacity : 1;

        _level.Target = level;
        if (!_shown || !SystemParameters.ClientAreaAnimation)
        {
            _level.Jump();
            ApplyLevel();
        }
        else if (!_moving)
        {
            _moving = true;
            _clock.Restart();
            CompositionTarget.Rendering += CompositionTarget_Rendering;
        }

        _holdTimer.Stop();
        _holdTimer.Start();

        if (_hiding)
        {
            _hiding = false;
            Move(opacity: 1, slide: 0, RevealMs);
        }
    }

    /// <summary>Popup açıldı: arkası alınınca belirir. Kart üstüne çıkmadan alındığı için pencereyi hariç tutmaya gerek yok.</summary>
    public async Task RevealAsync(Vector towardBar)
    {
        _towardBar = towardBar;
        _shown = true;
        _hiding = false;

        Card.BeginAnimation(OpacityProperty, null);
        await Card.RefreshGlassAsync(animateTint: false, isHidden: true);

        // Kapalı Popup'ta yerleşim askıda; çizginin uzunluğu ancak şimdi belli.
        ApplyLevel();

        if (_shown && !_hiding)
        {
            SetSlide(SlideDistance);
            Move(opacity: 1, slide: 0, RevealMs);
        }
    }

    /// <summary>Yerleşim yeni yerde; kart eski yerinden oraya kayar.</summary>
    public void SlideFrom(Vector fromOldPlace)
    {
        if (!SystemParameters.ClientAreaAnimation)
        {
            FinishSlide();
            return;
        }

        var duration = TimeSpan.FromMilliseconds(MoveMs);
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };

        // Önceki kayma sürüyorsa bulunduğu yerden başlar.
        var slideY = new DoubleAnimation(Slide.Y + fromOldPlace.Y, 0, duration) { EasingFunction = ease };
        slideY.Completed += (_, _) => FinishSlide();
        Slide.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(Slide.X + fromOldPlace.X, 0, duration) { EasingFunction = ease });
        Slide.BeginAnimation(TranslateTransform.YProperty, slideY);
    }

    private void FinishSlide()
    {
        if (_shown && !_hiding)
        {
            _ = Card.RefreshGlassAsync(animateTint: true);
        }
        Slid?.Invoke();
    }

    private void HoldTimer_Tick(object? sender, EventArgs e)
    {
        _holdTimer.Stop();
        _hiding = true;

        if (!SystemParameters.ClientAreaAnimation)
        {
            Finish();
            return;
        }

        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(HideMs));
        fade.Completed += (_, _) =>
        {
            // Bu sırada yeni basış geldiyse kart kalır.
            if (_hiding)
            {
                Finish();
            }
        };
        Card.BeginAnimation(OpacityProperty, fade);
        AnimateSlide(SlideDistance, HideMs);
    }

    private void Finish()
    {
        _hiding = false;
        _shown = false;
        Card.BeginAnimation(OpacityProperty, null);
        Dismissed?.Invoke();
    }

    private void Move(double opacity, double slide, int milliseconds)
    {
        int duration = SystemParameters.ClientAreaAnimation ? milliseconds : 0;
        Card.BeginAnimation(OpacityProperty, new DoubleAnimation(opacity, TimeSpan.FromMilliseconds(duration)));
        AnimateSlide(slide, duration);
    }

    private void AnimateSlide(double distance, int milliseconds)
    {
        var duration = TimeSpan.FromMilliseconds(milliseconds);
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        Slide.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(_towardBar.X * distance, duration) { EasingFunction = ease });
        Slide.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(_towardBar.Y * distance, duration) { EasingFunction = ease });
    }

    private void SetSlide(double distance)
    {
        Slide.BeginAnimation(TranslateTransform.XProperty, null);
        Slide.BeginAnimation(TranslateTransform.YProperty, null);
        Slide.X = _towardBar.X * distance;
        Slide.Y = _towardBar.Y * distance;
    }

    private void CompositionTarget_Rendering(object? sender, EventArgs e)
    {
        double step = Math.Min(_clock.Elapsed.TotalSeconds, MaxStepSeconds);
        _clock.Restart();

        bool moving = _level.Advance(step, LevelStiffness, LevelDamping, RestLevel);
        ApplyLevel();

        if (!moving)
        {
            _moving = false;
            CompositionTarget.Rendering -= CompositionTarget_Rendering;
        }
    }

    private void ApplyLevel()
    {
        Fill.Width = Math.Clamp(_level.Value, 0, 1) * Track.ActualWidth;
    }
}
