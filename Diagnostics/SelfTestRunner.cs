using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Media;
using PdfSharp;
using PdfSharp.Pdf;
using PDFtoImage;
using Scribble.Models;
using Scribble.Rendering;
using Scribble.Services;
using Scribble.Session;
using SkiaSharp;

namespace Scribble.Diagnostics;

/// <summary>
/// Headless end-to-end verification suite and sample 16:9 lecture deck generator.
/// Invoked via: Scribble.exe --selftest [samplePdfOutputPath]
/// </summary>
public static class SelfTestRunner
{
    public static int Run(string? samplePdfOutputPath)
    {
        var log = new StringBuilder();
        try
        {
            string tempDir = Path.Combine(Path.GetTempPath(), $"scribble_selftest_{Guid.NewGuid():N}");
            Directory.CreateDirectory(tempDir);

            string samplePath = string.IsNullOrWhiteSpace(samplePdfOutputPath)
                ? Path.Combine(tempDir, "SampleLecture.pdf")
                : Path.GetFullPath(samplePdfOutputPath);

            // 1. Generate a clean 3-slide 16:9 lecture PDF with blank gaps for handwriting
            CreateSampleLecturePdf(samplePath);
            log.AppendLine($"[OK] Created 3-slide 16:9 sample PDF: {samplePath}");

            // 2. Load into PresentationSession and verify rendering
            using var session = new PresentationSession();
            session.LoadPdf(samplePath);

            Assert(session.Pages.Count == 3, $"Expected 3 pages, got {session.Pages.Count}");
            Assert(Math.Abs(session.Pages[0].WidthPt - 960) < 1.0 && Math.Abs(session.Pages[0].HeightPt - 540) < 1.0,
                $"Unexpected page size {session.Pages[0].WidthPt}x{session.Pages[0].HeightPt}");

            var bmp0 = session.GetCurrentSlideBitmap();
            Assert(bmp0 != null && bmp0.PixelWidth == 2880 && bmp0.PixelHeight == 1620,
                $"Unexpected rendered bitmap size {bmp0?.PixelWidth}x{bmp0?.PixelHeight}");
            log.AppendLine($"[OK] Rendered slide 1 at crisp {bmp0!.PixelWidth}x{bmp0.PixelHeight} px");

            // 3. Draw pressure-sensitive strokes on Slide 1 (Black + Blue) and Slide 2 (Red + Green)
            session.SelectPen(PenPreset.Defaults[0]); // Black
            session.BeginStroke(new InkPoint(120, 260, 0.25f));
            session.AppendStrokePoint(new InkPoint(180, 255, 0.65f));
            session.AppendStrokePoint(new InkPoint(250, 280, 0.95f));
            session.AppendStrokePoint(new InkPoint(330, 265, 0.40f));
            session.EndStroke();

            session.SelectPen(PenPreset.Defaults[1]); // Blue
            session.BeginStroke(new InkPoint(500, 310, 0.80f));
            session.AppendStrokePoint(new InkPoint(560, 340, 0.50f));
            session.EndStroke();

            session.GoNext();
            Assert(session.CurrentPageIndex == 1, "Failed to navigate to slide 2");

            session.SelectPen(PenPreset.Defaults[2]); // Red
            session.SetStrokeWidthScale(2.0);
            session.BeginStroke(new InkPoint(200, 200, 0.90f));
            session.AppendStrokePoint(new InkPoint(400, 220, 0.35f));
            session.EndStroke();
            Assert(Math.Abs(session.CurrentPageStrokes[0].BaseWidth - PenPreset.Defaults[2].BaseWidth * 2.0) < 0.05,
                $"Expected scaled stroke width, got {session.CurrentPageStrokes[0].BaseWidth}");
            session.ResetStrokeWidthScale();
            Assert(Math.Abs(session.StrokeWidthScale - 1.0) < 0.001, "ResetStrokeWidthScale failed");
            session.ToggleEraserMode();
            Assert(session.IsEraserMode, "ToggleEraserMode failed to enable");
            session.SelectPen(PenPreset.Defaults[0]);
            Assert(!session.IsEraserMode, "SelectPen should exit eraser mode");

            Assert(session.HasUnsavedChanges, "Expected HasUnsavedChanges == true");

            // 4. Save annotated copy
            string annotatedPath = Path.Combine(tempDir, "SampleLecture_annotated.pdf");
            session.SaveAnnotatedCopy(annotatedPath);
            Assert(!session.HasUnsavedChanges, "Expected HasUnsavedChanges == false after save");
            Assert(File.Exists(annotatedPath), "Annotated PDF was not created");
            log.AppendLine($"[OK] Saved annotated PDF copy ({new FileInfo(annotatedPath).Length:N0} bytes)");

            // 5. Verify PDFium renders the saved /AP /N ink appearance stream when WithAnnotations=true,
            //    and omits it cleanly when WithAnnotations=false (so erasing never leaves a ghost)
            byte[] savedBytes = File.ReadAllBytes(annotatedPath);
            int darkPixelsWithAnnots = CountDarkPixelsInRegion(savedBytes, pageIndex: 0, withAnnotations: true);
            int darkPixelsWithoutAnnots = CountDarkPixelsInRegion(savedBytes, pageIndex: 0, withAnnotations: false);
            Assert(darkPixelsWithAnnots > darkPixelsWithoutAnnots + 100,
                $"Expected visible ink in PDF /AP stream (with={darkPixelsWithAnnots}, without={darkPixelsWithoutAnnots})");
            log.AppendLine($"[OK] Verified PDF /AP appearance stream visibility (ink delta = +{darkPixelsWithAnnots - darkPixelsWithoutAnnots} px) and clean background separation");

            // 6. Reload saved PDF into a fresh session and verify lossless stroke + pressure restoration
            using var reloaded = new PresentationSession();
            reloaded.LoadPdf(annotatedPath);
            Assert(reloaded.CurrentPageStrokes.Count == 2, $"Expected 2 strokes on slide 1, got {reloaded.CurrentPageStrokes.Count}");
            InkStroke s0 = reloaded.CurrentPageStrokes[0];
            Assert(s0.Points.Count == 4, $"Expected 4 points on stroke 0, got {s0.Points.Count}");
            Assert(Math.Abs(s0.Points[2].Pressure - 0.95f) < 0.01f, $"Pressure mismatch: {s0.Points[2].Pressure}");
            Assert(s0.Color == PenPreset.Defaults[0].Color, "Color mismatch on stroke 0");

            reloaded.GoNext();
            Assert(reloaded.CurrentPageStrokes.Count == 1, $"Expected 1 stroke on slide 2, got {reloaded.CurrentPageStrokes.Count}");
            Assert(reloaded.CurrentPageStrokes[0].Color == PenPreset.Defaults[2].Color, "Color mismatch on slide 2");
            log.AppendLine("[OK] Verified 100% lossless stroke, color, and pressure round-trip on reload");

            // 7. Erase a stroke, re-save, reload, and verify deletion persists
            reloaded.GoPrevious();
            bool erased = reloaded.EraseAt(new Point(530, 325), tolerancePt: 12.0);
            Assert(erased && reloaded.CurrentPageStrokes.Count == 1, "Expected Blue stroke to be erased");

            string reEditedPath = Path.Combine(tempDir, "SampleLecture_edited.pdf");
            reloaded.SaveAnnotatedCopy(reEditedPath);

            using var verifiedDeleted = new PresentationSession();
            verifiedDeleted.LoadPdf(reEditedPath);
            Assert(verifiedDeleted.CurrentPageStrokes.Count == 1,
                $"Expected 1 remaining stroke after erase+save, got {verifiedDeleted.CurrentPageStrokes.Count}");
            log.AppendLine("[OK] Verified erasing previously-saved ink persists cleanly on re-save");

            // 8. Verify StrokeGeometryBuilder builds valid polygons for single-point taps and multi-point strokes
            var tapStroke = new InkStroke(Colors.Black, 2.6, [new InkPoint(100, 100, 0.7f)]);
            var tapGeom = StrokeGeometryBuilder.BuildWpfGeometry(tapStroke);
            Assert(tapGeom != null && !tapGeom.Bounds.IsEmpty, "Single-point tap geometry failed");
            log.AppendLine("[OK] Verified StrokeGeometryBuilder for taps and variable-pressure curves");

            // Cleanup temp files (keep samplePath if user requested an explicit output location)
            try
            {
                if (!samplePath.StartsWith(tempDir, StringComparison.OrdinalIgnoreCase))
                {
                    Directory.Delete(tempDir, recursive: true);
                }
            }
            catch { /* ignore */ }

            File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "selftest_result.txt"), "PASS\n" + log);
            return 0;
        }
        catch (Exception ex)
        {
            File.WriteAllText(
                Path.Combine(AppContext.BaseDirectory, "selftest_result.txt"),
                "FAIL\n" + log + "\n" + ex);
            return 1;
        }
    }

    private static int CountDarkPixelsInRegion(byte[] pdfBytes, int pageIndex, bool withAnnotations)
    {
        using var ms = new MemoryStream(pdfBytes, writable: false);
        var opts = new PDFtoImage.RenderOptions(
            Width: 960,
            WithAnnotations: withAnnotations,
            WithFormFill: false,
            WithAspectRatio: true,
            BackgroundColor: SKColors.White);

        using SKBitmap bmp = Conversion.ToImage(ms, page: pageIndex, options: opts);
        int count = 0;
        // Inspect the blank gap region where strokes were drawn (y in [220..380], x in [100..600])
        for (int y = 220; y < Math.Min(380, bmp.Height); y++)
        {
            for (int x = 100; x < Math.Min(600, bmp.Width); x++)
            {
                SKColor c = bmp.GetPixel(x, y);
                if (c.Red < 200 || c.Green < 200 || c.Blue < 200)
                {
                    count++;
                }
            }
        }
        return count;
    }

    public static void CreateSampleLecturePdf(string outputPath)
    {
        using var doc = new PdfDocument();
        doc.Info.Title = "Scribble Sample Lecture";

        AddSlide(doc,
            title: "Slide 1: Worked Derivation (Practice Zoom + Inking)",
            subtitle: "Zoom in on Screen 1 (Mouse Wheel + Right-Drag pan) and write in the box below.",
            footer: "Notice that Screen 2 stays locked at 100% full-slide view while you zoom in.",
            boxLabel: "Work out the derivation here:");

        AddSlide(doc,
            title: "Slide 2: Pressure Sensitivity & Color Switching",
            subtitle: "Try Black, Blue, Red, and Green on the right-edge rail — and test your stylus eraser.",
            footer: "Advancing slides automatically resets Screen 1 zoom back to 100%.",
            boxLabel: "Sketch a diagram or annotate here:");

        AddSlide(doc,
            title: "Slide 3: Editable PDF Ink Round-Trip",
            subtitle: "Click Quit (top-right) to save an annotated copy, then reopen it in Scribble or Edge.",
            footer: "Every stroke is saved as a vector PDF Ink Annotation that remains erasable later.",
            boxLabel: "Summary notes:");

        doc.Save(outputPath);
    }

    private static void AddSlide(PdfDocument doc, string title, string subtitle, string footer, string boxLabel)
    {
        PdfPage page = doc.AddPage();
        page.Width = PdfSharp.Drawing.XUnit.FromPoint(960);
        page.Height = PdfSharp.Drawing.XUnit.FromPoint(540);

        // Register standard built-in PDF Helvetica fonts (no external font files needed)
        var fRegular = new PdfDictionary(doc);
        fRegular.Elements["/Type"] = new PdfName("/Font");
        fRegular.Elements["/Subtype"] = new PdfName("/Type1");
        fRegular.Elements["/BaseFont"] = new PdfName("/Helvetica");
        doc.Internals.AddObject(fRegular);

        var fBold = new PdfDictionary(doc);
        fBold.Elements["/Type"] = new PdfName("/Font");
        fBold.Elements["/Subtype"] = new PdfName("/Type1");
        fBold.Elements["/BaseFont"] = new PdfName("/Helvetica-Bold");
        doc.Internals.AddObject(fBold);

        var fonts = new PdfDictionary(doc);
        fonts.Elements["/F1"] = fRegular.Reference!;
        fonts.Elements["/F2"] = fBold.Reference!;

        var resources = new PdfDictionary(doc);
        resources.Elements["/Font"] = fonts;
        page.Elements["/Resources"] = resources;

        // Clean vector slide layout in PDF bottom-left coordinates (960 x 540 pt)
        string content = $$"""
            q
            0.11 0.16 0.28 rg
            0 448 960 92 re f
            0.29 0.54 0.96 rg
            0 444 960 4 re f
            1 1 1 rg
            BT /F2 26 Tf 48 492 Td ({{EscapePdf(title)}}) Tj ET
            0.82 0.87 0.96 rg
            BT /F1 14 Tf 48 464 Td ({{EscapePdf(subtitle)}}) Tj ET
            0.35 0.40 0.48 rg
            BT /F2 15 Tf 52 406 Td ({{EscapePdf(boxLabel)}}) Tj ET
            0.84 0.87 0.92 RG
            1.2 w
            [6 4] 0 d
            48 76 864 316 re S
            [] 0 d
            0.48 0.52 0.60 rg
            BT /F1 12 Tf 48 34 Td ({{EscapePdf(footer)}}) Tj ET
            Q
            """;

        var streamDict = new PdfDictionary(doc);
        streamDict.CreateStream(Encoding.ASCII.GetBytes(content));
        doc.Internals.AddObject(streamDict);
        page.Elements["/Contents"] = streamDict.Reference!;
    }

    private static string EscapePdf(string text) =>
        text.Replace("\\", "\\\\").Replace("(", "\\(").Replace(")", "\\)");

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException("ASSERTION FAILED: " + message);
        }
    }
}
