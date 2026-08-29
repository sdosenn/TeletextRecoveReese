# TeletextRecoveReese
Teletext recovery editor (AI built, with a name to pun Kyle Reese (much to his horror) and recoveries)

<img width="780" height="488" alt="TRR preview" src="https://github.com/user-attachments/assets/ebddef02-19d4-4bae-8d66-f8500a5df900" />
<br><br>
TeletextRecoveReese is a tool for inspecting, editing, and reconstructing
teletext pages from raw broadcast captures.<br>
Its primary purpose is damaged-page
recovery: every occurrence of a page is preserved as a separate version,<br>allowing
the best rows from different broadcasts to be combined into a new restored capture.

# Cross-platform
The project is written in C#, targets .NET 10, uses Avalonia UI,<br>
and is designed as a cross-platform application for Linux, macOS, and Windows.

## How the application works

The workspace contains two views:

- **Full broadcast** displays the complete input `.t42` capture. Pages can be
  browsed by magazine, page number, subpage, and captured version.
- **Squashed page** contains the restored result. Individual rows from any version
  shown on the right can be transferred into it, edited further, and saved as a new
  `.t42` capture.

A typical workflow is:

1. Open a complete capture through **File > Open broadcast stream**.
2. Find the required page, subpage, and best captured version.
3. Transfer good rows into the restored view on the left.
4. It is recommended to also load a squashed version from vhs-teletext
5. Edit characters, control codes, mosaics, or X/26 diacritics as needed.
6. Save the result as `.t42` or export pages as PNG images.

## Current features

- decoding raw 42-byte teletext packets
- organizing captures by magazine, page, subpage, and version
- preserving every repeated page instance from a broadcast capture
- allows editing text, mosaics and supports adding diacritics (tested croatian for now)
- supports moving X/26 diacritics within a page via drag and drop
- character and teletext control-code editing
- cell and block selection, copy/paste, and per page undo/redo history
- transferring individual rows from the broadcast view to the restored page
- adding and deleting pages
- selecting the font used to render the teletext grid (TIFAX font recommended)
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

Project target `net10.0`.

## Inspiration
I copied concepts from two Teletext editors I used for my recoveries,<br>
Teletext Meddler and QTeletextMaker but I also wanted some extra features,<br>
so in this day and age I used AI to build me a tool that I need.<br>
Even though I could have used any other language I used C# which I like<br>
so I can check the code myself if need be. Surprisingly for now I didn't need to.

## Formats

### T42

The primary and fully supported format is raw `.t42`: a sequence of consecutive
packets, each exactly 42 bytes long (2 address bytes and 40 payload bytes). Loading
and saving operate directly on these packets.

## Status

This is an actively developed early-stage project, currently at version **0.1 beta**.<br>
The core workflow for loading a broadcast capture, combining rows, editing content,<br>
preserving source packets, and saving a restored `.t42` file is available.<br>
Complete EN 300 706 coverage and broader interoperability testing are still in progress.

## Message from Reese
<img width="584" height="500" alt="Reese" src="https://github.com/user-attachments/assets/81d6b8dd-63d3-4289-8fb7-4ed07bf13981" />
