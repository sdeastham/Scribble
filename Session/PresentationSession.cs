using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using Scribble.Models;
using Scribble.Services;

namespace Scribble.Session;

/// <summary>
/// Single source of truth shared between PresenterWindow (Screen 1) and AudienceWindow (Screen 2).
/// Coordinates slide navigation, committed strokes, live 'wet' stroke mirroring, undo/redo, and saving.
/// </summary>
public sealed class PresentationSession : IDisposable
{
    private PdfSlideRenderer? _renderer;
    private byte[]? _sourcePdfBytes;
    private readonly Dictionary<int, List<InkStroke>> _strokesByPage = new();
    private readonly Stack<HistoryEntry> _undoStack = new();
    private readonly Stack<HistoryEntry> _redoStack = new();

    private enum HistoryAction { Add, Remove }
    private sealed record HistoryEntry(int PageIndex, HistoryAction Action, InkStroke Stroke);

    public string? SourceFilePath { get; private set; }
    public IReadOnlyList<SlidePageInfo> Pages { get; private set; } = [];
    public int CurrentPageIndex { get; private set; }
    public bool IsLoaded => _renderer != null && Pages.Count > 0;
    public bool HasUnsavedChanges { get; private set; }

    public IReadOnlyList<PenPreset> PenPresets => PenPreset.Defaults;
    public PenPreset ActivePen { get; private set; } = PenPreset.Defaults[0];
    public bool IsEraserMode { get; private set; }
    public double StrokeWidthScale { get; private set; } = 1.0;
    public InkStroke? ActiveWetStroke { get; private set; }

    public SlidePageInfo? CurrentPage =>
        IsLoaded && CurrentPageIndex >= 0 && CurrentPageIndex < Pages.Count
            ? Pages[CurrentPageIndex]
            : null;

    public IReadOnlyList<InkStroke> CurrentPageStrokes =>
        _strokesByPage.TryGetValue(CurrentPageIndex, out var list) ? list : [];

    public event Action? SlideChanged;
    public event Action? StrokesChanged;
    public event Action? WetStrokeUpdated;
    public event Action? ActivePenChanged;
    public event Action? StrokeWidthScaleChanged;

    public void LoadPdf(string filePath)
    {
        LoadedPdfData data = PdfInkStorage.Load(filePath);

        _renderer?.Dispose();
        _renderer = new PdfSlideRenderer(data.RawBytes);
        _sourcePdfBytes = data.RawBytes;
        SourceFilePath = Path.GetFullPath(filePath);
        Pages = data.Pages;

        _strokesByPage.Clear();
        foreach (var kv in data.ExistingStrokes)
        {
            _strokesByPage[kv.Key] = kv.Value;
        }
        for (int i = 0; i < Pages.Count; i++)
        {
            if (!_strokesByPage.ContainsKey(i))
            {
                _strokesByPage[i] = [];
            }
        }

        _undoStack.Clear();
        _redoStack.Clear();
        ActiveWetStroke = null;
        HasUnsavedChanges = false;
        CurrentPageIndex = 0;

        _renderer.PrefetchAdjacent(0);
        SlideChanged?.Invoke();
    }

    public BitmapSource? GetCurrentSlideBitmap()
    {
        if (!IsLoaded || _renderer == null)
        {
            return null;
        }
        return _renderer.GetSlideBitmap(CurrentPageIndex);
    }

    public bool CanGoPrevious => IsLoaded && CurrentPageIndex > 0;
    public bool CanGoNext => IsLoaded && CurrentPageIndex < Pages.Count - 1;

    public void GoPrevious()
    {
        if (!CanGoPrevious) return;
        ActiveWetStroke = null;
        CurrentPageIndex--;
        _renderer?.PrefetchAdjacent(CurrentPageIndex);
        SlideChanged?.Invoke();
    }

    public void GoNext()
    {
        if (!CanGoNext) return;
        ActiveWetStroke = null;
        CurrentPageIndex++;
        _renderer?.PrefetchAdjacent(CurrentPageIndex);
        SlideChanged?.Invoke();
    }

    public void SelectPen(PenPreset pen)
    {
        if (ActivePen == pen && !IsEraserMode) return;
        ActivePen = pen;
        IsEraserMode = false;
        ActivePenChanged?.Invoke();
    }

    public void SetEraserMode(bool enabled)
    {
        if (IsEraserMode == enabled) return;
        IsEraserMode = enabled;
        ActivePenChanged?.Invoke();
    }

    public void ToggleEraserMode() => SetEraserMode(!IsEraserMode);

    public void SetStrokeWidthScale(double scale)
    {
        double clamped = Math.Clamp(scale, 0.4, 3.0);
        if (Math.Abs(StrokeWidthScale - clamped) < 0.001) return;
        StrokeWidthScale = clamped;
        StrokeWidthScaleChanged?.Invoke();
    }

    public void ResetStrokeWidthScale() => SetStrokeWidthScale(1.0);

    public void BeginStroke(InkPoint startPoint)
    {
        if (!IsLoaded || CurrentPage == null) return;

        // Scale base pen width cleanly relative to standard 960pt 16:9 PowerPoint slide width
        double slideScale = Math.Clamp(CurrentPage.WidthPt / 960.0, 0.5, 2.5);
        double widthPt = ActivePen.BaseWidth * slideScale * StrokeWidthScale;

        var stroke = new InkStroke(ActivePen.Color, widthPt);
        stroke.AddPoint(startPoint);
        ActiveWetStroke = stroke;
        WetStrokeUpdated?.Invoke();
    }

    public void AppendStrokePoint(InkPoint point)
    {
        if (ActiveWetStroke == null) return;
        ActiveWetStroke.AddPoint(point);
        WetStrokeUpdated?.Invoke();
    }

    public void EndStroke()
    {
        if (ActiveWetStroke == null) return;
        var finished = ActiveWetStroke;
        ActiveWetStroke = null;

        if (finished.Points.Count > 0)
        {
            var list = GetOrCreatePageStrokes(CurrentPageIndex);
            list.Add(finished);
            _undoStack.Push(new HistoryEntry(CurrentPageIndex, HistoryAction.Add, finished));
            _redoStack.Clear();
            HasUnsavedChanges = true;
        }

        WetStrokeUpdated?.Invoke();
        StrokesChanged?.Invoke();
    }

    public void CancelWetStroke()
    {
        if (ActiveWetStroke == null) return;
        ActiveWetStroke = null;
        WetStrokeUpdated?.Invoke();
    }

    public bool EraseAt(Point slidePoint, double tolerancePt)
    {
        if (!IsLoaded) return false;
        var list = GetOrCreatePageStrokes(CurrentPageIndex);
        bool erasedAny = false;

        for (int i = list.Count - 1; i >= 0; i--)
        {
            if (list[i].HitTest(slidePoint, tolerancePt))
            {
                InkStroke removed = list[i];
                list.RemoveAt(i);
                _undoStack.Push(new HistoryEntry(CurrentPageIndex, HistoryAction.Remove, removed));
                _redoStack.Clear();
                HasUnsavedChanges = true;
                erasedAny = true;
            }
        }

        if (erasedAny)
        {
            StrokesChanged?.Invoke();
        }
        return erasedAny;
    }

    public void Undo()
    {
        if (_undoStack.Count == 0) return;
        var entry = _undoStack.Pop();
        var list = GetOrCreatePageStrokes(entry.PageIndex);

        if (entry.Action == HistoryAction.Add)
        {
            list.Remove(entry.Stroke);
        }
        else
        {
            list.Add(entry.Stroke);
        }

        _redoStack.Push(entry);
        HasUnsavedChanges = true;
        if (entry.PageIndex == CurrentPageIndex)
        {
            StrokesChanged?.Invoke();
        }
    }

    public void Redo()
    {
        if (_redoStack.Count == 0) return;
        var entry = _redoStack.Pop();
        var list = GetOrCreatePageStrokes(entry.PageIndex);

        if (entry.Action == HistoryAction.Add)
        {
            list.Add(entry.Stroke);
        }
        else
        {
            list.Remove(entry.Stroke);
        }

        _undoStack.Push(entry);
        HasUnsavedChanges = true;
        if (entry.PageIndex == CurrentPageIndex)
        {
            StrokesChanged?.Invoke();
        }
    }

    public string GetSuggestedAnnotatedFileName()
    {
        if (string.IsNullOrEmpty(SourceFilePath))
        {
            return "Presentation_annotated.pdf";
        }

        string stem = Path.GetFileNameWithoutExtension(SourceFilePath);
        if (stem.EndsWith("_annotated", StringComparison.OrdinalIgnoreCase))
        {
            return $"{stem}.pdf";
        }
        return $"{stem}_annotated.pdf";
    }

    public string? GetSourceDirectory() =>
        string.IsNullOrEmpty(SourceFilePath) ? null : Path.GetDirectoryName(SourceFilePath);

    public void SaveAnnotatedCopy(string destinationPath)
    {
        if (_sourcePdfBytes == null)
        {
            throw new InvalidOperationException("No PDF document is loaded.");
        }

        PdfInkStorage.SaveAnnotatedCopy(_sourcePdfBytes, destinationPath, _strokesByPage);
        HasUnsavedChanges = false;
    }

    private List<InkStroke> GetOrCreatePageStrokes(int pageIndex)
    {
        if (!_strokesByPage.TryGetValue(pageIndex, out var list))
        {
            list = [];
            _strokesByPage[pageIndex] = list;
        }
        return list;
    }

    public void Dispose()
    {
        _renderer?.Dispose();
        _renderer = null;
    }
}
