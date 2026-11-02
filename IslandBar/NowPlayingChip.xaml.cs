using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;

namespace IslandBar;

/// <summary>
/// Yeni şarkı başlayınca çubuğun yanında beliren küçük kart: kapak, ad, sanatçı. Odak almaz, kısa sürede kendi gider,
/// üzerine gelince bekler, tıklayınca şarkının uygulamasına geçer. Yerini <see cref="BarWindow"/> belirler.
/// Claude Code bitirince ya da onay bekleyince aynı kart Claude kipinde çıkar (<see cref="SetClaude"/>): kapak yerine
/// Claude'un ikonu, çubuklar yerine bitti işareti ya da kehribar nokta.
/// Gelişi: çubuktan kapak büyüklüğünde bir cam damla fırlar, yay gibi oturur, kart yana açılır, yazılar bulanıklıktan netleşir.
/// Her şey tek saatten hesaplanır (<see cref="Motion"/>): yarıda kesilince gidiş bulunduğu yerden başlar.
/// </summary>
public partial class NowPlayingChip : UserControl
{
    private const int ShowSeconds = 5;
    private const int AfterHoverSeconds = 2;
    private const int SlideMs = 280;
    private const int BarFrameRate = 20;

    // Geliş: damla, kapak, açılma, yazılar, çubuklar. Yay frekansı radyan / ms, sönüm 1'in altında yerini geçip geri gelir.
    private const double BudFrequency = 0.024;
    private const double BudDamping = 0.6;
    private const double BudStartScale = 0.35;
    private const double BudStretch = 0.25;
    private const double BudFadeMs = 70;
    private const double MinBudDistance = 16;
    private const double BudDistanceRatio = 0.5;
    private const double CoverDelayMs = 60;
    private const double CoverFrequency = 0.03;
    private const double CoverDamping = 0.5;
    private const double CoverStartScale = 0.55;
    private const double CoverStartTilt = -12;
    private const double UnfurlDelayMs = 170;
    private const double UnfurlFrequency = 0.016;
    private const double UnfurlDamping = 0.68;
    private const double MaxUnfurlOvershoot = 5;
    private const double TitleDelayMs = 270;
    private const double ArtistDelayMs = 330;
    private const double TextMs = 260;
    private const double TextSlideDistance = 12;
    private const double TextBlur = 8;
    private const double BarsDelayMs = 400;
    private const double EnterEndMs = 800;

    // Kart açıkken yeni şarkı: yalnızca kapak ve yazılar.
    private const double RenewCoverScale = 0.82;
    private const double RenewCoverTilt = -6;
    private const double RenewTitleDelayMs = 40;
    private const double RenewArtistDelayMs = 100;
    private const double RenewEndMs = 520;

    // Gidiş: yazılar söner, kart damlaya kapanır (yerini geçmez), damla çubuğa iner.
    private const double LeaveTextMs = 90;
    private const double LeaveCloseFrequency = 0.028;
    private const double LeaveBudDelayMs = 130;
    private const double LeaveBudMs = 170;
    private const double LeaveBudScale = 0.45;

    private const double CoverInsetRatio = 0.15;
    private const double MinCoverRadius = 4;
    private const double GlyphRatio = 0.44;
    private const double TitleFontRatio = 0.33;
    private const double MinTitleFont = 12;
    private const double MaxTitleFont = 18;
    private const double EndPadding = 16;
    private const double TextMaxWidth = 240;
    private const double CompactTitleMaxWidth = 200;
    private const double CompactArtistMaxWidth = 140;
    private const double CompactArtistGap = 8;

    // Onay noktasının halkaları: iki halka yarım tur arayla, kart görünür kaldıkça.
    private const int RingPeriodMs = 1400;
    private const double RingScale = 3.2;
    private const double RingStartOpacity = 0.9;
    private const int RingFrameRate = 30;

    /// <summary>Birbirini tutmayan süreler: çubuklar aynı anda inip kalkmasın.</summary>
    private static readonly int[] BarPeriodsMs = { 430, 310, 520 };

    private readonly DispatcherTimer _dismissTimer = new() { Interval = TimeSpan.FromSeconds(ShowSeconds) };
    private readonly Stopwatch _motionClock = new();
    private readonly BlurEffect _titleBlur = new() { RenderingBias = RenderingBias.Performance };
    private readonly BlurEffect _artistBlur = new() { RenderingBias = RenderingBias.Performance };
    private Motion _motion;
    private double _unfurl = 1;
    private double _leaveFromUnfurl = 1;
    private Vector _towardBar;
    private bool _isHiding;

    private enum Motion
    {
        None,
        Enter,
        Renew,
        Leave
    }

    public NowPlayingChip()
    {
        InitializeComponent();
        _dismissTimer.Tick += DismissTimer_Tick;
    }

    /// <summary>Tıklandı; kart kendini kapatır.</summary>
    public event Action? Clicked;

    /// <summary>Çıkış animasyonu bitti, pencere kapanabilir.</summary>
    public event Action? Dismissed;

    /// <summary>Çubuk açılıp kapanınca yeni yerine kaydı.</summary>
    public event Action? Slid;

    internal string AppId { get; private set; } = string.Empty;

    /// <summary>Şarkı değil Claude kartı gösteriliyor.</summary>
    internal bool IsClaude { get; private set; }

    public bool IsHiding => _isHiding;

    /// <summary>Görüntüsü alındı, kart ekranda.</summary>
    public bool IsShown { get; private set; }

    /// <summary>Kart adayla aynı kalınlıkta ve köşede, her şey ona göre ölçeklenir.</summary>
    /// <param name="cornerRadius">Adanın köşe yarıçapı.</param>
    /// <param name="isCompact">İki satır sığmıyor: ad ve sanatçı yan yana.</param>
    public void SetThickness(double thickness, double cornerRadius, bool isCompact)
    {
        double inset = Math.Round(thickness * CoverInsetRatio);
        double cover = thickness - 2 * inset;

        // İç içe köşeler aynı merkezden dönsün: kapağın köşesi kartınkinden kenar payı kadar küçük.
        double radius = Math.Max(MinCoverRadius, cornerRadius - inset);
        double titleFont = Math.Clamp(Math.Round(thickness * TitleFontRatio), MinTitleFont, MaxTitleFont);

        Card.Height = thickness;
        Card.CornerRadius = new CornerRadius(cornerRadius);
        Card.Padding = new Thickness(inset, 0, EndPadding, 0);
        CoverFrame.Width = cover;
        CoverFrame.Height = cover;
        CoverFrame.CornerRadius = new CornerRadius(radius);
        CoverImage.CornerRadius = new CornerRadius(radius);
        CoverGlyph.FontSize = Math.Round(cover * GlyphRatio);

        TitleText.FontSize = titleFont;
        ArtistText.FontSize = titleFont - 2;
        TextStack.Orientation = isCompact ? Orientation.Horizontal : Orientation.Vertical;
        TitleText.MaxWidth = isCompact ? CompactTitleMaxWidth : TextMaxWidth;
        ArtistText.MaxWidth = isCompact ? CompactArtistMaxWidth : TextMaxWidth;
        ArtistText.Margin = isCompact ? new Thickness(CompactArtistGap, 0, 0, 0) : new Thickness(0, -1, 0, 0);
    }

    internal void SetTrack(NowPlayingTrack track)
    {
        AppId = track.AppId;
        TitleText.Text = track.Title;
        ArtistText.Text = track.Artist;
        ArtistText.Foreground = (Brush)FindResource("TextSecondary");
        SetCover(track);
        SetKind(claude: false);
    }

    /// <summary>
    /// Claude kipi (kullanıcının seçtiği düzen): üstte proje, altta durum; onay beklerken durum kehribar. Cevabın metni
    /// gösterilmez. İkonu yoksa (Claude terminalde) genel sohbet işareti.
    /// </summary>
    internal void SetClaude(ImageSource? icon, string project, string status, bool waiting)
    {
        AppId = string.Empty;
        TitleText.Text = project;
        ArtistText.Text = status;
        ArtistText.Foreground = (Brush)FindResource(waiting ? "AttentionFill" : "TextSecondary");
        Cover.ImageSource = icon;
        CoverGlyph.Visibility = icon == null ? Visibility.Visible : Visibility.Collapsed;
        DoneMark.Visibility = waiting ? Visibility.Collapsed : Visibility.Visible;
        WaitMark.Visibility = waiting ? Visibility.Visible : Visibility.Collapsed;
        SetKind(claude: true);
        if (IsShown)
        {
            UpdateRings();
        }
    }

    /// <summary>Halkalar yalnızca onay beklerken ve kart ekrandayken döner.</summary>
    private void UpdateRings()
    {
        if (IsShown && IsClaude && WaitMark.Visibility == Visibility.Visible && SystemParameters.ClientAreaAnimation)
        {
            StartRing(WaitRing1, RingScale1, 0);
            StartRing(WaitRing2, RingScale2, RingPeriodMs / 2);
        }
        else
        {
            StopRing(WaitRing1, RingScale1);
            StopRing(WaitRing2, RingScale2);
        }
    }

    private static void StartRing(UIElement ring, ScaleTransform scale, int delayMs)
    {
        var duration = TimeSpan.FromMilliseconds(RingPeriodMs);
        var begin = TimeSpan.FromMilliseconds(delayMs);
        var grow = new DoubleAnimation(1, RingScale, duration) { BeginTime = begin, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        var fade = new DoubleAnimation(RingStartOpacity, 0, duration) { BeginTime = begin, RepeatBehavior = RepeatBehavior.Forever };
        Timeline.SetDesiredFrameRate(grow, RingFrameRate);
        Timeline.SetDesiredFrameRate(fade, RingFrameRate);
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
        ring.BeginAnimation(OpacityProperty, fade);
    }

    private static void StopRing(UIElement ring, ScaleTransform scale)
    {
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        ring.BeginAnimation(OpacityProperty, null);
    }

    /// <summary>Açık kart kip değiştirirse çubuklar da durur ya da başlar.</summary>
    private void SetKind(bool claude)
    {
        IsClaude = claude;
        CoverGlyph.Text = claude ? "" : "";
        BarsPanel.Visibility = claude ? Visibility.Collapsed : Visibility.Visible;
        ClaudeMark.Visibility = claude ? Visibility.Visible : Visibility.Collapsed;
        if (claude)
        {
            StopBars();
        }
        else if (IsShown)
        {
            StartBars();
        }
        UpdateRings();
    }

    /// <summary>Kapak şarkıdan sonra gelebildiği için ayrı güncellenir.</summary>
    internal void SetCover(NowPlayingTrack track)
    {
        Cover.ImageSource = track.Cover;
        CoverGlyph.Visibility = track.Cover == null ? Visibility.Visible : Visibility.Collapsed;

        var accent = new SolidColorBrush(track.Cover != null ? QuickSettingsPanel.CoverAccent(track.Cover) : ThemeManager.AccentColor);
        accent.Freeze();
        Bar1.Fill = accent;
        Bar2.Fill = accent;
        Bar3.Fill = accent;
    }

    /// <summary>Görüntüsü alınır, kart çubuktan damla olarak gelir.</summary>
    /// <param name="towardBar">Çubuğun bulunduğu yön, birim vektör.</param>
    public async Task RevealAsync(Vector towardBar)
    {
        _towardBar = towardBar;
        _isHiding = false;
        StopMotion();
        Card.Opacity = 0;

        await Card.RefreshGlassAsync(animateTint: false, isHidden: true);
        if (_isHiding)
        {
            return;
        }

        IsShown = true;
        if (!IsClaude)
        {
            StartBars();
        }
        UpdateRings();
        RestartDismiss(ShowSeconds);

        if (!SystemParameters.ClientAreaAnimation)
        {
            SetRest();
            return;
        }

        StartMotion(Motion.Enter);
    }

    /// <summary>Kart açıkken yeni şarkı geldi: süre baştan başlar.</summary>
    public void Renew()
    {
        RestartDismiss(ShowSeconds);

        // Giderken yakalandıysa geri gelsin; hâlâ geliyorsa yazısı zaten sırada.
        _isHiding = false;
        if (_motion == Motion.Enter)
        {
            return;
        }

        StopMotion();
        SetRest();
        if (SystemParameters.ClientAreaAnimation)
        {
            StartMotion(Motion.Renew);
        }
    }

    /// <summary>Çubuk açılıp kapanınca kart yeni yerine kayar. Pencere zaten taşındı; önce eski yerde çizilir.</summary>
    /// <param name="fromOldPlace">Eski konumun yeni konuma göre farkı.</param>
    public void SlideFrom(Vector fromOldPlace, Vector towardBar)
    {
        _towardBar = towardBar;
        if (!SystemParameters.ClientAreaAnimation)
        {
            _ = RefreshGlassAsync();
            Slid?.Invoke();
            return;
        }

        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var duration = TimeSpan.FromMilliseconds(SlideMs);
        var slideY = new DoubleAnimation(fromOldPlace.Y, 0, duration) { EasingFunction = ease };
        slideY.Completed += (_, _) =>
        {
            _ = RefreshGlassAsync();
            Slid?.Invoke();
        };
        CardSlide.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(fromOldPlace.X, 0, duration) { EasingFunction = ease });
        CardSlide.BeginAnimation(TranslateTransform.YProperty, slideY);
    }

    public Task RefreshGlassAsync()
    {
        return Card.RefreshGlassAsync(animateTint: true);
    }

    /// <summary>Damlaya kapanıp çubuğa iner; bitince <see cref="Dismissed"/>.</summary>
    /// <param name="immediately">Animasyonsuz: başka bir kart açılıyor.</param>
    public void Dismiss(bool immediately = false)
    {
        if (_isHiding)
        {
            return;
        }

        _isHiding = true;
        _dismissTimer.Stop();

        if (immediately || !SystemParameters.ClientAreaAnimation || Card.Opacity == 0)
        {
            FinishHiding();
            return;
        }

        _leaveFromUnfurl = Math.Min(_unfurl, 1);
        StartMotion(Motion.Leave);
    }

    /// <summary>Tam ekran uygulama, sunum modu ya da Windows'un "rahatsız etme" sayılan durumları.</summary>
    public static bool IsUserBusy()
    {
        const int Busy = 2;
        const int FullScreenDirect3D = 3;
        const int PresentationMode = 4;

        return SHQueryUserNotificationState(out int state) == 0 && state is Busy or FullScreenDirect3D or PresentationMode;
    }

    private void FinishHiding()
    {
        IsShown = false;
        StopBars();
        UpdateRings();
        StopMotion();
        SetRest();
        CardSlide.BeginAnimation(TranslateTransform.XProperty, null);
        CardSlide.BeginAnimation(TranslateTransform.YProperty, null);
        Card.Opacity = 0;
        Dismissed?.Invoke();
    }

    private void StartMotion(Motion motion)
    {
        if (_motion == Motion.None)
        {
            CompositionTarget.Rendering += CompositionTarget_Rendering;
        }

        _motion = motion;
        _motionClock.Restart();
        DrawMotion(0);
    }

    private void StopMotion()
    {
        if (_motion != Motion.None)
        {
            CompositionTarget.Rendering -= CompositionTarget_Rendering;
            _motion = Motion.None;
        }
    }

    private void CompositionTarget_Rendering(object? sender, EventArgs e)
    {
        if (DrawMotion(_motionClock.Elapsed.TotalMilliseconds))
        {
            return;
        }

        bool leaving = _motion == Motion.Leave;
        StopMotion();
        if (leaving)
        {
            FinishHiding();
        }
        else
        {
            SetRest();
        }
    }

    /// <summary>O anın karesi; hareket bittiyse false.</summary>
    private bool DrawMotion(double elapsed)
    {
        switch (_motion)
        {
            case Motion.Enter:
                DrawEnter(elapsed);
                return elapsed < EnterEndMs;
            case Motion.Renew:
                DrawCover(elapsed, RenewCoverScale, RenewCoverTilt);
                DrawText(TitleText, TitleSlide, _titleBlur, (elapsed - RenewTitleDelayMs) / TextMs);
                DrawText(ArtistText, ArtistSlide, _artistBlur, (elapsed - RenewArtistDelayMs) / TextMs);
                return elapsed < RenewEndMs;
            case Motion.Leave:
                DrawLeave(elapsed);
                return elapsed < LeaveBudDelayMs + LeaveBudMs;
            default:
                return false;
        }
    }

    private void DrawEnter(double elapsed)
    {
        double bud = Spring(elapsed, BudFrequency, BudDamping);

        // Hızlanırken gidiş yönünde uzar, yerini geçip dönerken basılır.
        double speed = (Spring(elapsed + 1, BudFrequency, BudDamping) - bud) / BudFrequency;
        double along = 1 + speed * BudStretch;
        double across = 1 - speed * BudStretch * 0.6;
        double scale = BudStartScale + (1 - BudStartScale) * bud;

        _unfurl = Spring(elapsed - UnfurlDelayMs, UnfurlFrequency, UnfurlDamping);
        DrawShell(_unfurl, scale, along, across, BudDistance() * (1 - bud), Math.Clamp(elapsed / BudFadeMs, 0, 1));
        DrawCover(elapsed - CoverDelayMs, CoverStartScale, CoverStartTilt);

        DrawText(TitleText, TitleSlide, _titleBlur, (elapsed - TitleDelayMs) / TextMs);
        DrawText(ArtistText, ArtistSlide, _artistBlur, (elapsed - ArtistDelayMs) / TextMs);

        double bars = Spring(elapsed - BarsDelayMs, CoverFrequency, CoverDamping);
        BarsScale.ScaleX = bars;
        BarsScale.ScaleY = bars;
        BarsPanel.Opacity = Math.Min(1, bars);
        MarkScale.ScaleX = bars;
        MarkScale.ScaleY = bars;
        ClaudeMark.Opacity = Math.Min(1, bars);
    }

    private void DrawLeave(double elapsed)
    {
        // Kritik sönüm: yerini geçmez, geri yaylanmaz.
        double x = LeaveCloseFrequency * elapsed;
        _unfurl = _leaveFromUnfurl * (1 + x) * Math.Exp(-x);

        double fall = Math.Clamp((elapsed - LeaveBudDelayMs) / LeaveBudMs, 0, 1);
        fall *= fall;
        DrawShell(_unfurl, 1 + (LeaveBudScale - 1) * fall, 1, 1, BudDistance() * fall, 1 - fall);

        double text = Math.Clamp(1 - elapsed / LeaveTextMs, 0, 1);
        TitleText.Effect = null;
        ArtistText.Effect = null;
        TitleText.Opacity = Math.Min(TitleText.Opacity, text);
        ArtistText.Opacity = Math.Min(ArtistText.Opacity, text);
        BarsPanel.Opacity = Math.Min(BarsPanel.Opacity, text);
        ClaudeMark.Opacity = Math.Min(ClaudeMark.Opacity, text);
    }

    /// <summary>Camın görünen bölümü ve yeri. Yerleşim değişmez: kapak görünen kısmın başına yapışık gider, yazılar yerinde açığa çıkar.</summary>
    /// <param name="unfurl">0 kapak kadar damla, 1 tam kart; fazlası yerini geçmesi.</param>
    /// <param name="along">Çubuğa dik eksende esneme.</param>
    /// <param name="across">Çubuk boyunca esneme.</param>
    private void DrawShell(double unfurl, double scale, double along, double across, double distance, double opacity)
    {
        double full = Card.ActualWidth;
        double height = Card.ActualHeight;
        double droplet = Math.Min(height, full);
        double span = full - droplet;
        double width = unfurl <= 1 ? droplet + span * unfurl : full + Math.Min(MaxUnfurlOvershoot, span * (unfurl - 1));

        // Yan kenarda kart çubuğa yaslı açılır; altta ve üstte ortadan.
        double left = _towardBar.X < 0 ? 0 : _towardBar.X > 0 ? full - width : (full - width) / 2;
        Card.SetReveal(left, full - width - left);

        double radius = Card.CornerRadius.TopLeft;
        ChipContent.Clip = new RectangleGeometry(new Rect(left - Card.Padding.Left, 0, width, height), radius, radius);
        CoverSlide.X = left;

        bool vertical = _towardBar.Y != 0;
        CardScale.CenterX = left + width / 2;
        CardScale.CenterY = height / 2;
        CardScale.ScaleX = scale * (vertical ? across : along);
        CardScale.ScaleY = scale * (vertical ? along : across);
        BudSlide.X = _towardBar.X * distance;
        BudSlide.Y = _towardBar.Y * distance;
        Card.Opacity = opacity;
    }

    private void DrawCover(double elapsed, double startScale, double startTilt)
    {
        double cover = Spring(elapsed, CoverFrequency, CoverDamping);
        CoverScale.ScaleX = startScale + (1 - startScale) * cover;
        CoverScale.ScaleY = CoverScale.ScaleX;
        CoverTilt.Angle = startTilt * (1 - cover);
    }

    /// <summary>Kapağın altından çıkıp bulanıklıktan netleşir. Bitince efekt kalkar: yazı yeniden keskin çizilsin.</summary>
    private static void DrawText(FrameworkElement text, TranslateTransform slide, BlurEffect blur, double progress)
    {
        double eased = 1 - Math.Pow(1 - Math.Clamp(progress, 0, 1), 3);
        text.Opacity = eased;
        slide.X = -TextSlideDistance * (1 - eased);
        blur.Radius = TextBlur * (1 - eased);
        text.Effect = eased is > 0 and < 1 ? blur : null;
    }

    private double BudDistance()
    {
        return Math.Max(MinBudDistance, Card.ActualHeight * BudDistanceRatio);
    }

    /// <summary>Yay denkleminin çözümü: 0'dan başlar, 1'e oturur; sönüm 1'in altındaysa önce geçer.</summary>
    private static double Spring(double elapsed, double frequency, double damping)
    {
        if (elapsed <= 0)
        {
            return 0;
        }

        double decay = damping * frequency;
        double swing = frequency * Math.Sqrt(1 - damping * damping);
        return 1 - Math.Exp(-decay * elapsed) * (Math.Cos(swing * elapsed) + decay / swing * Math.Sin(swing * elapsed));
    }

    /// <summary>Hareketsiz son hal.</summary>
    private void SetRest()
    {
        _unfurl = 1;
        Card.ClearReveal();
        ChipContent.Clip = null;
        CardScale.ScaleX = 1;
        CardScale.ScaleY = 1;
        BudSlide.X = 0;
        BudSlide.Y = 0;
        CoverSlide.X = 0;
        CoverScale.ScaleX = 1;
        CoverScale.ScaleY = 1;
        CoverTilt.Angle = 0;
        BarsScale.ScaleX = 1;
        BarsScale.ScaleY = 1;
        BarsPanel.Opacity = 1;
        MarkScale.ScaleX = 1;
        MarkScale.ScaleY = 1;
        ClaudeMark.Opacity = 1;
        DrawText(TitleText, TitleSlide, _titleBlur, 1);
        DrawText(ArtistText, ArtistSlide, _artistBlur, 1);
        Card.Opacity = 1;
    }

    private void StartBars()
    {
        ScaleTransform[] bars = { BarScale1, BarScale2, BarScale3 };
        for (int i = 0; i < bars.Length; i++)
        {
            var bounce = new DoubleAnimation(0.25, 1, TimeSpan.FromMilliseconds(BarPeriodsMs[i]))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
            };

            // Küçük bir süs için ekran hızında çizmeye gerek yok.
            Timeline.SetDesiredFrameRate(bounce, BarFrameRate);
            bars[i].BeginAnimation(ScaleTransform.ScaleYProperty, bounce);
        }
    }

    private void StopBars()
    {
        BarScale1.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        BarScale2.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        BarScale3.BeginAnimation(ScaleTransform.ScaleYProperty, null);
    }

    private void RestartDismiss(int seconds)
    {
        _dismissTimer.Stop();
        _dismissTimer.Interval = TimeSpan.FromSeconds(seconds);
        _dismissTimer.Start();
    }

    private void DismissTimer_Tick(object? sender, EventArgs e)
    {
        // Üzerindeyken okunuyor olabilir.
        if (!IsMouseOver)
        {
            Dismiss();
        }
    }

    private void Chip_MouseEnter(object sender, MouseEventArgs e)
    {
        _dismissTimer.Stop();
    }

    private void Chip_MouseLeave(object sender, MouseEventArgs e)
    {
        if (!_isHiding)
        {
            RestartDismiss(AfterHoverSeconds);
        }
    }

    private void Chip_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        Clicked?.Invoke();
        Dismiss();
    }

    private void Chip_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        // Olay mantıksal ağaçtan çubuğa çıkıp onun menüsünü açmasın; sağ tık yalnızca kapatır.
        e.Handled = true;
        Dismiss();
    }

    [DllImport("shell32")]
    private static extern int SHQueryUserNotificationState(out int state);
}
