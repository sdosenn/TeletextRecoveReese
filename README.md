# TeletextRecoveReese

Cross-platform tool for VHS teletext recovery, developed with OpenAI
Codex.<br>
Capture VBI, deconvolve and restore teletext from VHS tapes all in one cross-platform GUI application.

<p align="left">
  <a href="https://github.com/sdosenn/TeletextRecoveReese/releases/tag/0.9.3-beta">
    <img src="https://img.shields.io/badge/Download_latest_release-0.9.3_beta-2ea44f?style=for-the-badge&logo=github" alt="Download TeletextRecoveReese 0.9.3 beta">
  </a>
</p>

<img width="850" height="608" alt="TeletextRecoveReese preview" src="https://github.com/user-attachments/assets/44b0fcb3-55d9-4319-93ae-7e0ebbebd968" />


## Realtime VBI capture and deconvolving chain
Deconvolve data in real time and see teletext in the exact moment on the tape that's playing.<br>
No need to store data to disk and separately deconvolve it later. Do it all in one pass.<br>
Of course raw VBI can be stored for preservation if needed.<br>
You can also hot swap tapes to quickly date taped broadcasts.<br>

<img width="780" height="452" alt="TeletextRecoveReese preview" src="https://github.com/user-attachments/assets/4263db68-66a8-4cfc-b447-ec28574045d8" />

## Supported capture cards

| Capture card | Windows  | Linux | macOS |
| ------------- |:-------------:|:-------------:|:-------------:|
| August VGB100 |<span style="color: green">Supported</span> * |<span style="color: green">Supported</span> * |<span style="color: green">Supported</span> *
| SAA7131 PCI/USB | <span style="color: green">Supported</span> |<span style="color: green">Supported</span>| <span style="color: red">Not supported</span>
| BT8x8   |<span style="color: orange">Supported</span>|<span style="color: green">Supported</span>|<span style="color: red">Not supported</span>
| CX88   |<span style="color: orange">Supported</span>|<span style="color: green">Supported</span>|<span style="color: red">Not supported</span>

## Capture card notes

### August VGB
Officially this card is only supported up to Windows 10.<br>
Linux drivers have issues with freezing while reading VBI data.<br>
Use a provided built-in libusb usermode driver as of version 0.9.3-beta.<br>
This card lacks line TBC features so the signal from VHS tapes can be jittery.

For Windows 10/11 use [Zadig](https://zadig.akeo.ie/) to install signed libusb driver.<br>
For Linux make sure udev rules allow access to the USB device.<br>
For macOS install libusb via homebrew.

### SAA7131 based capture cards
Well supported on modern x64 Windows and a recommended route with desktop PCs.<br>
Supported on Linux but will experience frame drops if video and VBI is streamed at the same time.

### BT8x8 based capture cards
Supported on Linux but will experience frame drops if video and VBI is streamed at the same time.<br>
Lackluster support on modern x64 Windows, there are almost no modern drivers for these cards.<br>
They lack line TBC features so the signal from VHS tapes can be jittery.

### CX88 based capture cards
Well supported on Linux and can stream both video and VBI without frame dropps.<br>
May not have modern Windows drivers as those are Windows XP era cards.<br>
Use a sampling offset of 2 for these cards for better capture results.

## ☕ Support the recovery effort

Teletext recovery is a niche kind of digital archaeology. If TeletextRecoveReese
helps you rescue a few more pages from VHS noise, corruption, or an imperfect
broadcast capture, consider supporting its continued development.

Your support helps me spend more time on better restoration tools, faster and
simpler recovery workflows, broader compatibility, and reliable cross-platform
releases.

<p align="center">
  <a href="https://ko-fi.com/sinisinavideoteka">
    <img src="https://ko-fi.com/img/githubbutton_sm.svg" alt="Support TeletextRecoveReese on Ko-fi" height="36">
  </a>
</p>

Every recovered page is a small piece of broadcasting history that might
otherwise disappear. Thank you for helping keep the work moving.

## Highlights

- Inspect full broadcast captures.
- Import DVB teletext services directly from `.ts`, `.mts`, and `.m2ts` recordings.
- Compare every version of a page, subpage, row, or selected block.
- Restore good rows and blocks into a separate editable page.
- Edit text, mosaics, control codes, national subsets, and X/26 diacritics.
- Build a new squashed stream with configurable recovery filters.
- Export restored work as `.t42`, PNG screenshots, or video.
- Runs on Windows, macOS, and Linux.

## Workspace

The editor uses one or two synchronized panes:

- **Squashed / single page** — editable restoration result on the left.
- **Full broadcast** — read-only source capture on the right.

## Broadcast inspection

- Browse by magazine, page, subpage, and captured version.
- Use fast version buttons for direct selection and hover preview.
- Use **Flash Roll** to cycle all versions rapidly for visual comparison.
- Show control-code bytes and hexadecimal values directly in the grid.
- Show raw bytes across the complete current selection.
- Toggle X/26 diacritic markers only when they are needed.
- Inspect packet 8/30 status messages, UTC/local date and time,
  network ID and initial-page links via **Page → Broadcast service data…**.

## Restoration workflow

- Transfer any complete row from the broadcast into the squashed page.
- Select a block, hold `Shift`, and use `Left` / `Right` to browse that block
  through every broadcast version.
- Copy a broadcast selection directly into the same position on the left.
- Undo and redo independently for each restored page.
- Add or delete pages and subpages.

## Text, attributes, and mosaics

- Type directly into the editable 40 × 25 grid.
- Insert all Level 1 alpha and mosaic colours.
- Edit mosaic cells with the six Teletext mosaic keys.
- Choose the G0 national subset for the complete file.
- Choose a **Secondary G0 subset** for ESC switching.

## X/26 enhancements and diacritics

- Type supported precomposed letters as a G0 base character plus X/26 enhancement.
- Create, move, and delete Level 1.5 diacritics.
- Drag and drop a diacritic to another cell.
- Inspect X/26 packets and decoded triplets in the enhancements sidebar.
- Delete an individually selected triplet with undo support.

## Squash recovery

- Create a squashed stream directly from an open full broadcast using filters.
- Build consensus rows from repeated receptions and parity quality.
  
## Page bookmarks

- Add a bookmark to any restored page or subpage.
- Restore bookmarks when the same file is opened in a later session.
- Generate timestamp, page, subpage, and bookmark text after video export.

## Display and workflow options

- Choose any installed grid font; TIFAX is recommended.
- Move all three toolbars below the grids with **View > Toolbar on Bottom**.
- Show or hide the X/26 and page-bookmark sidebar sections.

## Export

- Save the restored result as raw `.t42` packets.
- Export the current page as a PNG screenshot.
- Batch-export every restored page as PNG.
- Export a sequence of pages as video through FFmpeg.

FFmpeg must be available on the system to use the video export feature.

## Keyboard shortcuts

Use `Ctrl` on Windows/Linux and `Cmd` on macOS.

| Shortcut | Action |
| --- | --- |
| `Ctrl/Cmd + O` | Open a squashed or single-page stream |
| `Ctrl/Cmd + S` | Save the restored stream |
| `Ctrl/Cmd + N` | Create a new page |
| `Ctrl/Cmd + C` | Copy the current cell or block |
| `Ctrl/Cmd + Shift + C` | Copy a broadcast block directly into the same position on the left |
| `Ctrl/Cmd + V` | Paste into the editable grid |
| `Ctrl/Cmd + Z` | Undo the current page edit |
| `Ctrl/Cmd + Shift + Z` | Redo the current page edit |
| `Arrow keys` | Move the active grid selection |
| `Shift + Left/Right` | Browse broadcast versions of the selected restoration block |
| `Backspace` | Clear the previous editable cell |
| `Enter` | Move to column 0 of the next row |
| `Q A Z W S X` | Toggle mosaic segments while editing a mosaic cell |
| `Shift + transfer-row click` | Pin or unpin the row guide |

## File format

### T42

- Raw sequence of 42-byte Teletext packets.
- Original packet data is preserved wherever possible during restoration.
- Line-aware captures use an all-zero 42-byte positional placeholder compatible
  with `vhs-teletext --keep-empty`; squashed exports omit those placeholders.

### DVB transport streams

- MPEG-TS and M2TS imports locate valid DVB teletext PES services automatically.
- Found EN 300 472 data units are converted directly to T42.

## Building from source

```bash
dotnet restore
dotnet run --project TeletextRecoveReese
```

Build the complete solution:

```bash
dotnet build TeletextRecoveReese.sln
```

## Command line options
Restore the previous session at startup:

```bash
dotnet run --project TeletextRecoveReese -- -loadlast
```

Explicitly start a new session at startup:

```bash
dotnet run --project TeletextRecoveReese -- -startnew
```

## Status

- Current development version: **0.9.2**
- Actively developed and used for real restoration work.
- Complete EN 300 706 coverage and broader interoperability testing are ongoing.
- Licensed under GPL-3.0.

## Thanks and inspiration

TeletextRecoveReese would not have been possible without the ideas, concepts, and
practical solutions demonstrated by these projects:

- **[Teletext Meddler](https://teletext.wiki.zxnet.co.uk/wiki/Teletext_Meddler)** —
  page restoration, version comparison, control-code inspection, and the idea that
  recovered captures deserve a purpose-built editor.
- **[QTeletextMaker](https://github.com/gkthemac/QTeletextMaker)** — Level 2.5 page
  authoring and a valuable reference point for X/26 enhancement editing.
- **[Teletext Recovery Editor](https://teletextarchaeologist.org/software/tre-documentation/)** —
  service-oriented recovery workflows, carousel handling, and restoration-focused
  page management.
- **[vhs-teletext](https://github.com/ali1234/vhs-teletext)** — the recovery and
  `.t42` toolchain that made large-scale VHS Teletext preservation practical.

Thanks to their respective authors and contributors for building the foundations, solving
hard problems, and sharing their work with the Teletext preservation community.
