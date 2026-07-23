# Slate PDF

A clean, dark-themed PDF reader and annotator for **Windows 11**, built with
**WinUI 3** (Windows App SDK) and free/open-source PDF libraries only.

![status](https://img.shields.io/badge/version-1.0-blue)

## Feature roadmap

| Phase | Features | Status |
|---|---|---|
| **1 — Viewer** | Open, render, scroll, zoom, fit-width, page navigation, print, drag & drop, `.pdf` file association, dark Mica UI | ✅ Done |
| **2 — Markup** | Freehand pen drawing, text-aware highlighting, eraser, undo, save annotated copy (PDFsharp) | ✅ Done |
| **3 — Objects** | Text selection + copy, text boxes, sticky-note comments, pre-saved signature stamp from a scanned image | ✅ Done |
| **4 — Ship** | Full icon asset set, v1.0, self-contained Release packages, one-command signed installer build | ✅ Done |

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

The easy way — from a PowerShell prompt at the repo root:

```powershell
.\build-installer.ps1            # x64; add -Platform ARM64 for ARM machines
```

The script finds MSBuild, creates (or reuses) a self-signed signing
certificate matching the manifest publisher, and builds a **signed,
self-contained** MSIX into `.\dist` — the .NET and Windows App SDK runtimes
ship inside the package, so recipients install nothing else.

Give recipients the `dist` folder contents:

1. **Once per machine:** install `SlatePdf.cer` — right-click → *Install
   Certificate* → *Local Machine* → store: **Trusted People**. (Or from an
   admin PowerShell:
   `Import-Certificate -FilePath SlatePdf.cer -CertStoreLocation Cert:\LocalMachine\TrustedPeople`)
2. Double-click the `.msix` → **Install**. Updates install over the top as
   long as they're signed with the same certificate.

The signing key lives in your user certificate store (`certmgr.msc` →
Personal → Certificates → `CN=SlatePdf`) — export it from there if you ever
need to build with the same identity on another machine. If you later buy a
real code-signing certificate or publish through the Microsoft Store, the
certificate-trust step disappears; the Store path additionally requires
reserving the app name and letting the Store re-sign the package.

The Visual Studio wizard (right-click project → **Package and Publish** →
**Create App Packages…**) remains available if you prefer a UI.

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
| Select & copy text | Text-select tool (I-beam icon), drag across text, then `Ctrl+C` — or switch straight to the highlighter to highlight the selection. Selection follows columns in multi-column layouts |
| Add text | Text tool (font icon), click a spot, type; click away to commit; click existing text to edit, drag to move; sizes under the palette icon |
| Comment | Comment tool, click a spot, write the note; click an icon to reopen, drag to move. Saved as a real PDF sticky note |
| Signature | Signature tool — first use asks for a scanned image (white background is made transparent automatically); then click to place, drag to move, drag the corner handle to resize. Manage the saved image under the palette icon |
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
- On save, ink, highlights, text boxes, and signatures are drawn into the page
  content with PDFsharp in append mode ("flattened"), which renders
  identically in every PDF viewer. Comments are written as real PDF text
  annotations, so they open as sticky notes in other viewers.
- The signature image lives in `%LocalAppData%\SlatePdf\signature.png`,
  imported once and reused across documents and sessions.

## Known limitations (as of Phase 3)

- Password-protected PDFs show an error instead of a password prompt.
- Saved marks (except comments) are flattened into the page, so they can't be
  selected or deleted afterwards in other PDF editors (undo works while the
  app is open).
- Comment sticky notes may not be visible when the saved file is reopened in
  Slate PDF itself (the Windows renderer skips annotations without embedded
  appearances); Acrobat and Edge show them.
- Pages with a `/Rotate` entry may place marks slightly off; standard
  documents are unaffected.
- On touch screens, one-finger drag pans the document rather than drawing —
  use a mouse or pen stylus for annotation.
- `Ctrl` + mouse wheel zoom may also scroll slightly (ScrollViewer quirk);
  keyboard zoom is always clean.
- Column detection for text selection is heuristic (recursive XY-cut); most
  column layouts work, but unusual table-heavy pages may still group oddly —
  the freeform highlight fallback always works.
