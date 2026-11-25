using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using Microsoft.Win32;
using Scribble.Models;
using Scribble.Services;
using Scribble.Session;

namespace Scribble.Views;

public partial class PresenterWindow : Window
{
    private readonly PresentationSession _session = new();
    private readonly List<(PenPreset Preset, Border Ring)> _penSwatchRings = new();
    private readonly string? _initialPdfPath;

    private AudienceWindow? _audienceWindow;
    private bool _isClosingConfirmed;
    private bool _isFullscreen;
    private bool _isSwappingScreens;

    public PresenterWindow(string? initialPdfPath = null)
    {
        InitializeComponent();
        _initialPdfPath = initialPdfPath;

        Viewport.AttachSession(_session);
        Viewport.ZoomStateChanged += UpdateZoomPill;

        _session.SlideChanged += UpdateSlideControls;
        _session.ActivePenChanged += UpdatePenSwatchSelection;
        _session.StrokeWidthScaleChanged += UpdateStrokeSizeUi;

        BuildPenSwatches();
        UpdateStrokeSizeUi();
        Loaded += PresenterWindow_Loaded;
        LocationChanged += PresenterWindow_LocationChanged;
    }

    private void PresenterWindow_Loaded(object sender, RoutedEventArgs e)
    {
        UpdateDisplayStatus();

        if (!string.IsNullOrEmpty(_initialPdfPath))
        {
            TryOpenPdf(_initialPdfPath);
        }
    }

    private void PresenterWindow_LocationChanged(object? sender, EventArgs e)
    {
        if (_isSwappingScreens) return;

        UpdateDisplayStatus();
        if (_audienceWindow != null && _session.IsLoaded)
        {
            PositionAudienceWindow();
        }
    }

    private void BuildPenSwatches()
    {
        PenSwatchesPanel.Children.Clear();
        _penSwatchRings.Clear();

        foreach (PenPreset preset in _session.PenPresets)
        {
            var swatchDot = new Ellipse
            {
                Width = 26,
                Height = 26,
                Fill = new SolidColorBrush(preset.Color),
                Stroke = preset.Name == "Black"
                    ? new SolidColorBrush(Color.FromRgb(0x66, 0x6D, 0x7A))
                    : Brushes.Transparent,
                StrokeThickness = 1.2
            };

            var ring = new Border
            {
                Width = 44,
                Height = 44,
                CornerRadius = new CornerRadius(22),
                BorderThickness = new Thickness(2.5),
                BorderBrush = Brushes.Transparent,
                Background = new SolidColorBrush(Color.FromRgb(0x22, 0x25, 0x2B)),
                Child = swatchDot
            };

            var btn = new Button
            {
                Content = ring,
                Margin = new Thickness(0, 5, 0, 5),
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(0),
                Cursor = Cursors.Hand,
                Focusable = false,
                ToolTip = $"{preset.Name} Pen"
            };

            // Strip default button chrome so only our clean circular ring renders
            var tpl = new ControlTemplate(typeof(Button));
            var presenterFactory = new FrameworkElementFactory(typeof(ContentPresenter));
            tpl.VisualTree = presenterFactory;
            btn.Template = tpl;

            PenPreset captured = preset;
            btn.Click += (_, _) => _session.SelectPen(captured);

            _penSwatchRings.Add((preset, ring));
            PenSwatchesPanel.Children.Add(btn);
        }

        UpdatePenSwatchSelection();
    }

    private void UpdatePenSwatchSelection()
    {
        var activeBorder = new SolidColorBrush(Color.FromRgb(0xF2, 0xF4, 0xF8));
        var eraserBorder = new SolidColorBrush(Color.FromRgb(0xF0, 0x6A, 0x7A));
        var activeBg = new SolidColorBrush(Color.FromRgb(0x30, 0x36, 0x44));
        var eraserBg = new SolidColorBrush(Color.FromRgb(0x38, 0x22, 0x28));
        var idleBg = new SolidColorBrush(Color.FromRgb(0x20, 0x23, 0x29));

        foreach (var (preset, ring) in _penSwatchRings)
        {
            bool selected = !_session.IsEraserMode && preset == _session.ActivePen;
            ring.BorderBrush = selected ? activeBorder : Brushes.Transparent;
            ring.Background = selected ? activeBg : idleBg;
        }

        if (EraserRing != null)
        {
            EraserRing.BorderBrush = _session.IsEraserMode ? eraserBorder : Brushes.Transparent;
            EraserRing.Background = _session.IsEraserMode ? eraserBg : idleBg;
        }

        UpdateStrokeSizePreviewColor();
    }

    private void UpdateStrokeSizeUi()
    {
        if (StrokeSizeSlider == null || ResetStrokeSizeButton == null || StrokeSizePreviewDot == null)
        {
            return;
        }

        double scale = _session.StrokeWidthScale;
        if (Math.Abs(StrokeSizeSlider.Value - scale) > 0.005)
        {
            StrokeSizeSlider.Value = scale;
        }

        double dotDiameter = Math.Clamp(6.5 * scale, 3.5, 20.0);
        StrokeSizePreviewDot.Width = dotDiameter;
        StrokeSizePreviewDot.Height = dotDiameter;
        UpdateStrokeSizePreviewColor();

        bool isDefault = Math.Abs(scale - 1.0) < 0.02;
        ResetStrokeSizeButton.Content = $"{scale.ToString("0.0", CultureInfo.InvariantCulture)}×";
        ResetStrokeSizeButton.ToolTip = isDefault
            ? "Stroke size (1.0× default)"
            : "Click to reset stroke size to 1.0×";
        ResetStrokeSizeButton.BorderBrush = isDefault
            ? (Brush)FindResource("BorderBrush")
            : (Brush)FindResource("AccentBrush");
        ResetStrokeSizeButton.Foreground = isDefault
            ? (Brush)FindResource("TextSecondaryBrush")
            : (Brush)FindResource("TextPrimaryBrush");
    }

    private void UpdateStrokeSizePreviewColor()
    {
        if (StrokeSizePreviewDot == null) return;

        // Keep black preview dot visible against dark rail using light neutral fill + subtle border feel
        Color c = _session.ActivePen.Name == "Black"
            ? Color.FromRgb(0xE6, 0xEA, 0xF2)
            : _session.ActivePen.Color;
        StrokeSizePreviewDot.Fill = new SolidColorBrush(c);
    }

    private void UpdateSlideControls()
    {
        if (!_session.IsLoaded)
        {
            SlideCounterText.Text = "— / —";
            PrevSlideButton.IsEnabled = false;
            NextSlideButton.IsEnabled = false;
            return;
        }

        SlideCounterText.Text = $"{_session.CurrentPageIndex + 1} / {_session.Pages.Count}";
        PrevSlideButton.IsEnabled = _session.CanGoPrevious;
        NextSlideButton.IsEnabled = _session.CanGoNext;
        Title = $"Scribble — {System.IO.Path.GetFileName(_session.SourceFilePath)} ({_session.CurrentPageIndex + 1}/{_session.Pages.Count})";
        UpdateZoomPill();
    }

    private void UpdateZoomPill()
    {
        if (_session.IsLoaded && Viewport.IsZoomedOrPanned)
        {
            string zoomLabel = Viewport.ZoomFactor.ToString("0.0", CultureInfo.InvariantCulture);
            ResetZoomPill.Content = $"Reset Zoom ({zoomLabel}× → 100%)";
            ResetZoomPill.Visibility = Visibility.Visible;
        }
        else
        {
            ResetZoomPill.Visibility = Visibility.Collapsed;
        }
    }

    private void UpdateDisplayStatus()
    {
        var monitors = MonitorService.GetMonitors();
        if (monitors.Count >= 2)
        {
            DisplayStatusText.Text = $"{monitors.Count} displays detected — Audience view will open fullscreen on Screen 2";
            SwapScreensButton.Visibility = Visibility.Visible;
        }
        else
        {
            DisplayStatusText.Text = "1 display detected — Running in standalone Presenter mode";
            SwapScreensButton.Visibility = Visibility.Collapsed;
        }
    }

    private void SwapScreens()
    {
        var monitors = MonitorService.GetMonitors();
        if (monitors.Count < 2)
        {
            UpdateDisplayStatus();
            return;
        }

        MonitorBounds? currentPresenterMonitor = MonitorService.GetMonitorForWindow(this);
        MonitorBounds? otherMonitor = MonitorService.GetSecondaryMonitor(this);
        if (currentPresenterMonitor == null || otherMonitor == null)
        {
            return;
        }

        _isSwappingScreens = true;
        try
        {
            // Move Presenter to the other display, and Audience onto the previous Presenter display
            MonitorService.MoveWindowToMonitor(this, otherMonitor, maximize: true);

            if (_audienceWindow != null && _session.IsLoaded)
            {
                _audienceWindow.ShowOnMonitor(currentPresenterMonitor);
            }

            Activate();
        }
        finally
        {
            Dispatcher.BeginInvoke(
                System.Windows.Threading.DispatcherPriority.Loaded,
                () => _isSwappingScreens = false);
        }
    }

    private void EnsureAudienceWindow()
    {
        MonitorBounds? secondary = MonitorService.GetSecondaryMonitor(this);
        if (secondary == null)
        {
            CloseAudienceWindow();
            return;
        }

        if (_audienceWindow == null)
        {
            _audienceWindow = new AudienceWindow(_session);
        }

        _audienceWindow.ShowOnMonitor(secondary);
    }

    private void PositionAudienceWindow()
    {
        MonitorBounds? secondary = MonitorService.GetSecondaryMonitor(this);
        if (secondary != null && _audienceWindow != null)
        {
            _audienceWindow.ShowOnMonitor(secondary);
        }
    }

    private void CloseAudienceWindow()
    {
        if (_audienceWindow != null)
        {
            _audienceWindow.Close();
            _audienceWindow = null;
        }
    }

    private void TryOpenPdf(string filePath)
    {
        try
        {
            _session.LoadPdf(filePath);
            WelcomeOverlay.Visibility = Visibility.Collapsed;
            UpdateSlideControls();
            EnsureAudienceWindow();
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                this,
                $"Could not open PDF:\n{ex.Message}",
                "Scribble",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private void PromptAndOpenPdf()
    {
        if (_session.HasUnsavedChanges && !ConfirmSaveOrDiscard())
        {
            return;
        }

        var dlg = new OpenFileDialog
        {
            Title = "Open Presentation PDF",
            Filter = "PDF Files (*.pdf)|*.pdf|All Files (*.*)|*.*"
        };

        if (dlg.ShowDialog(this) == true)
        {
            TryOpenPdf(dlg.FileName);
        }
    }

    /// <summary>
    /// Prompts the user to save an annotated copy if there are unsaved changes.
    /// Returns true if it is safe to proceed (saved or discarded), false if cancelled.
    /// </summary>
    private bool ConfirmSaveOrDiscard()
    {
        if (!_session.HasUnsavedChanges)
        {
            return true;
        }

        MessageBoxResult choice = MessageBox.Show(
            this,
            "You have ink annotations on these slides.\nWould you like to save them as a new annotated PDF copy?",
            "Save Annotated Copy?",
            MessageBoxButton.YesNoCancel,
            MessageBoxImage.Question);

        if (choice == MessageBoxResult.Cancel)
        {
            return false;
        }

        if (choice == MessageBoxResult.No)
        {
            return true;
        }

        return SaveAnnotatedCopyViaDialog();
    }

    private bool SaveAnnotatedCopyViaDialog()
    {
        var dlg = new SaveFileDialog
        {
            Title = "Save Annotated PDF Copy",
            Filter = "PDF Files (*.pdf)|*.pdf",
            FileName = _session.GetSuggestedAnnotatedFileName(),
            InitialDirectory = _session.GetSourceDirectory() ?? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            AddExtension = true,
            DefaultExt = ".pdf",
            OverwritePrompt = true
        };

        if (dlg.ShowDialog(this) != true)
        {
            return false;
        }

        try
        {
            _session.SaveAnnotatedCopy(dlg.FileName);
            return true;
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                this,
                $"Could not save annotated PDF:\n{ex.Message}",
                "Save Failed",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            return false;
        }
    }

    private void ToggleFullscreen()
    {
        _isFullscreen = !_isFullscreen;
        if (_isFullscreen)
        {
            WindowStyle = WindowStyle.None;
            WindowState = WindowState.Maximized;
        }
        else
        {
            WindowStyle = WindowStyle.SingleBorderWindow;
            WindowState = WindowState.Maximized;
        }
    }

    // -------------------------------------------------------------------------
    // UI Events
    // -------------------------------------------------------------------------

    private void OpenPdfButton_Click(object sender, RoutedEventArgs e) => PromptAndOpenPdf();
    private void PrevSlideButton_Click(object sender, RoutedEventArgs e) => _session.GoPrevious();
    private void NextSlideButton_Click(object sender, RoutedEventArgs e) => _session.GoNext();
    private void ResetZoomPill_Click(object sender, RoutedEventArgs e) => Viewport.ResetZoom();
    private void EraserButton_Click(object sender, RoutedEventArgs e) => _session.ToggleEraserMode();
    private void SwapScreensButton_Click(object sender, RoutedEventArgs e) => SwapScreens();
    private void ResetStrokeSizeButton_Click(object sender, RoutedEventArgs e) => _session.ResetStrokeWidthScale();
    private void StrokeSizeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) =>
        _session.SetStrokeWidthScale(e.NewValue);
    private void QuitButton_Click(object sender, RoutedEventArgs e) => Close();

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            var files = (string[]?)e.Data.GetData(DataFormats.FileDrop);
            if (files != null && files.Any(f => f.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)))
            {
                e.Effects = DragDropEffects.Copy;
                e.Handled = true;
                return;
            }
        }
        e.Effects = DragDropEffects.None;
        e.Handled = true;
    }

    private void Window_Drop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        var files = (string[]?)e.Data.GetData(DataFormats.FileDrop);
        string? pdf = files?.FirstOrDefault(f => f.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase));
        if (pdf != null)
        {
            if (_session.HasUnsavedChanges && !ConfirmSaveOrDiscard())
            {
                return;
            }
            TryOpenPdf(pdf);
        }
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;

        if (ctrl && shift && e.Key == Key.S)
        {
            SwapScreens();
            e.Handled = true;
            return;
        }

        if (ctrl && e.Key == Key.O)
        {
            PromptAndOpenPdf();
            e.Handled = true;
            return;
        }

        if (ctrl && e.Key == Key.S && _session.IsLoaded)
        {
            SaveAnnotatedCopyViaDialog();
            e.Handled = true;
            return;
        }

        if (ctrl && e.Key == Key.Z && _session.IsLoaded)
        {
            _session.Undo();
            e.Handled = true;
            return;
        }

        if (ctrl && e.Key == Key.Y && _session.IsLoaded)
        {
            _session.Redo();
            e.Handled = true;
            return;
        }

        switch (e.Key)
        {
            case Key.Right:
            case Key.Down:
            case Key.PageDown:
            case Key.Space:
                _session.GoNext();
                e.Handled = true;
                break;

            case Key.Left:
            case Key.Up:
            case Key.PageUp:
                _session.GoPrevious();
                e.Handled = true;
                break;

            case Key.E:
                _session.ToggleEraserMode();
                e.Handled = true;
                break;

            case Key.D0:
            case Key.NumPad0:
                Viewport.ResetZoom();
                e.Handled = true;
                break;

            case Key.F11:
                ToggleFullscreen();
                e.Handled = true;
                break;

            case Key.Escape:
                Close();
                e.Handled = true;
                break;
        }
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (!_isClosingConfirmed)
        {
            if (!ConfirmSaveOrDiscard())
            {
                e.Cancel = true;
                return;
            }
            _isClosingConfirmed = true;
        }

        CloseAudienceWindow();
        _session.Dispose();
    }
}
