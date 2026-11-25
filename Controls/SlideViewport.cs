using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Scribble.Models;
using Scribble.Rendering;
using Scribble.Session;

namespace Scribble.Controls;

/// <summary>
/// High-performance dual-mode slide & pressure-sensitive ink viewport.
/// - Audience mode (IsInteractive = false): locked 100% fit-to-screen, pure black surround, live wet ink.
/// - Presenter mode (IsInteractive = true): stylus-only ink + stylus eraser, 2-finger touch pinch/pan
///   with stylus-proximity palm lock, plus mouse-wheel zoom and right-drag pan for desktop testing.
/// </summary>
public sealed class SlideViewport : FrameworkElement
{
    private const double MinZoom = 1.0;
    private const double MaxZoom = 6.0;

    private readonly VisualCollection _visuals;
    private readonly DrawingVisual _backdropVisual = new();
    private readonly ContainerVisual _slideRootVisual = new();
    private readonly DrawingVisual _slideBitmapVisual = new();
    private readonly DrawingVisual _committedInkVisual = new();
    private readonly DrawingVisual _wetInkVisual = new();
    private readonly DrawingVisual _overlayVisual = new();
    private readonly MatrixTransform _slideTransform = new();

    private readonly Dictionary<Guid, StreamGeometry> _geometryCache = new();

    private PresentationSession? _session;
    private BitmapSource? _currentBitmap;
    private SlidePageInfo? _currentPage;

    // Viewport zoom/pan state (used only when IsInteractive == true)
    private double _zoom = 1.0;
    private double _panX;
    private double _panY;

    // Input state
    private bool _isStylusInRange;
    private bool _isStylusDrawing;
    private bool _isStylusErasing;
    private Point? _eraserScreenPos;
    private readonly HashSet<int> _activeTouchIds = new();

    private bool _isMouseDrawing;
    private bool _isMouseErasing;
    private bool _isMousePanning;
    private Point _lastPanMousePos;

    public bool IsInteractive { get; set; } = true;
    public double ZoomFactor => _zoom;
    public bool IsZoomedOrPanned => _zoom > 1.01 || Math.Abs(_panX) > 1.0 || Math.Abs(_panY) > 1.0;

    public event Action? ZoomStateChanged;

    public SlideViewport()
    {
        _visuals = new VisualCollection(this);
        _slideRootVisual.Transform = _slideTransform;
        _slideRootVisual.Children.Add(_slideBitmapVisual);
        _slideRootVisual.Children.Add(_committedInkVisual);
        _slideRootVisual.Children.Add(_wetInkVisual);

        _visuals.Add(_backdropVisual);
        _visuals.Add(_slideRootVisual);
        _visuals.Add(_overlayVisual);

        RenderOptions.SetBitmapScalingMode(_slideBitmapVisual, BitmapScalingMode.HighQuality);
        ClipToBounds = true;
        Focusable = false;
    }

    protected override int VisualChildrenCount => _visuals.Count;
    protected override Visual GetVisualChild(int index) => _visuals[index];

    public void AttachSession(PresentationSession session)
    {
        if (_session != null)
        {
            _session.SlideChanged -= OnSlideChanged;
            _session.StrokesChanged -= OnStrokesChanged;
            _session.WetStrokeUpdated -= OnWetStrokeUpdated;
        }

        _session = session;
        IsManipulationEnabled = IsInteractive;

        _session.SlideChanged += OnSlideChanged;
        _session.StrokesChanged += OnStrokesChanged;
        _session.WetStrokeUpdated += OnWetStrokeUpdated;

        RefreshSlide();
    }

    public void ResetZoom()
    {
        bool changed = IsZoomedOrPanned;
        _zoom = 1.0;
        _panX = 0.0;
        _panY = 0.0;
        UpdateTransform();
        if (changed)
        {
            ZoomStateChanged?.Invoke();
        }
    }

    private void OnSlideChanged()
    {
        Dispatcher.Invoke(() =>
        {
            _geometryCache.Clear();
            if (IsInteractive)
            {
                _zoom = 1.0;
                _panX = 0.0;
                _panY = 0.0;
                ZoomStateChanged?.Invoke();
            }
            RefreshSlide();
        });
    }

    private void OnStrokesChanged()
    {
        Dispatcher.Invoke(RedrawCommittedInk);
    }

    private void OnWetStrokeUpdated()
    {
        Dispatcher.Invoke(RedrawWetInk);
    }

    private void RefreshSlide()
    {
        if (_session == null || !_session.IsLoaded)
        {
            _currentBitmap = null;
            _currentPage = null;
            RedrawBackdrop();
            RedrawSlideBitmap();
            RedrawCommittedInk();
            RedrawWetInk();
            return;
        }

        _currentPage = _session.CurrentPage;
        _currentBitmap = _session.GetCurrentSlideBitmap();

        RedrawBackdrop();
        RedrawSlideBitmap();
        RedrawCommittedInk();
        RedrawWetInk();
        UpdateTransform();
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        RedrawBackdrop();
        UpdateTransform();
    }

    private (double Scale, double OffsetX, double OffsetY) GetBaseFit()
    {
        if (_currentPage == null || ActualWidth <= 1 || ActualHeight <= 1)
        {
            return (1.0, 0.0, 0.0);
        }

        double margin = IsInteractive ? 18.0 : 0.0;
        double availW = Math.Max(1.0, ActualWidth - margin * 2);
        double availH = Math.Max(1.0, ActualHeight - margin * 2);

        double scale = Math.Min(availW / _currentPage.WidthPt, availH / _currentPage.HeightPt);
        double offsetX = (ActualWidth - _currentPage.WidthPt * scale) * 0.5;
        double offsetY = (ActualHeight - _currentPage.HeightPt * scale) * 0.5;
        return (scale, offsetX, offsetY);
    }

    private void ClampPan()
    {
        if (_currentPage == null || _zoom <= 1.005)
        {
            _zoom = 1.0;
            _panX = 0.0;
            _panY = 0.0;
            return;
        }

        var (baseScale, baseX, baseY) = GetBaseFit();
        double totalScale = baseScale * _zoom;
        double slideW = _currentPage.WidthPt * totalScale;
        double slideH = _currentPage.HeightPt * totalScale;

        // Generous pan bounds so any corner of the slide can be brought near center for comfortable writing,
        // while never allowing the slide to disappear completely off-screen.
        double minOriginX = ActualWidth * 0.15 - slideW;
        double maxOriginX = ActualWidth * 0.85;
        double minOriginY = ActualHeight * 0.15 - slideH;
        double maxOriginY = ActualHeight * 0.85;

        double originX = Math.Clamp(baseX + _panX, minOriginX, maxOriginX);
        double originY = Math.Clamp(baseY + _panY, minOriginY, maxOriginY);

        _panX = originX - baseX;
        _panY = originY - baseY;
    }

    private void UpdateTransform()
    {
        if (_currentPage == null || ActualWidth <= 1 || ActualHeight <= 1)
        {
            return;
        }

        var (baseScale, baseX, baseY) = GetBaseFit();
        if (!IsInteractive)
        {
            _slideTransform.Matrix = new Matrix(baseScale, 0, 0, baseScale, baseX, baseY);
            return;
        }

        ClampPan();
        double totalScale = baseScale * _zoom;
        double originX = baseX + _panX;
        double originY = baseY + _panY;
        _slideTransform.Matrix = new Matrix(totalScale, 0, 0, totalScale, originX, originY);
    }

    private Point ScreenToSlide(Point screenPt)
    {
        Matrix inv = _slideTransform.Matrix;
        if (inv.HasInverse)
        {
            inv.Invert();
            return inv.Transform(screenPt);
        }
        return screenPt;
    }

    private void ZoomAround(Point focalScreenPt, double newZoom)
    {
        if (!IsInteractive || _currentPage == null) return;

        double clamped = Math.Clamp(newZoom, MinZoom, MaxZoom);
        if (clamped <= 1.01)
        {
            ResetZoom();
            return;
        }

        Point slideFocal = ScreenToSlide(focalScreenPt);
        var (baseScale, baseX, baseY) = GetBaseFit();

        _zoom = clamped;
        double totalScale = baseScale * _zoom;
        double newOriginX = focalScreenPt.X - slideFocal.X * totalScale;
        double newOriginY = focalScreenPt.Y - slideFocal.Y * totalScale;
        _panX = newOriginX - baseX;
        _panY = newOriginY - baseY;

        UpdateTransform();
        ZoomStateChanged?.Invoke();
    }

    private void PanBy(double dx, double dy)
    {
        if (!IsInteractive || _currentPage == null || _zoom <= 1.005) return;

        _panX += dx;
        _panY += dy;
        UpdateTransform();
        ZoomStateChanged?.Invoke();
    }

    private void RedrawBackdrop()
    {
        using var dc = _backdropVisual.RenderOpen();
        Brush bg = IsInteractive
            ? new SolidColorBrush(Color.FromRgb(0x18, 0x1A, 0x1E))
            : Brushes.Black;
        bg.Freeze();
        dc.DrawRectangle(bg, null, new Rect(0, 0, ActualWidth, ActualHeight));
    }

    private void RedrawSlideBitmap()
    {
        using var dc = _slideBitmapVisual.RenderOpen();
        if (_currentBitmap == null || _currentPage == null)
        {
            return;
        }

        var slideRect = new Rect(0, 0, _currentPage.WidthPt, _currentPage.HeightPt);
        dc.DrawRectangle(Brushes.White, null, slideRect);
        dc.DrawImage(_currentBitmap, slideRect);

        var clip = new RectangleGeometry(slideRect);
        clip.Freeze();
        _committedInkVisual.Clip = clip;
        _wetInkVisual.Clip = clip;
    }

    private void RedrawCommittedInk()
    {
        using var dc = _committedInkVisual.RenderOpen();
        if (_session == null || !_session.IsLoaded)
        {
            return;
        }

        var strokes = _session.CurrentPageStrokes;
        var activeIds = new HashSet<Guid>();

        foreach (InkStroke stroke in strokes)
        {
            activeIds.Add(stroke.Id);
            if (!_geometryCache.TryGetValue(stroke.Id, out StreamGeometry? geom))
            {
                geom = StrokeGeometryBuilder.BuildWpfGeometry(stroke);
                if (geom != null)
                {
                    _geometryCache[stroke.Id] = geom;
                }
            }

            if (geom != null)
            {
                var brush = new SolidColorBrush(stroke.Color);
                brush.Freeze();
                dc.DrawGeometry(brush, null, geom);
            }
        }

        // Prune geometries for erased strokes
        if (_geometryCache.Count > activeIds.Count)
        {
            var stale = _geometryCache.Keys.Where(id => !activeIds.Contains(id)).ToList();
            foreach (Guid id in stale)
            {
                _geometryCache.Remove(id);
            }
        }
    }

    private void RedrawWetInk()
    {
        using var dc = _wetInkVisual.RenderOpen();
        if (_session?.ActiveWetStroke is not InkStroke wet)
        {
            return;
        }

        StreamGeometry? geom = StrokeGeometryBuilder.BuildWpfGeometry(wet);
        if (geom != null)
        {
            var brush = new SolidColorBrush(wet.Color);
            brush.Freeze();
            dc.DrawGeometry(brush, null, geom);
        }
    }

    private void RedrawOverlay()
    {
        using var dc = _overlayVisual.RenderOpen();
        if ((!_isStylusErasing && !_isMouseErasing) || _eraserScreenPos is not Point pos)
        {
            return;
        }

        var fill = new SolidColorBrush(Color.FromArgb(40, 230, 70, 70));
        fill.Freeze();
        var pen = new Pen(new SolidColorBrush(Color.FromArgb(200, 230, 70, 70)), 1.5);
        pen.Freeze();
        dc.DrawEllipse(fill, pen, pos, 11, 11);
    }

    // -------------------------------------------------------------------------
    // Stylus Input (Windows Ink — strict stylus-only, ignores touch promotions)
    // -------------------------------------------------------------------------

    private static bool IsPhysicalStylus(StylusEventArgs e) =>
        e.StylusDevice?.TabletDevice?.Type == TabletDeviceType.Stylus;

    private static bool IsEraserActive(StylusEventArgs e)
    {
        if (e.Inverted)
        {
            return true;
        }

        var buttons = e.StylusDevice?.StylusButtons;
        if (buttons != null)
        {
            foreach (StylusButton btn in buttons)
            {
                if ((btn.Guid == StylusPointProperties.BarrelButton.Id ||
                     btn.Guid == StylusPointProperties.SecondaryTipButton.Id) &&
                    btn.StylusButtonState == StylusButtonState.Down)
                {
                    return true;
                }
            }
        }

        return false;
    }

    protected override void OnStylusInRange(StylusEventArgs e)
    {
        base.OnStylusInRange(e);
        if (IsInteractive && IsPhysicalStylus(e))
        {
            _isStylusInRange = true;
        }
    }

    protected override void OnStylusOutOfRange(StylusEventArgs e)
    {
        base.OnStylusOutOfRange(e);
        if (IsInteractive && IsPhysicalStylus(e))
        {
            _isStylusInRange = false;
            _eraserScreenPos = null;
            RedrawOverlay();
        }
    }

    protected override void OnStylusDown(StylusDownEventArgs e)
    {
        base.OnStylusDown(e);
        if (!IsInteractive || _session == null || !_session.IsLoaded || !IsPhysicalStylus(e))
        {
            return;
        }

        e.Handled = true;
        _isStylusInRange = true;
        CaptureStylus();

        var stylusPoints = e.GetStylusPoints(this);
        if (stylusPoints.Count == 0) return;

        if (IsEraserActive(e) || _session.IsEraserMode)
        {
            _isStylusErasing = true;
            _isStylusDrawing = false;
            EraseStylusPoints(stylusPoints);
        }
        else
        {
            _isStylusDrawing = true;
            _isStylusErasing = false;
            var first = ToInkPoint(stylusPoints[0]);
            _session.BeginStroke(first);
            for (int i = 1; i < stylusPoints.Count; i++)
            {
                _session.AppendStrokePoint(ToInkPoint(stylusPoints[i]));
            }
        }
    }

    protected override void OnStylusMove(StylusEventArgs e)
    {
        base.OnStylusMove(e);
        if (!IsInteractive || _session == null || !IsPhysicalStylus(e))
        {
            return;
        }

        if (!_isStylusDrawing && !_isStylusErasing)
        {
            return;
        }

        e.Handled = true;
        var stylusPoints = e.GetStylusPoints(this);
        if (stylusPoints.Count == 0) return;

        if (_isStylusErasing)
        {
            EraseStylusPoints(stylusPoints);
        }
        else if (_isStylusDrawing)
        {
            for (int i = 0; i < stylusPoints.Count; i++)
            {
                _session.AppendStrokePoint(ToInkPoint(stylusPoints[i]));
            }
        }
    }

    protected override void OnStylusUp(StylusEventArgs e)
    {
        base.OnStylusUp(e);
        if (!IsInteractive || _session == null || !IsPhysicalStylus(e))
        {
            return;
        }

        if (_isStylusDrawing || _isStylusErasing)
        {
            e.Handled = true;
        }

        if (_isStylusDrawing)
        {
            _isStylusDrawing = false;
            _session.EndStroke();
        }

        if (_isStylusErasing)
        {
            _isStylusErasing = false;
            _eraserScreenPos = null;
            RedrawOverlay();
        }

        if (IsStylusCaptured)
        {
            ReleaseStylusCapture();
        }
    }

    private InkPoint ToInkPoint(StylusPoint sp)
    {
        Point slidePt = ScreenToSlide(new Point(sp.X, sp.Y));
        float pressure = sp.PressureFactor;
        if (pressure <= 0.001f) pressure = 0.5f;
        return new InkPoint(slidePt.X, slidePt.Y, pressure);
    }

    private void EraseStylusPoints(StylusPointCollection points)
    {
        if (_session == null) return;
        double tolerancePt = Math.Max(4.5, 11.0 / _zoom);
        foreach (StylusPoint sp in points)
        {
            var screenPt = new Point(sp.X, sp.Y);
            _eraserScreenPos = screenPt;
            Point slidePt = ScreenToSlide(screenPt);
            _session.EraseAt(slidePt, tolerancePt);
        }
        RedrawOverlay();
    }

    // -------------------------------------------------------------------------
    // Touch Input (2-Finger Pinch-Zoom & Pan ONLY; locked out while stylus near)
    // -------------------------------------------------------------------------

    protected override void OnTouchDown(TouchEventArgs e)
    {
        base.OnTouchDown(e);
        if (!IsInteractive) return;
        _activeTouchIds.Add(e.TouchDevice.Id);
    }

    protected override void OnTouchUp(TouchEventArgs e)
    {
        base.OnTouchUp(e);
        _activeTouchIds.Remove(e.TouchDevice.Id);
    }

    protected override void OnTouchLeave(TouchEventArgs e)
    {
        base.OnTouchLeave(e);
        _activeTouchIds.Remove(e.TouchDevice.Id);
    }

    protected override void OnManipulationStarting(ManipulationStartingEventArgs e)
    {
        base.OnManipulationStarting(e);
        if (!IsInteractive || _isStylusInRange || _isStylusDrawing || _isStylusErasing)
        {
            e.Cancel();
            return;
        }

        e.ManipulationContainer = this;
        e.Mode = ManipulationModes.Scale | ManipulationModes.Translate;
        e.Handled = true;
    }

    protected override void OnManipulationDelta(ManipulationDeltaEventArgs e)
    {
        base.OnManipulationDelta(e);
        if (!IsInteractive || _session == null || !_session.IsLoaded)
        {
            return;
        }

        // Strict palm protection: ignore touch manipulation if stylus is in proximity OR fewer than 2 fingers are down
        if (_isStylusInRange || _isStylusDrawing || _isStylusErasing || _activeTouchIds.Count < 2)
        {
            e.Handled = true;
            return;
        }

        double scaleDelta = (e.DeltaManipulation.Scale.X + e.DeltaManipulation.Scale.Y) * 0.5;
        if (Math.Abs(scaleDelta - 1.0) > 0.001)
        {
            ZoomAround(e.ManipulationOrigin, _zoom * scaleDelta);
        }

        Vector trans = e.DeltaManipulation.Translation;
        if (trans.LengthSquared > 0.01)
        {
            PanBy(trans.X, trans.Y);
        }

        e.Handled = true;
    }

    // -------------------------------------------------------------------------
    // Desktop Mouse Input (Wheel Zoom + Right-Drag Pan + Optional Left-Drag Test)
    // Strictly ignores mouse events promoted from stylus/touch (StylusDevice != null)
    // -------------------------------------------------------------------------

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        if (!IsInteractive || _session == null || !_session.IsLoaded)
        {
            return;
        }

        double factor = e.Delta > 0 ? 1.15 : (1.0 / 1.15);
        ZoomAround(e.GetPosition(this), _zoom * factor);
        e.Handled = true;
    }

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        base.OnMouseDown(e);
        if (!IsInteractive || _session == null || !_session.IsLoaded || e.StylusDevice != null)
        {
            return;
        }

        if (e.ChangedButton == MouseButton.Right || e.ChangedButton == MouseButton.Middle)
        {
            _isMousePanning = true;
            _lastPanMousePos = e.GetPosition(this);
            CaptureMouse();
            Cursor = Cursors.Hand;
            e.Handled = true;
        }
        else if (e.ChangedButton == MouseButton.Left)
        {
            CaptureMouse();
            Point screenPt = e.GetPosition(this);
            Point slidePt = ScreenToSlide(screenPt);

            if (_session.IsEraserMode)
            {
                _isMouseErasing = true;
                _eraserScreenPos = screenPt;
                _session.EraseAt(slidePt, Math.Max(4.5, 11.0 / _zoom));
                RedrawOverlay();
            }
            else
            {
                _isMouseDrawing = true;
                _session.BeginStroke(new InkPoint(slidePt.X, slidePt.Y, 0.55f));
            }
            e.Handled = true;
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!IsInteractive || _session == null || e.StylusDevice != null)
        {
            return;
        }

        if (_isMousePanning)
        {
            Point pos = e.GetPosition(this);
            Vector delta = pos - _lastPanMousePos;
            _lastPanMousePos = pos;
            PanBy(delta.X, delta.Y);
            e.Handled = true;
        }
        else if (_isMouseErasing)
        {
            Point screenPt = e.GetPosition(this);
            _eraserScreenPos = screenPt;
            _session.EraseAt(ScreenToSlide(screenPt), Math.Max(4.5, 11.0 / _zoom));
            RedrawOverlay();
            e.Handled = true;
        }
        else if (_isMouseDrawing)
        {
            Point slidePt = ScreenToSlide(e.GetPosition(this));
            _session.AppendStrokePoint(new InkPoint(slidePt.X, slidePt.Y, 0.55f));
            e.Handled = true;
        }
    }

    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        base.OnMouseUp(e);
        if (!IsInteractive || _session == null || e.StylusDevice != null)
        {
            return;
        }

        if (_isMousePanning && (e.ChangedButton == MouseButton.Right || e.ChangedButton == MouseButton.Middle))
        {
            _isMousePanning = false;
            Cursor = Cursors.Arrow;
            ReleaseMouseCapture();
            e.Handled = true;
        }
        else if (_isMouseErasing && e.ChangedButton == MouseButton.Left)
        {
            _isMouseErasing = false;
            _eraserScreenPos = null;
            RedrawOverlay();
            ReleaseMouseCapture();
            e.Handled = true;
        }
        else if (_isMouseDrawing && e.ChangedButton == MouseButton.Left)
        {
            _isMouseDrawing = false;
            _session.EndStroke();
            ReleaseMouseCapture();
            e.Handled = true;
        }
    }
}
