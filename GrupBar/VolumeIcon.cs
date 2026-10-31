using System.Windows.Controls;

namespace GrupBar;

/// <summary>
/// Windows'taki ses simgesi: düzey ne olursa olsun tam dalgalar silik olarak görünür, üstüne geçerli düzey
/// çizilir. Yalnızca geçerli düzey çizilse simge az seste sola kaymış durur. Yazı tipi üst öğeden gelir.
/// </summary>
public sealed class VolumeIcon : Grid
{
    public const string MutedGlyph = "";
    private const string FullGlyph = "";
    private const double FadedOpacity = 0.35;

    private readonly TextBlock _faded = new() { Text = FullGlyph, Opacity = FadedOpacity };
    private readonly TextBlock _level = new() { Text = FullGlyph };

    public VolumeIcon()
    {
        Children.Add(_faded);
        Children.Add(_level);
    }

    public string Glyph
    {
        set
        {
            _level.Text = value;
            _faded.Visibility = value == MutedGlyph ? System.Windows.Visibility.Hidden : System.Windows.Visibility.Visible;
        }
    }
}
