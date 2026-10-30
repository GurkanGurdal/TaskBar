using System.Windows;
using System.Windows.Controls;

namespace GrupBar;

/// <summary>
/// Pencere düğmelerini dizer; yer kalmayınca Windows görev çubuğu gibi hepsini eşit olarak daraltır.
/// </summary>
public sealed class TaskPanel : Panel
{
    private const double MinItemLength = 24;

    public static readonly DependencyProperty OrientationProperty = DependencyProperty.Register(
        nameof(Orientation), typeof(Orientation), typeof(TaskPanel),
        new FrameworkPropertyMetadata(Orientation.Horizontal, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty ItemLengthProperty = DependencyProperty.Register(
        nameof(ItemLength), typeof(double), typeof(TaskPanel),
        new FrameworkPropertyMetadata(32d, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public TaskPanel()
    {
        ClipToBounds = true;
    }

    public Orientation Orientation
    {
        get => (Orientation)GetValue(OrientationProperty);
        set => SetValue(OrientationProperty, value);
    }

    public double ItemLength
    {
        get => (double)GetValue(ItemLengthProperty);
        set => SetValue(ItemLengthProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        double length = LengthFor(availableSize);
        var item = new Size(ItemLength, ItemLength);

        if (Orientation == Orientation.Horizontal)
        {
            item.Width = length;
        }
        else
        {
            item.Height = length;
        }

        foreach (UIElement child in InternalChildren)
        {
            child.Measure(item);
        }

        double total = length * InternalChildren.Count;
        return Orientation == Orientation.Horizontal ? new Size(total, ItemLength) : new Size(ItemLength, total);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        double length = LengthFor(finalSize);
        double offset = 0;

        foreach (UIElement child in InternalChildren)
        {
            child.Arrange(Orientation == Orientation.Horizontal
                ? new Rect(offset, 0, length, finalSize.Height)
                : new Rect(0, offset, finalSize.Width, length));
            offset += length;
        }

        return finalSize;
    }

    private double LengthFor(Size available)
    {
        double room = Orientation == Orientation.Horizontal ? available.Width : available.Height;
        return Math.Clamp(room / Math.Max(InternalChildren.Count, 1), Math.Min(MinItemLength, ItemLength), ItemLength);
    }
}
