using System.ComponentModel;

namespace GrupBar;

/// <summary>Hızlı ayarlardaki bir kutu.</summary>
public sealed class QuickTile : INotifyPropertyChanged
{
    private bool _isOn;
    private string _label;

    public QuickTile(string glyph, string label, Action primary, Action? more = null, bool opensMenu = false)
    {
        Glyph = glyph;
        _label = label;
        Primary = primary;
        More = more;
        OpensMenu = opensMenu;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Glyph { get; }

    /// <summary>Kutunun kendisine tıklanınca: aç / kapat ya da ilgili ayarı aç.</summary>
    public Action Primary { get; }

    /// <summary>Varsa kutu ikiye bölünür, sağdaki ok bunu çalıştırır (Wi-Fi'de ağ ayarları gibi).</summary>
    public Action? More { get; }

    public bool HasMore => More != null;

    /// <summary>Kutunun tamamı başka bir yere götürüyorsa içinde küçük ok görünür.</summary>
    public bool OpensMenu { get; }

    public bool IsOn
    {
        get => _isOn;
        set
        {
            if (_isOn != value)
            {
                _isOn = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsOn)));
            }
        }
    }

    public string Label
    {
        get => _label;
        set
        {
            if (_label != value)
            {
                _label = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Label)));
            }
        }
    }
}
