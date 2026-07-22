# Slate PDF

A clean, dark-themed PDF reader and annotator for **Windows 11**, built with
**WinUI 3** (Windows App SDK) and free/open-source PDF libraries only.

![status](https://img.shields.io/badge/phase-1%20viewer-blue)

## Feature roadmap

| Phase | Features | Status |
|---|---|---|
| **1 — Viewer** | Open, render, scroll, zoom, fit-width, page navigation, print, drag & drop, `.pdf` file association, dark Mica UI | ✅ In this branch |
| **2 — Markup** | Freehand drawing (InkCanvas), text highlighting, save annotations back into the PDF (PDFsharp) | Planned |
| **3 — Objects** | Text boxes, sticky-note comments, pre-saved signature stamp from a scanned image | Planned |
| **4 — Ship** | MSIX installer polish, signing, distribution | Planned |

## Tech stack

- **UI:** WinUI 3 / Windows App SDK 1.6, dark theme with Mica backdrop
- **Rendering:** `Windows.Data.Pdf` (built into Windows — no external native binaries)
- **Coming in later phases:** PdfPig (text geometry for highlights, Apache 2.0),
  PDFsharp 6 (writing annotations into the PDF, MIT)
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
- Annotation phases will draw on a transparent overlay above each page bitmap,
  then write real PDF annotation objects into the file on save.

## Known limitations (Phase 1)

- Password-protected PDFs show an error instead of a password prompt.
- `Ctrl` + mouse wheel zoom may also scroll slightly (ScrollViewer quirk);
  keyboard zoom is always clean.
- The app logo assets are programmer-art placeholders.
