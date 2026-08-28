# TeletextRecoveReese

TeletextRecoveReese is a desktop tool for inspecting, editing, and reconstructing
teletext pages from raw broadcast captures. Its primary purpose is damaged-page
recovery: every occurrence of a page is preserved as a separate version, allowing
the best rows from different broadcasts to be combined into a new restored capture.

The project is written in C#, targets .NET 10, uses Avalonia UI, and is designed as
a cross-platform application for Linux, macOS, and Windows.

## How the application works

The workspace contains two views:

- **Full broadcast** displays the complete input `.t42` capture. Pages can be
  browsed by magazine, page number, subpage, and captured version.
- **Squashed page** contains the restored result. Individual rows from any version
  shown on the right can be transferred into it, edited further, and saved as a new
  `.t42` capture.

The application does not preserve only decoded text. It retains the original
42-byte teletext packet for every row as the source of truth, allowing unchanged
data to be written back without unnecessary re-encoding.

A typical workflow is:

1. Open a complete capture through **File > Open broadcast stream**.
2. Find the required page, subpage, and best captured version.
3. Transfer good rows into the restored view on the left.
4. Edit characters, control codes, mosaics, or X/26 diacritics as needed.
5. Save the result as `.t42` or export pages as PNG images.

## Current features

- decoding raw 42-byte teletext packets
- rendering a 40-column by 25-row grid, including the header
- organizing captures by magazine, page, subpage, and version
- preserving every repeated page instance from a broadcast capture
- Hamming 8/4 and Hamming 24/18 decoding, encoding, and error correction
- teletext foreground and background colors
- contiguous and separated mosaic/sixel graphics
- hold and release mosaics
- normal, double-height, double-width, and double-size characters
- flash, conceal, and boxed attributes
- Level 1.5 X/26 enhancement packets and diacritics
- X/26 triplet inspection and uncorrectable Hamming error indication
- moving X/26 diacritics within a page
- character and teletext control-code editing
- cell and block selection, copy/paste, and undo/redo history
- transferring individual rows from the broadcast view to the restored page
- adding and deleting pages
- selecting the font used to render the teletext grid
- exporting the current page or all restored pages as PNG images or video (ffmpeg required)
- preserving paths, selected pages, the X/26 sidebar preference, and font settings
  between sessions

## Running

The .NET 10 SDK is required.

```bash
dotnet restore
dotnet run --project TeletextRecoveReese
```

To reopen files and selections remembered from the previous session:

```bash
dotnet run --project TeletextRecoveReese -- -loadlast
```

Build the complete solution with:

```bash
dotnet build TeletextRecoveReese.sln
```

Both projects currently target `net10.0`. Using an older SDK requires changing
`TargetFramework` in both `.csproj` files and installing the corresponding runtime.

## Formats

### T42

The primary and fully supported format is raw `.t42`: a sequence of consecutive
packets, each exactly 42 bytes long (2 address bytes and 40 payload bytes). Loading
and saving operate directly on these packets.

## Status

This is an actively developed early-stage project, currently at version **0.1 beta**.
The core workflow for loading a broadcast capture, combining rows, editing content,
preserving source packets, and saving a restored `.t42` file is available. Complete
EN 300 706 coverage and broader interoperability testing are still in progress.
