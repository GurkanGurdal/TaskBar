using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace IslandBar;

/// <summary>
/// Saate tıklayınca açılan takvim kartının içi: bugünün tarihi ve ay görünümü. Windows'un saat kartı gibi yalnızca
/// gösterir, günler tıklanmaz. Haftanın ilk günü ve adlar Windows'un bölge ayarından.
/// </summary>
public partial class CalendarPanel : UserControl
{
    private const int Weeks = 6;
    private const double DayDiameter = 32;
    private const double SlideDistance = 12;
    private const int SlideMs = 170;

    private DateTime _month;
    private int _wheelRemainder;

    public CalendarPanel()
    {
        InitializeComponent();
    }

    /// <summary>Ayar açıldı, kart kapanabilir.</summary>
    public event Action? Opened;

    /// <summary>Kart her açılışta bugünden başlar; gece yarısını geçmiş olabilir.</summary>
    public void ShowToday()
    {
        DateTime today = DateTime.Today;
        TodayWeekday.Text = today.ToString("dddd", CultureInfo.CurrentCulture);
        TodayDate.Text = today.ToString("d MMMM yyyy", CultureInfo.CurrentCulture);
        FillWeekdays();
        ShowMonth(new DateTime(today.Year, today.Month, 1), direction: 0);
    }

    private void FillWeekdays()
    {
        DateTimeFormatInfo format = CultureInfo.CurrentCulture.DateTimeFormat;
        WeekdayRow.Children.Clear();
        for (int i = 0; i < 7; i++)
        {
            WeekdayRow.Children.Add(new TextBlock
            {
                Text = format.AbbreviatedDayNames[((int)format.FirstDayOfWeek + i) % 7],
                FontSize = 11,
                Foreground = (Brush)FindResource("TextSecondary"),
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 4, 0, 4)
            });
        }
    }

    /// <param name="direction">İleri için 1, geri için -1; ızgara o yönden gelir.</param>
    private void ShowMonth(DateTime month, int direction)
    {
        _month = month;
        MonthTitle.Text = month.ToString("MMMM yyyy", CultureInfo.CurrentCulture);

        DateTime today = DateTime.Today;
        DayOfWeek firstDay = CultureInfo.CurrentCulture.DateTimeFormat.FirstDayOfWeek;
        int lead = ((int)month.DayOfWeek - (int)firstDay + 7) % 7;
        DateTime day = month.AddDays(-lead);

        DayGrid.Children.Clear();
        for (int i = 0; i < Weeks * 7; i++, day = day.AddDays(1))
        {
            DayGrid.Children.Add(DayCell(day, isToday: day == today, inMonth: day.Month == month.Month));
        }

        if (direction != 0 && SystemParameters.ClientAreaAnimation)
        {
            GridSlide.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(direction * SlideDistance, 0, TimeSpan.FromMilliseconds(SlideMs))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            });
        }
    }

    /// <summary>Bugün vurgu renginde dolu daire; ayın dışındaki günler soluk.</summary>
    private Border DayCell(DateTime day, bool isToday, bool inMonth)
    {
        return new Border
        {
            Width = DayDiameter,
            Height = DayDiameter,
            Margin = new Thickness(0, 1, 0, 1),
            CornerRadius = new CornerRadius(DayDiameter / 2),
            Background = isToday ? (Brush)FindResource("AccentFill") : Brushes.Transparent,
            Opacity = inMonth || isToday ? 1 : 0.4,
            Child = new TextBlock
            {
                Text = day.Day.ToString(CultureInfo.CurrentCulture),
                FontSize = 13,
                FontWeight = isToday ? FontWeights.SemiBold : FontWeights.Normal,
                Foreground = (Brush)FindResource(isToday ? "AccentInk" : inMonth ? "TextPrimary" : "TextSecondary"),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        };
    }

    private void Previous_Click(object sender, RoutedEventArgs e)
    {
        ShowMonth(_month.AddMonths(-1), direction: -1);
    }

    private void Next_Click(object sender, RoutedEventArgs e)
    {
        ShowMonth(_month.AddMonths(1), direction: 1);
    }

    private void Today_Click(object sender, RoutedEventArgs e)
    {
        DateTime today = DateTime.Today;
        var month = new DateTime(today.Year, today.Month, 1);
        if (month != _month)
        {
            ShowMonth(month, direction: month > _month ? 1 : -1);
        }
    }

    /// <summary>Yukarı önceki ay, aşağı sonraki. Dokunmatik yüzeyin parça parça çentikleri biriktirilir.</summary>
    private void Panel_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        e.Handled = true;
        if (Math.Sign(e.Delta) != Math.Sign(_wheelRemainder))
        {
            _wheelRemainder = 0;
        }

        _wheelRemainder += e.Delta;
        int notches = _wheelRemainder / Mouse.MouseWheelDeltaForOneLine;
        _wheelRemainder -= notches * Mouse.MouseWheelDeltaForOneLine;
        if (notches != 0)
        {
            ShowMonth(_month.AddMonths(-notches), direction: -Math.Sign(notches));
        }
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        Opened?.Invoke();
        Process.Start(new ProcessStartInfo("ms-settings:dateandtime") { UseShellExecute = true })?.Dispose();
    }
}
