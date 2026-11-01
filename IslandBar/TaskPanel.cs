using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace IslandBar;

/// <summary>
/// Pencere düğmelerini dizer; yer kalmayınca Windows görev çubuğu gibi hepsini eşit olarak daraltır.
/// Sürükleme sırasında listeye dokunulmaz: öğeler çizim dönüşümüyle kayar, sıra bırakınca değişir. Konumlar öğeye
/// bağlıdır, listede yer değiştirince kap yeniden oluşsa da görünen yerinden devam eder.
/// </summary>
public sealed class TaskPanel : Panel
{
    private const double MinItemLength = 24;
    private const double SlideStiffness = 520;
    private const double SlideDamping = 38;
    private const double RestDistance = 0.1;
    private const double MaxStepSeconds = 1d / 30;
    private const double LiftedScale = 1.14;
    private const double GhostOpacity = 0.6;

    public static readonly DependencyProperty OrientationProperty = DependencyProperty.Register(
        nameof(Orientation), typeof(Orientation), typeof(TaskPanel),
        new FrameworkPropertyMetadata(Orientation.Horizontal, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty ItemLengthProperty = DependencyProperty.Register(
        nameof(ItemLength), typeof(double), typeof(TaskPanel),
        new FrameworkPropertyMetadata(32d, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty IconLengthProperty = DependencyProperty.Register(
        nameof(IconLength), typeof(double), typeof(TaskPanel),
        new FrameworkPropertyMetadata(20d, FrameworkPropertyMetadataOptions.AffectsRender));

    private readonly Dictionary<object, Slide> _slides = new();
    private readonly Stopwatch _clock = new();
    private double _length = 32;
    private int _gapIndex = -1;
    private int _gapSlots;
    private int _roomSlots;
    private Spring _room;
    private Spring _ghost;
    private ImageSource?[]? _ghostIcons;
    private object? _lifted;
    private double _grabOffset;
    private bool _isMoving;
    private bool _settling;

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

    public double IconLength
    {
        get => (double)GetValue(IconLengthProperty);
        set => SetValue(IconLengthProperty, value);
    }

    /// <summary>Boşluğun açık olduğu sıra; yoksa -1.</summary>
    public int GapIndex => _gapIndex;

    /// <summary>Ada genişliyor ya da öğeler kayıyor; ekran bu sırada cama alınmamalı.</summary>
    public bool IsMoving => _isMoving;

    /// <summary>
    /// Düğme gerçekten yeni mi? Listede yer değiştirince kap yeniden oluşur, bırakılan öğenin yerinde de hayaleti vardı:
    /// bunlar giriş animasyonuyla bir an kaybolmamalı.
    /// </summary>
    public bool TakeIsNew(object item)
    {
        if (!_slides.TryGetValue(item, out Slide? slide))
        {
            return true;
        }

        bool isNew = slide.IsNew && !_settling;
        slide.IsNew = false;
        return isNew;
    }

    /// <summary>İmleç ilk öğeden önce mi?</summary>
    public bool IsBefore(Point cursor)
    {
        return Along(cursor) < 0;
    }

    /// <summary>
    /// İmlecin altına açılacak boşluğun sırası. Ada ortalı olduğu için boşluk açıldıkça panel kayar; imleç boşluk tam
    /// açılmış gibi hesaplanır, yoksa açılırken komşu sıraya atlayıp titrer. Boşluğun içindeyken yerinde kalır.
    /// </summary>
    public int SlotAt(Point cursor, int slots)
    {
        int count = InternalChildren.Count;
        double position = Along(cursor);
        if (_gapIndex < 0)
        {
            return Math.Clamp((int)Math.Round(position / _length), 0, count);
        }

        double full = slots * _length;
        double settled = position + (full - _room.Value) / 2;
        if (settled < _gapIndex * _length)
        {
            return Math.Max((int)Math.Floor(settled / _length), 0);
        }
        if (settled >= _gapIndex * _length + full)
        {
            return Math.Min((int)Math.Floor(settled / _length) - slots + 1, count);
        }
        return Math.Min(_gapIndex, count);
    }

    /// <summary>Dışarıdan sürüklenen için yer açar; ada da o kadar genişler.</summary>
    public void ShowGap(int index, int slots)
    {
        if (index == _gapIndex && slots == _roomSlots)
        {
            return;
        }

        if (_ghostIcons == null || _ghostIcons.Length != slots)
        {
            _ghostIcons = new ImageSource?[slots];
        }

        // Yeni açılan boşluğun hayaleti yerinde belirir, kayarak gelmez.
        if (_room.Value <= RestDistance)
        {
            _ghost.Target = index * _length;
            _ghost.Jump();
        }

        _gapIndex = index;
        _gapSlots = slots;
        _roomSlots = slots;
        InvalidateMeasure();
        InvalidateArrange();
        StartMoving();
    }

    public void SetGhostIcon(int slot, ImageSource icon)
    {
        if (_ghostIcons != null && slot < _ghostIcons.Length)
        {
            _ghostIcons[slot] = icon;
            InvalidateVisual();
        }
    }

    public void CloseGap()
    {
        _gapIndex = -1;
        _gapSlots = 0;
        _roomSlots = 0;
        InvalidateMeasure();
        InvalidateArrange();
        StartMoving();
    }

    /// <summary>
    /// Bırakıldı: <paramref name="added"/> yeni öğe birazdan boşluğa eklenecek. Açık kalan yer öğelerin yerine geçer,
    /// tam açılmadan bırakıldıysa ada kalanı yaylanarak tamamlar.
    /// </summary>
    public void FillGap(int added)
    {
        _room.Hold(_room.Value - added * _length);
        _ghostIcons = null;
        _settling = true;
        CloseGap();
        InvalidateVisual();
    }

    /// <summary>Öğeyi kaldırır: artık fareyi izler, yeri boşluk olarak açık kalır.</summary>
    public void Lift(object item, Point cursor)
    {
        if (!_slides.TryGetValue(item, out Slide? slide))
        {
            return;
        }

        _lifted = item;
        _grabOffset = Along(cursor) - slide.Position.Value;
        _gapIndex = Math.Max(ChildIndexOf(item), 0);
        _gapSlots = 1;
        slide.Scale.Target = LiftedScale;
        InvalidateArrange();
        StartMoving();
    }

    public void MoveLifted(Point cursor)
    {
        if (_lifted == null || !_slides.TryGetValue(_lifted, out Slide? slide))
        {
            return;
        }

        double last = Math.Max(InternalChildren.Count - 1, 0);
        double position = Math.Clamp(Along(cursor) - _grabOffset, 0, last * _length);
        slide.Position.Hold(position);
        ApplySlide(_lifted, slide);

        int index = (int)Math.Round(position / _length);
        if (index != _gapIndex)
        {
            _gapIndex = index;
            InvalidateArrange();
        }
    }

    /// <summary>Bırakır; öğe durduğu yerden yerine yaylanır.</summary>
    /// <returns>İmlecin bıraktığı sıra.</returns>
    public int Release()
    {
        int index = _gapIndex;
        if (_lifted != null && _slides.TryGetValue(_lifted, out Slide? slide))
        {
            slide.Scale.Target = 1;
        }

        _lifted = null;
        _gapIndex = -1;
        _gapSlots = 0;
        _settling = true;
        InvalidateArrange();
        StartMoving();
        return index;
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

        double total = Math.Max(length * InternalChildren.Count + _room.Value, 0);
        return Orientation == Orientation.Horizontal ? new Size(total, ItemLength) : new Size(ItemLength, total);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        _length = LengthFor(finalSize);
        _room.Target = _roomSlots * _length;
        _ghost.Target = _gapIndex >= 0 ? _gapIndex * _length : _ghost.Target;

        bool animate = _isMoving || _settling || _gapIndex >= 0;
        bool moved = _room.Value != _room.Target || _ghost.Value != _ghost.Target;
        int slot = 0;

        foreach (UIElement child in InternalChildren)
        {
            object item = ItemOf(child);
            bool lifted = Equals(item, _lifted);
            double target;
            if (lifted)
            {
                target = _gapIndex * _length;
            }
            else
            {
                if (slot == _gapIndex)
                {
                    slot += _gapSlots;
                }
                target = slot++ * _length;
            }

            child.Arrange(Orientation == Orientation.Horizontal
                ? new Rect(target, 0, _length, finalSize.Height)
                : new Rect(0, target, finalSize.Width, _length));

            if (!_slides.TryGetValue(item, out Slide? slide))
            {
                slide = new Slide(target);
                _slides[item] = slide;
            }

            slide.Position.Target = target;
            if (!lifted)
            {
                if (!animate)
                {
                    slide.Position.Jump();
                }
                moved |= slide.Position.Value != target;
            }
            SetZIndex(child, lifted ? 1 : 0);
            slide.Offset = slide.Position.Value - target;
            Apply(child, slide);
        }

        if (_slides.Count > InternalChildren.Count)
        {
            PruneSlides();
        }

        if (animate && moved)
        {
            StartMoving();
        }
        return finalSize;
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);

        // Bırakılınca neyin geleceği boşlukta görünsün.
        if (_ghostIcons == null || _room.Value <= 0)
        {
            return;
        }

        double progress = Math.Clamp(_room.Value / (_ghostIcons.Length * _length), 0, 1);
        double icon = Math.Min(IconLength, _length);
        double along = _ghost.Value + (_length - icon) / 2;
        double across = (ItemLength - icon) / 2;

        drawingContext.PushOpacity(GhostOpacity * progress);
        foreach (ImageSource? image in _ghostIcons)
        {
            if (image != null)
            {
                drawingContext.DrawImage(image, Orientation == Orientation.Horizontal
                    ? new Rect(along, across, icon, icon)
                    : new Rect(across, along, icon, icon));
            }
            along += _length;
        }
        drawingContext.Pop();
    }

    private void StartMoving()
    {
        if (!SystemParameters.ClientAreaAnimation)
        {
            JumpAll();
            return;
        }

        if (!_isMoving)
        {
            _isMoving = true;
            _clock.Restart();
            CompositionTarget.Rendering += CompositionTarget_Rendering;
        }
    }

    private void JumpAll()
    {
        foreach ((object item, Slide slide) in _slides)
        {
            if (!Equals(item, _lifted))
            {
                slide.Position.Jump();
            }
            slide.Scale.Jump();
        }

        _room.Target = _roomSlots * _length;
        _room.Jump();
        _ghost.Jump();
        _settling = false;
        if (_gapIndex < 0)
        {
            _ghostIcons = null;
        }
        InvalidateMeasure();
        InvalidateArrange();
        InvalidateVisual();
    }

    private void CompositionTarget_Rendering(object? sender, EventArgs e)
    {
        double step = Math.Min(_clock.Elapsed.TotalSeconds, MaxStepSeconds);
        _clock.Restart();
        bool moving = false;

        foreach ((object item, Slide slide) in _slides)
        {
            if (!Equals(item, _lifted))
            {
                moving |= slide.Position.Advance(step, SlideStiffness, SlideDamping, RestDistance);
            }
            moving |= slide.Scale.Advance(step, SlideStiffness, SlideDamping, RestDistance / 100);
        }

        foreach (UIElement child in InternalChildren)
        {
            if (_slides.TryGetValue(ItemOf(child), out Slide? slide))
            {
                slide.Offset = slide.Position.Value - slide.Position.Target;
                Apply(child, slide);
            }
        }

        double room = _room.Value;
        moving |= _room.Advance(step, SlideStiffness, SlideDamping, RestDistance);
        if (_room.Value != room)
        {
            InvalidateMeasure();
        }

        moving |= _ghost.Advance(step, SlideStiffness, SlideDamping, RestDistance);
        if (_ghostIcons != null)
        {
            InvalidateVisual();
        }

        if (!moving)
        {
            _isMoving = false;
            _settling = false;
            CompositionTarget.Rendering -= CompositionTarget_Rendering;
            if (_gapIndex < 0)
            {
                _ghostIcons = null;
                InvalidateVisual();
            }
        }
    }

    private void ApplySlide(object item, Slide slide)
    {
        slide.Offset = slide.Position.Value - slide.Position.Target;
        int index = ChildIndexOf(item);
        if (index >= 0)
        {
            Apply(InternalChildren[index], slide);
        }
    }

    private void Apply(UIElement child, Slide slide)
    {
        if (child.RenderTransform is not MatrixTransform { IsFrozen: false } transform)
        {
            transform = new MatrixTransform();
            child.RenderTransform = transform;
        }

        Matrix matrix = Matrix.Identity;
        Size size = child.RenderSize;
        matrix.ScaleAt(slide.Scale.Value, slide.Scale.Value, size.Width / 2, size.Height / 2);
        if (Orientation == Orientation.Horizontal)
        {
            matrix.Translate(slide.Offset, 0);
        }
        else
        {
            matrix.Translate(0, slide.Offset);
        }
        transform.Matrix = matrix;
    }

    private void PruneSlides()
    {
        var alive = new HashSet<object>();
        foreach (UIElement child in InternalChildren)
        {
            alive.Add(ItemOf(child));
        }

        foreach (object item in _slides.Keys.Where(key => !alive.Contains(key)).ToList())
        {
            _slides.Remove(item);
        }
    }

    private int ChildIndexOf(object item)
    {
        for (int i = 0; i < InternalChildren.Count; i++)
        {
            if (Equals(ItemOf(InternalChildren[i]), item))
            {
                return i;
            }
        }
        return -1;
    }

    private static object ItemOf(UIElement child)
    {
        return ((FrameworkElement)child).DataContext ?? child;
    }

    private double Along(Point point)
    {
        return Orientation == Orientation.Horizontal ? point.X : point.Y;
    }

    private double LengthFor(Size available)
    {
        double room = Orientation == Orientation.Horizontal ? available.Width : available.Height;
        return Math.Clamp(room / Math.Max(InternalChildren.Count + _roomSlots, 1), Math.Min(MinItemLength, ItemLength), ItemLength);
    }

    private sealed class Slide
    {
        public Spring Position;
        public Spring Scale = new() { Value = 1, Target = 1 };
        public double Offset;
        public bool IsNew = true;

        public Slide(double position)
        {
            Position = new Spring { Value = position, Target = position };
        }
    }
}
