using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Media;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using PdfSharp.Pdf.IO;
using Scribble.Models;
using Scribble.Rendering;

namespace Scribble.Services;

public sealed record LoadedPdfData(
    byte[] RawBytes,
    IReadOnlyList<SlidePageInfo> Pages,
    Dictionary<int, List<InkStroke>> ExistingStrokes);

/// <summary>
/// Loads PDF page geometry and editable Ink Annotations (/Subtype /Ink), and saves
/// annotated copies with both standard PDF Ink metadata and exact variable-width
/// vector Appearance Streams (/AP /N).
/// </summary>
public static class PdfInkStorage
{
    private const string ScribblePointsKey = "/ScribblePoints";
    private const string ScribbleWidthKey = "/ScribbleWidth";

    public static LoadedPdfData Load(string pdfPath)
    {
        byte[] bytes = File.ReadAllBytes(pdfPath);
        using var ms = new MemoryStream(bytes, writable: false);
        using PdfDocument doc = PdfReader.Open(ms, PdfDocumentOpenMode.Import);

        var pages = new List<SlidePageInfo>(doc.PageCount);
        var strokesByPage = new Dictionary<int, List<InkStroke>>();

        for (int i = 0; i < doc.PageCount; i++)
        {
            PdfPage page = doc.Pages[i];
            (double w, double h) = GetEffectivePageSizePt(page);
            pages.Add(new SlidePageInfo(i, w, h));

            var pageStrokes = ReadPageInkAnnotations(page, h);
            strokesByPage[i] = pageStrokes;
        }

        return new LoadedPdfData(bytes, pages, strokesByPage);
    }

    public static void SaveAnnotatedCopy(
        byte[] sourcePdfBytes,
        string destinationPath,
        IReadOnlyDictionary<int, List<InkStroke>> strokesByPage)
    {
        string fullDest = Path.GetFullPath(destinationPath);
        string? dir = Path.GetDirectoryName(fullDest);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        string tempPath = Path.Combine(
            dir ?? Path.GetTempPath(),
            $".scribble_tmp_{Guid.NewGuid():N}.pdf");

        try
        {
            using (var ms = new MemoryStream(sourcePdfBytes, writable: false))
            using (PdfDocument doc = PdfReader.Open(ms, PdfDocumentOpenMode.Modify))
            {
                for (int i = 0; i < doc.PageCount; i++)
                {
                    PdfPage page = doc.Pages[i];
                    (_, double pageHeightPt) = GetEffectivePageSizePt(page);

                    RemoveExistingInkAnnotations(page);

                    if (strokesByPage.TryGetValue(i, out var strokes) && strokes.Count > 0)
                    {
                        PdfArray annotsArray = GetOrCreateAnnotsArray(doc, page);
                        foreach (InkStroke stroke in strokes)
                        {
                            if (stroke.Points.Count == 0)
                            {
                                continue;
                            }

                            PdfDictionary annot = BuildInkAnnotation(doc, stroke, pageHeightPt);
                            doc.Internals.AddObject(annot);
                            annotsArray.Elements.Add(annot.Reference!);
                        }
                    }
                }

                doc.Save(tempPath);
            }

            File.Move(tempPath, fullDest, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                try { File.Delete(tempPath); } catch { /* ignore cleanup error */ }
            }
        }
    }

    private static (double WidthPt, double HeightPt) GetEffectivePageSizePt(PdfPage page)
    {
        double w = page.Width.Point;
        double h = page.Height.Point;
        int rot = ((page.Rotate % 360) + 360) % 360;
        if (rot == 90 || rot == 270)
        {
            return (h, w);
        }
        return (w, h);
    }

    private static List<InkStroke> ReadPageInkAnnotations(PdfPage page, double pageHeightPt)
    {
        var result = new List<InkStroke>();
        PdfArray? annots = ResolveArray(page.Elements["/Annots"]);
        if (annots == null)
        {
            return result;
        }

        foreach (PdfItem item in annots.Elements)
        {
            PdfDictionary? annot = ResolveDictionary(item);
            if (annot == null)
            {
                continue;
            }

            string? subtype = annot.Elements.GetName("/Subtype");
            if (!string.Equals(subtype, "/Ink", StringComparison.Ordinal))
            {
                continue;
            }

            Color color = ReadColor(annot);
            Guid id = ReadStrokeId(annot);

            // Prefer lossless Scribble point+pressure data when available
            string? scribblePointsRaw = annot.Elements.GetString(ScribblePointsKey);
            if (!string.IsNullOrWhiteSpace(scribblePointsRaw))
            {
                double baseWidth = annot.Elements.ContainsKey(ScribbleWidthKey)
                    ? annot.Elements.GetReal(ScribbleWidthKey)
                    : ReadBorderWidth(annot);

                var pts = DecodeScribblePoints(scribblePointsRaw);
                if (pts.Count > 0)
                {
                    result.Add(new InkStroke(color, baseWidth > 0.2 ? baseWidth : 2.6, pts, id));
                    continue;
                }
            }

            // Fallback: import standard PDF /InkList created by other PDF editors
            double fallbackWidth = ReadBorderWidth(annot);
            PdfArray? inkList = ResolveArray(annot.Elements["/InkList"]);
            if (inkList == null)
            {
                continue;
            }

            foreach (PdfItem pathItem in inkList.Elements)
            {
                PdfArray? coords = ResolveArray(pathItem);
                if (coords == null || coords.Elements.Count < 2)
                {
                    continue;
                }

                var pts = new List<InkPoint>(coords.Elements.Count / 2);
                for (int c = 0; c + 1 < coords.Elements.Count; c += 2)
                {
                    double x = ReadNumber(coords.Elements[c]);
                    double pdfY = ReadNumber(coords.Elements[c + 1]);
                    pts.Add(new InkPoint(x, pageHeightPt - pdfY, 0.55f));
                }

                if (pts.Count > 0)
                {
                    result.Add(new InkStroke(color, fallbackWidth, pts));
                }
            }
        }

        return result;
    }

    private static void RemoveExistingInkAnnotations(PdfPage page)
    {
        PdfArray? annots = ResolveArray(page.Elements["/Annots"]);
        if (annots == null)
        {
            return;
        }

        for (int i = annots.Elements.Count - 1; i >= 0; i--)
        {
            PdfDictionary? annot = ResolveDictionary(annots.Elements[i]);
            if (annot != null && string.Equals(annot.Elements.GetName("/Subtype"), "/Ink", StringComparison.Ordinal))
            {
                annots.Elements.RemoveAt(i);
            }
        }
    }

    private static PdfArray GetOrCreateAnnotsArray(PdfDocument doc, PdfPage page)
    {
        PdfArray? existing = ResolveArray(page.Elements["/Annots"]);
        if (existing != null)
        {
            return existing;
        }

        var created = new PdfArray(doc);
        page.Elements["/Annots"] = created;
        return created;
    }

    private static PdfDictionary BuildInkAnnotation(PdfDocument doc, InkStroke stroke, double pageHeightPt)
    {
        var poly = StrokeGeometryBuilder.BuildOutlinePolygon(stroke);

        double minX = double.MaxValue, minY = double.MaxValue;
        double maxX = double.MinValue, maxY = double.MinValue;

        if (poly.Count > 0)
        {
            foreach (Point p in poly)
            {
                double pdfY = pageHeightPt - p.Y;
                if (p.X < minX) minX = p.X;
                if (p.X > maxX) maxX = p.X;
                if (pdfY < minY) minY = pdfY;
                if (pdfY > maxY) maxY = pdfY;
            }
        }
        else
        {
            foreach (InkPoint p in stroke.Points)
            {
                double pdfY = pageHeightPt - p.Y;
                if (p.X < minX) minX = p.X;
                if (p.X > maxX) maxX = p.X;
                if (pdfY < minY) minY = pdfY;
                if (pdfY > maxY) maxY = pdfY;
            }
        }

        double pad = Math.Max(2.0, stroke.BaseWidth * 1.5);
        double rectX1 = minX - pad;
        double rectY1 = minY - pad;
        double rectX2 = maxX + pad;
        double rectY2 = maxY + pad;

        var annot = new PdfDictionary(doc);
        annot.Elements["/Type"] = new PdfName("/Annot");
        annot.Elements["/Subtype"] = new PdfName("/Ink");
        annot.Elements["/F"] = new PdfInteger(4); // Print flag
        annot.Elements["/NM"] = new PdfString($"scribble-{stroke.Id:N}");
        annot.Elements["/Rect"] = new PdfArray(
            doc,
            new PdfReal(rectX1),
            new PdfReal(rectY1),
            new PdfReal(rectX2),
            new PdfReal(rectY2));

        // Standard /InkList (centerline polyline in PDF bottom-left coordinates)
        var coordArray = new PdfArray(doc);
        foreach (InkPoint p in stroke.Points)
        {
            coordArray.Elements.Add(new PdfReal(Math.Round(p.X, 2)));
            coordArray.Elements.Add(new PdfReal(Math.Round(pageHeightPt - p.Y, 2)));
        }
        // Single-point taps need at least 2 points in /InkList for strict PDF viewers
        if (stroke.Points.Count == 1)
        {
            InkPoint p = stroke.Points[0];
            coordArray.Elements.Add(new PdfReal(Math.Round(p.X + 0.1, 2)));
            coordArray.Elements.Add(new PdfReal(Math.Round(pageHeightPt - p.Y, 2)));
        }
        annot.Elements["/InkList"] = new PdfArray(doc, coordArray);

        // Stroke color (/C)
        annot.Elements["/C"] = new PdfArray(
            doc,
            new PdfReal(Math.Round(stroke.Color.R / 255.0, 4)),
            new PdfReal(Math.Round(stroke.Color.G / 255.0, 4)),
            new PdfReal(Math.Round(stroke.Color.B / 255.0, 4)));

        // Border style (/BS) for viewers that ignore /AP
        var bs = new PdfDictionary(doc);
        bs.Elements["/Type"] = new PdfName("/Border");
        bs.Elements["/W"] = new PdfReal(Math.Round(stroke.BaseWidth, 2));
        bs.Elements["/S"] = new PdfName("/S");
        annot.Elements["/BS"] = bs;

        // Lossless pressure & geometry metadata for Scribble reopening
        annot.Elements[ScribbleWidthKey] = new PdfReal(Math.Round(stroke.BaseWidth, 3));
        annot.Elements[ScribblePointsKey] = new PdfString(EncodeScribblePoints(stroke.Points));

        // Exact variable-width Normal Appearance Stream (/AP /N)
        string apOps = StrokeGeometryBuilder.BuildPdfAppearanceStream(stroke, pageHeightPt);
        if (!string.IsNullOrEmpty(apOps))
        {
            var apForm = new PdfDictionary(doc);
            apForm.Elements["/Type"] = new PdfName("/XObject");
            apForm.Elements["/Subtype"] = new PdfName("/Form");
            apForm.Elements["/FormType"] = new PdfInteger(1);
            apForm.Elements["/BBox"] = new PdfArray(
                doc,
                new PdfReal(rectX1),
                new PdfReal(rectY1),
                new PdfReal(rectX2),
                new PdfReal(rectY2));
            apForm.Elements["/Matrix"] = new PdfArray(
                doc,
                new PdfInteger(1),
                new PdfInteger(0),
                new PdfInteger(0),
                new PdfInteger(1),
                new PdfInteger(0),
                new PdfInteger(0));
            apForm.CreateStream(Encoding.ASCII.GetBytes(apOps));
            doc.Internals.AddObject(apForm);

            var apDict = new PdfDictionary(doc);
            apDict.Elements["/N"] = apForm.Reference!;
            annot.Elements["/AP"] = apDict;
        }

        return annot;
    }

    private static string EncodeScribblePoints(List<InkPoint> points)
    {
        var inv = CultureInfo.InvariantCulture;
        var sb = new StringBuilder(points.Count * 16);
        for (int i = 0; i < points.Count; i++)
        {
            if (i > 0) sb.Append(';');
            var p = points[i];
            sb.AppendFormat(inv, "{0:0.##},{1:0.##},{2:0.###}", p.X, p.Y, p.Pressure);
        }
        return sb.ToString();
    }

    private static List<InkPoint> DecodeScribblePoints(string encoded)
    {
        var inv = CultureInfo.InvariantCulture;
        var result = new List<InkPoint>();
        foreach (string token in encoded.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            string[] parts = token.Split(',');
            if (parts.Length >= 3 &&
                double.TryParse(parts[0], NumberStyles.Float, inv, out double x) &&
                double.TryParse(parts[1], NumberStyles.Float, inv, out double y) &&
                float.TryParse(parts[2], NumberStyles.Float, inv, out float pressure))
            {
                result.Add(new InkPoint(x, y, pressure));
            }
        }
        return result;
    }

    private static Color ReadColor(PdfDictionary annot)
    {
        PdfArray? arr = ResolveArray(annot.Elements["/C"]);
        if (arr != null && arr.Elements.Count >= 3)
        {
            byte r = (byte)Math.Clamp(Math.Round(ReadNumber(arr.Elements[0]) * 255.0), 0, 255);
            byte g = (byte)Math.Clamp(Math.Round(ReadNumber(arr.Elements[1]) * 255.0), 0, 255);
            byte b = (byte)Math.Clamp(Math.Round(ReadNumber(arr.Elements[2]) * 255.0), 0, 255);
            return Color.FromRgb(r, g, b);
        }
        return PenPreset.Defaults[0].Color;
    }

    private static double ReadBorderWidth(PdfDictionary annot)
    {
        PdfDictionary? bs = ResolveDictionary(annot.Elements["/BS"]);
        if (bs != null && bs.Elements.ContainsKey("/W"))
        {
            double w = ReadNumber(bs.Elements["/W"]);
            if (w > 0.2) return w;
        }
        return 2.6;
    }

    private static Guid ReadStrokeId(PdfDictionary annot)
    {
        string? nm = annot.Elements.GetString("/NM");
        if (!string.IsNullOrEmpty(nm) && nm.StartsWith("scribble-", StringComparison.Ordinal))
        {
            if (Guid.TryParse(nm.Substring("scribble-".Length), out Guid parsed))
            {
                return parsed;
            }
        }
        return Guid.NewGuid();
    }

    private static PdfArray? ResolveArray(PdfItem? item)
    {
        if (item is PdfReference r) item = r.Value;
        return item as PdfArray;
    }

    private static PdfDictionary? ResolveDictionary(PdfItem? item)
    {
        if (item is PdfReference r) item = r.Value;
        return item as PdfDictionary;
    }

    private static double ReadNumber(PdfItem? item)
    {
        if (item is PdfReference r) item = r.Value;
        return item switch
        {
            PdfReal real => real.Value,
            PdfInteger integer => integer.Value,
            _ => 0.0
        };
    }
}
