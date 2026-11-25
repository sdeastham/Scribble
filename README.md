# Scribble

A lightweight Windows presentation and live-inking tool built for lecturers using a tablet (such as a Microsoft Surface) connected to a projector in **Extend** display mode.

## Why Scribble?

- **Decoupled Dual-Screen Zoom:** Pinch-zoom and pan freely on your tablet (Screen 1) to write neatly in small gaps, while the projector (Screen 2) stays locked to a crisp 100% full-slide view showing your strokes appear live as you write.
- **Left-Handed Ergonomics:** All controls live on a narrow right-edge rail so a left hand resting on the screen never accidentally triggers slide navigation or quit.
- **Strict Palm Protection:** Only a Windows Ink stylus draws on the canvas. Single-finger/palm contacts are ignored, and touch zoom/pan requires two fingers and locks out whenever the stylus is in proximity.
- **Pressure-Sensitive Vector Ink & Editable PDF Storage:** Ink is saved into a new PDF copy as standard PDF **Ink Annotations** (`/Subtype /Ink`) with exact variable-width vector appearance streams (`/AP /N`). Students can open the PDF in any standard viewer, and reopening it in Scribble restores every stroke losslessly so you can erase or add to it later.

---

## Using Scribble in a Lecture

1. Connect your Surface to the projector and set Windows display mode to **Extend** (`Win + P` → **Extend**).
2. Export your slides from PowerPoint as a `.pdf` (or use any existing PDF slide deck).
3. Open your PDF in Scribble in whichever way is fastest:
   - Launch `Scribble.exe` and click **Open Presentation PDF...**
   - **Drag and drop** a `.pdf` file onto the Scribble window
   - Pass a file path on launch (`Scribble.exe path\to\Lecture.pdf`) — or use Windows **Open with → Scribble.exe**
4. If Windows places the presenter window on the projector instead of your tablet, click the **Swap Screens** button (`⇄`, top-right below Quit) or press `Ctrl + Shift + S`.
5. When finished, click **Quit** (`✕`) or press `Esc` to save an annotated copy (`<DeckName>_annotated.pdf`), leaving your clean lecture slides untouched.

---

## Controls & Shortcuts

### Right-Edge Control Rail
- **Quit (`✕`):** Prompts to save an annotated PDF copy if you have unsaved ink.
- **Swap Screens (`⇄`):** Swaps Presenter and Audience displays (appears automatically when 2+ screens are connected).
- **Pen Colors:** Black, Blue, Red, and Green (pressure-sensitive thickness).
- **Stroke Eraser:** Toggle stroke-erasing mode for styluses without a built-in eraser (clicking any pen color returns to drawing).
- **Stroke Size Slider & `1.0×` Reset:** Scales stroke thickness from `0.4×` to `3.0×` with a live preview dot; click the multiplier pill underneath to snap back to `1.0×`.
- **Previous / Next Slide:** Advances slides and automatically resets Screen 1 zoom to 100%.
- **Reset Zoom Pill (top-right of canvas):** Appears only when zoomed in; one tap returns Screen 1 to 100%.

### Stylus, Touch & Desktop Controls
| Input | Action |
|---|---|
| **Stylus tip** | Draw pressure-sensitive ink (or erase strokes if Eraser mode is active) |
| **Stylus tail / barrel button** | Erase whole strokes on touch |
| **Two-finger pinch / drag** | Zoom and pan Screen 1 (locked while stylus is near screen) |
| **Mouse wheel** | Zoom Screen 1 around cursor (for desktop testing) |
| **Right-click drag** (or middle-drag) | Pan zoomed Screen 1 (for desktop testing) |

### Keyboard & Clicker Shortcuts
| Shortcut | Action |
|---|---|
| `Right` / `Down` / `PageDown` / `Space` | Next slide (resets Screen 1 zoom to 100%) |
| `Left` / `Up` / `PageUp` | Previous slide (resets Screen 1 zoom to 100%) |
| `E` | Toggle stroke eraser mode |
| `0` | Reset Screen 1 zoom to 100% |
| `Ctrl + Z` / `Ctrl + Y` | Undo / Redo |
| `Ctrl + S` | Save annotated PDF copy |
| `Ctrl + O` | Open another PDF |
| `Ctrl + Shift + S` | Swap Presenter & Audience displays |
| `F11` | Toggle Presenter borderless fullscreen |
| `Esc` | Quit (prompts to save annotated copy) |

---

## Building & Publishing a Standalone Executable

### Requirements
- Windows 10/11
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) (for building only — not required on the Surface when published self-contained)

### Run from Source
```powershell
dotnet build
.\bin\Debug\net8.0-windows\Scribble.exe .\SampleLecture.pdf
```

### Build a Single Standalone `Scribble.exe` for Your Surface
```powershell
dotnet publish Scribble.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o .\publish\win-x64
```

This produces a single self-contained `publish\win-x64\Scribble.exe` (bundles .NET 8, PDFium, SkiaSharp, and PDFsharp) that you can copy straight onto your Surface via OneDrive or USB and run with no installation required.

> **Tip:** To open any PDF with a right-click on your Surface, right-click a `.pdf` → **Open with** → **Choose another app** → browse to `Scribble.exe` (leave "Always" unchecked so your regular PDF viewer stays default).

## License

MIT License — see [LICENSE](LICENSE).
