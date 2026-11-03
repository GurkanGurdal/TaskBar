using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace IslandBar;

/// <summary>
/// Saate tıklayınca açılan takvim kartının içi: bugünün tarihi, ay görünümü ve hatırlatıcılar. Güne tıklamak onu seçer;
/// altta o günün hatırlatıcıları ve o güne ekleme. Hatırlatıcısı olan günün altında kehribar nokta. Haftanın ilk günü
/// ve adlar Windows'un bölge ayarından.
/// </summary>
public partial class CalendarPanel : UserControl
{
    private const int Weeks = 6;
    private const double DayDiameter = 32;
    private const double SlideDistance = 12;
    private const int SlideMs = 170;
    private const double ReminderDotSize = 4;
    private const string DefaultHint = "Güne tıklayıp o güne ekle. Zamanı gelince kart sen Tamam diyene kadar kalır.";

    private readonly ReminderStore _reminders = ReminderStore.Shared;
    private DateTime _month;
    private DateTime _selected = DateTime.Today;
    private int _wheelRemainder;

    public CalendarPanel()
    {
        InitializeComponent();

        // Liste uygulama boyunca yaşıyor, çubuklar yeniden açılabiliyor: kapanan kart listeye bağlı kalmasın.
        Loaded += (_, _) => _reminders.Items.CollectionChanged += Reminders_CollectionChanged;
        Unloaded += (_, _) => _reminders.Items.CollectionChanged -= Reminders_CollectionChanged;
    }

    private void Reminders_CollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        if (IsVisible)
        {
            ShowMonth(_month, direction: 0);
        }
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
        _selected = today;
        ReminderText.Clear();
        ReminderTime.Text = DateTime.Now.AddHours(1).ToString("HH:00", CultureInfo.CurrentCulture);
        ReminderHint.Text = DefaultHint;
        ShowMonth(new DateTime(today.Year, today.Month, 1), direction: 0);
    }

    /// <summary>Seçili günün hatırlatıcıları; başlıkta gün.</summary>
    private void ShowReminders()
    {
        ReminderDay.Text = _selected == DateTime.Today ? "Bugün"
            : _selected == DateTime.Today.AddDays(1) ? "Yarın"
            : _selected.ToString("d MMMM ddd", CultureInfo.CurrentCulture);
        ReminderList.ItemsSource = _reminders.On(_selected).ToList();
    }

    private void Day_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        _selected = (DateTime)((FrameworkElement)sender).Tag;
        ShowMonth(_month, direction: 0);
        ReminderText.Focus();
    }

    private void AddReminder_Click(object sender, RoutedEventArgs e)
    {
        string text = ReminderText.Text.Trim();
        if (text.Length == 0)
        {
            ReminderHint.Text = "Ne hatırlatılsın? Önce metni yaz.";
            ReminderText.Focus();
            return;
        }

        if (!TryParseTime(ReminderTime.Text, out TimeSpan time))
        {
            ReminderHint.Text = "Saati 18:30 gibi yaz.";
            ReminderTime.Focus();
            return;
        }

        DateTime dueAt = _selected.Date + time;
        if (dueAt <= DateTime.Now)
        {
            ReminderHint.Text = "Bu saat geçti; ileri bir saat ya da gün seç.";
            ReminderTime.Focus();
            return;
        }

        _reminders.Add(text, dueAt);
        ReminderText.Clear();
        ReminderHint.Text = $"{dueAt.ToString("d MMMM HH:mm", CultureInfo.CurrentCulture)} için eklendi.";
    }

    /// <summary>"18:30", "18.30", "1830" ve yalnızca saat ("9") kabul edilir.</summary>
    private static bool TryParseTime(string input, out TimeSpan time)
    {
        time = TimeSpan.Zero;
        System.Text.RegularExpressions.Match match = System.Text.RegularExpressions.Regex.Match(input.Trim(), @"^(\d{1,2})(?:[:.]?(\d{2}))?$");
        if (!match.Success)
        {
            return false;
        }

        int hours = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        int minutes = match.Groups[2].Success ? int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture) : 0;
        if (hours > 23 || minutes > 59)
        {
            return false;
        }

        time = new TimeSpan(hours, minutes, 0);
        return true;
    }

    private void ReminderField_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            AddReminder_Click(sender, e);
        }
    }

    private void DeleteReminder_Click(object sender, RoutedEventArgs e)
    {
        _reminders.Remove((Reminder)((FrameworkElement)sender).DataContext);
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

        var reminderDays = new HashSet<DateTime>(_reminders.Items.Select(reminder => reminder.DueAt.Date));
        DayGrid.Children.Clear();
        for (int i = 0; i < Weeks * 7; i++, day = day.AddDays(1))
        {
            DayGrid.Children.Add(DayCell(day, isToday: day == today, inMonth: day.Month == month.Month, reminderDays.Contains(day)));
        }
        ShowReminders();

        if (direction != 0 && SystemParameters.ClientAreaAnimation)
        {
            GridSlide.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(direction * SlideDistance, 0, TimeSpan.FromMilliseconds(SlideMs))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            });
        }
    }

    /// <summary>
    /// Bugün vurgu renginde dolu daire; ayın dışındaki günler soluk. Seçili gün ince halka, hatırlatıcısı olan gün
    /// altında kehribar nokta.
    /// </summary>
    private Border DayCell(DateTime day, bool isToday, bool inMonth, bool hasReminder)
    {
        bool selected = day == _selected && !isToday;
        var content = new Grid();
        content.Children.Add(new TextBlock
        {
            Text = day.Day.ToString(CultureInfo.CurrentCulture),
            FontSize = 13,
            FontWeight = isToday ? FontWeights.SemiBold : FontWeights.Normal,
            Foreground = (Brush)FindResource(isToday ? "AccentInk" : inMonth ? "TextPrimary" : "TextSecondary"),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        });
        if (hasReminder)
        {
            content.Children.Add(new System.Windows.Shapes.Ellipse
            {
                Width = ReminderDotSize,
                Height = ReminderDotSize,
                Margin = new Thickness(0, 0, 0, 3),
                Fill = (Brush)FindResource(isToday ? "AccentInk" : "AttentionFill"),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Bottom
            });
        }

        var cell = new Border
        {
            Width = DayDiameter,
            Height = DayDiameter,
            Margin = new Thickness(0, 1, 0, 1),
            CornerRadius = new CornerRadius(DayDiameter / 2),
            Background = isToday ? (Brush)FindResource("AccentFill") : Brushes.Transparent,
            BorderBrush = selected ? (Brush)FindResource("TextSecondary") : null,
            BorderThickness = new Thickness(selected ? 1 : 0),
            Opacity = inMonth || isToday ? 1 : 0.4,
            Cursor = Cursors.Hand,
            Tag = day,
            Child = content
        };
        cell.MouseLeftButtonDown += Day_MouseLeftButtonDown;
        return cell;
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
