using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace IslandBar;

/// <summary>Metin değişince eskisi yukarı kayıp çıkar, yenisi alttan gelir. Yazı tipi üst öğeden miras alınır.</summary>
public sealed class RollingText : Grid
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text), typeof(string), typeof(RollingText),
        new PropertyMetadata(string.Empty, (d, e) => ((RollingText)d).Roll((string)e.OldValue, (string)e.NewValue)));

    private const char HairSpace = ' ';
    private static readonly Duration RollDuration = TimeSpan.FromMilliseconds(320);

    private readonly TextBlock _current = new() { RenderTransform = new TranslateTransform() };
    private readonly TextBlock _previous = new() { RenderTransform = new TranslateTransform(), Opacity = 0 };

    public RollingText()
    {
        ClipToBounds = true;
        Children.Add(_previous);
        Children.Add(_current);
    }

    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    private void Roll(string oldText, string newText)
    {
        _current.Text = Spaced(newText);

        // İlk yazılış canlandırılmaz.
        if (!IsLoaded || ActualHeight <= 0)
        {
            return;
        }

        _previous.Text = Spaced(oldText);

        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        double travel = ActualHeight;
        _current.RenderTransform.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(travel, 0, RollDuration) { EasingFunction = ease });
        _current.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, RollDuration));
        _previous.RenderTransform.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, -travel, RollDuration) { EasingFunction = ease });
        _previous.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0, RollDuration));
    }

    /// <summary>WPF'te harf aralığı yok: yan yana rakamların arasına kıl boşluk girer (yazı boyunun sekizde biri, <see cref="DropClock"/> da aynı aralığı kullanır).</summary>
    private static string Spaced(string text)
    {
        var spaced = new StringBuilder(text.Length + 1);
        for (int i = 0; i < text.Length; i++)
        {
            if (i > 0 && char.IsDigit(text[i - 1]) && char.IsDigit(text[i]))
            {
                spaced.Append(HairSpace);
            }
            spaced.Append(text[i]);
        }
        return spaced.ToString();
    }
}
