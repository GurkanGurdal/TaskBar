using System.ComponentModel;
using System.Windows;
using System.Windows.Media;

namespace IslandBar;

/// <summary>Hızlı ayarların alt sayfasındaki bir satır (Yansıt modu, kablosuz ekran gibi).</summary>
public sealed class QuickOption : INotifyPropertyChanged
{
    private bool _isAvailable = true;
    private bool _isSelected;

    public QuickOption(string title, string detail, Action action, string? glyph = null, string? picture = null)
    {
        Title = title;
        Detail = detail;
        Action = action;
        Glyph = glyph;
        Picture = picture == null ? null : Geometry.Parse(picture);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Title { get; }

    public string Detail { get; }

    public Visibility DetailVisibility => Detail.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

    public Action Action { get; }

    /// <summary>Yazı tipi simgesi ya da çizim; ikisinden biri.</summary>
    public string? Glyph { get; }

    public Geometry? Picture { get; }

    public bool IsAvailable
    {
        get => _isAvailable;
        set
        {
            if (_isAvailable != value)
            {
                _isAvailable = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsAvailable)));
            }
        }
    }

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected != value)
            {
                _isSelected = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
            }
        }
    }
}
