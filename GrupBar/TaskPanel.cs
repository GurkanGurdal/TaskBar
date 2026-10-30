using System.Windows;
using System.Windows.Controls;

namespace GrupBar;

/// <summary>
/// Pencere düğmelerini dizer; yer kalmayınca Windows görev çubuğu gibi hepsini eşit olarak daraltır.
/// </summary>
public sealed class TaskPanel : Panel
{
    private const double MaxItemWidth = 180;
    private const double MinItemWidth = 36;
    private const double MaxItemHeight = 32;
    private const double MinItemHeight = 24;

    public static readonly DependencyProperty OrientationProperty = DependencyProperty.Register(
        nameof(Orientation), typeof(Orientation), typeof(TaskPanel),
        new FrameworkPropertyMetadata(Orientation.Horizontal, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public TaskPanel()
    {
        ClipToBounds = true;
    }

    public Orientation Orientation
    {
        get => (Orientation)GetValue(OrientationProperty);
        set => SetValue(OrientationProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        Size item = ItemSize(availableSize);
        double crossSize = 0;

        foreach (UIElement child in InternalChildren)
        {
            child.Measure(item);
            crossSize = Math.Max(crossSize, Orientation == Orientation.Horizontal ? child.DesiredSize.Height : child.DesiredSize.Width);
        }

        int count = InternalChildren.Count;
        return Orientation == Orientation.Horizontal
            ? new Size(item.Width * count, crossSize)
            : new Size(crossSize, item.Height * count);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        Size item = ItemSize(finalSize);
        double offset = 0;

        foreach (UIElement child in InternalChildren)
        {
            if (Orientation == Orientation.Horizontal)
            {
                child.Arrange(new Rect(offset, 0, item.Width, finalSize.Height));
                offset += item.Width;
            }
            else
            {
                child.Arrange(new Rect(0, offset, finalSize.Width, item.Height));
                offset += item.Height;
            }
        }

        return finalSize;
    }

    private Size ItemSize(Size available)
    {
        int count = Math.Max(InternalChildren.Count, 1);

        return Orientation == Orientation.Horizontal
            ? new Size(Math.Clamp(available.Width / count, MinItemWidth, MaxItemWidth), available.Height)
            : new Size(available.Width, Math.Clamp(available.Height / count, MinItemHeight, MaxItemHeight));
    }
}
