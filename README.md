# Slate PDF

A clean, dark-themed PDF reader and annotator for **Windows 11**, built with
**WinUI 3** (Windows App SDK) and free/open-source PDF libraries only.

![status](https://img.shields.io/badge/phase-2%20markup-blue)

## Feature roadmap

| Phase | Features | Status |
|---|---|---|
| **1 — Viewer** | Open, render, scroll, zoom, fit-width, page navigation, print, drag & drop, `.pdf` file association, dark Mica UI | ✅ Done |
| **2 — Markup** | Freehand pen drawing, text-aware highlighting, eraser, undo, save annotated copy (PDFsharp) | ✅ In this branch |
| **3 — Objects** | Text boxes, sticky-note comments, pre-saved signature stamp from a scanned image | Planned |
| **4 — Ship** | MSIX installer polish, signing, distribution | Planned |

## Tech stack

- **UI:** WinUI 3 / Windows App SDK 1.6, dark theme with Mica backdrop
- **Rendering:** `Windows.Data.Pdf` (built into Windows — no external native binaries)
- **Text geometry:** [PdfPig](https://github.com/UglyToad/PdfPig) (Apache 2.0) — word
  bounding boxes so highlights snap to real text lines
- **Saving:** [PDFsharp 6](https://github.com/empira/PDFsharp) (MIT) — draws the
  annotations into the saved PDF
- **Packaging:** single-project MSIX

## Building

### Prerequisites

- Windows 11
- **Visual Studio 2022** (17.8 or later) with these workloads:
  - *.NET desktop development*
  - *Windows application development* (includes the Windows App SDK / WinUI templates)
- Windows 11 SDK (installed with the workload above)

### Run it

1. Open `PdfReader.sln` in Visual Studio.
2. Set the platform to **x64** (toolbar dropdown).
3. Select the **PdfReader (Package)** launch profile and press **F5**.

On first F5, Visual Studio installs the MSIX package locally (it may prompt to
enable Developer Mode — accept). After that, "Slate PDF" also appears in the
Start menu and registers as a `.pdf` handler you can choose in *Open with*.

### Create a shareable installer

1. Right-click the project → **Package and Publish** → **Create App Packages…**
2. Choose **Sideloading**, create/select a signing certificate, pick your
   architectures (x64 at minimum), and build.
3. Ship the generated folder — recipients install the `.cer` certificate once
   (or you use a proper code-signing cert), then double-click the `.msix`.

## Using the app

| Action | How |
|---|---|
| Open a PDF | `Ctrl+O`, the **Open** button, or drag & drop a file onto the window |
| Navigate | Scroll, `Page Up`/`Page Down`, the arrows / page box in the toolbar |
| Zoom | `Ctrl` + `+`/`-`, `Ctrl` + mouse wheel, or the toolbar buttons |
| Fit width | `Ctrl+0` or the fit-width button |
| Print | `Ctrl+P` or the **Print** button |
| Draw | Pen tool (pencil icon), then drag on a page; pick color/size under the palette icon |
| Highlight | Highlighter tool (I-beam cursor), then drag across text like selecting in any reader — words are selected in reading order with a live preview; on scanned pages it keeps your rectangle |
| Erase | Eraser tool, then click (or drag over) a mark |
| Undo | `Ctrl+Z` or the undo button |
| Back to scrolling | `Esc` or the select tool |
| Save | `Ctrl+S` saves an annotated **copy** — the original file is never touched |

## Architecture notes

- Pages are rasterized **lazily**: each page renders only when it nears the
  viewport (via `EffectiveViewportChanged`) and is released again when it
  scrolls far away, so large documents stay light on memory.
- Page sizes are read up front (no rasterization needed), so the scrollbar
  extent is correct immediately after opening.
- Bitmaps are rendered at `zoom × monitor scale`, so text is crisp on
  high-DPI displays and after zooming.
- Printing pre-renders pages at up to 150 DPI within a global pixel budget,
  then feeds them through the standard `PrintDocument` pipeline.
- The document is read fully into memory: the same bytes feed the renderer
  (`Windows.Data.Pdf`), text extraction (PdfPig), and saving (PDFsharp), and
  the original file is never locked.
- Annotations live on a transparent `AnnotationCanvas` overlay above each page
  bitmap. Geometry is stored in zoom-independent page coordinates (DIPs at
  100%), so one dataset drives display at any zoom *and* the PDF output.
  (WinUI 3 has no `InkCanvas`, so the ink layer is custom pointer handling.)
- On save, marks are drawn into the page content with PDFsharp in append mode
  ("flattened"), which renders identically in every PDF viewer.

## Known limitations (as of Phase 2)

- Password-protected PDFs show an error instead of a password prompt.
- Saved marks are flattened into the page, so they can't be selected or
  deleted afterwards in other PDF editors (undo works while the app is open).
- Pages with a `/Rotate` entry may place marks slightly off; standard
  documents are unaffected.
- On touch screens, one-finger drag pans the document rather than drawing —
  use a mouse or pen stylus for annotation.
- `Ctrl` + mouse wheel zoom may also scroll slightly (ScrollViewer quirk);
  keyboard zoom is always clean.
- The app logo assets are programmer-art placeholders.
