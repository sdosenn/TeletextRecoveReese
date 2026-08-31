# TeletextRecoveReese

An AI-built editor for restoring VHS Teletext recoveries, developed with OpenAI
Codex — with a name that makes a Kyle Reese pun, much to his horror.

<img width="780" height="488" alt="TeletextRecoveReese preview" src="https://github.com/user-attachments/assets/ebddef02-19d4-4bae-8d66-f8500a5df900" />

TeletextRecoveReese preserves every captured version of a page and helps combine
the best surviving parts into a new, editable `.t42` stream.

## Download

<p align="center">
  <a href="https://github.com/sdosenn/TeletextRecoveReese/releases/tag/0.2-beta">
    <img src="https://img.shields.io/badge/Download_latest_release-0.2_beta-2ea44f?style=for-the-badge&logo=github" alt="Download TeletextRecoveReese 0.2 beta">
  </a>
</p>

**[Download the latest release here](https://github.com/sdosenn/TeletextRecoveReese/releases/tag/0.2-beta)**
and choose the package for Windows, Linux, Intel Mac, or Apple Silicon Mac.

## Highlights

- Inspect full broadcast captures.
- Compare every version of a page, subpage, row, or selected block.
- Restore good rows and blocks into a separate editable page.
- Edit text, mosaics, control codes, national subsets, and X/26 diacritics.
- Build a new squashed stream with configurable recovery filters.
- Export restored work as `.t42`, PNG screenshots, or video.
- Run on Windows, macOS, and Linux.

## Workspace

The editor uses one or two synchronized panes:

- **Squashed / single page** — editable restoration result on the left.
- **Full broadcast** — read-only source capture on the right.
- **Transfer controls** — copy complete captured rows from right to left.
- **Single-pane mode** — keeps the restoration grid and sidebars visible.
- **Dual-pane mode** — compares the restoration directly with the broadcast.

If a full broadcast is opened through the single-page command, the editor detects
the repeated page versions and opens it correctly as a read-only broadcast.

## Broadcast inspection

- Browse by magazine, page, subpage, and captured version.
- Preserve all repeated instances in their original capture order.
- Use fast version buttons for direct selection and hover preview.
- Use **Flash Roll** to cycle all versions rapidly for visual comparison.
- Pin a yellow row guide with `Shift` + click on a transfer button.
- Show control-code bytes and hexadecimal values directly in the grid.
- Show raw bytes across the complete current selection.
- Toggle X/26 diacritic markers only when they are needed.
- Keep the broadcast pane read-only to protect the source capture.

## Restoration workflow

- Transfer any complete row from the broadcast into the squashed page.
- Select a block, hold `Shift`, and use `Left` / `Right` to browse that block
  through every broadcast version.
- Preview block versions without creating history entries while `Shift` is held.
- Commit the final block as one undoable edit when `Shift` is released.
- Copy a broadcast selection directly into the same position on the left.
- Undo and redo independently for each restored page.
- Add or delete pages and subpages.
- Jump between matching pages in the two panes.

## Text, attributes, and mosaics

- Type directly into the editable 40 × 25 grid.
- Insert all Level 1 alpha and mosaic colours.
- Insert flash/steady, conceal, box, and background control codes.
- Use separate double-height and double-width controls.
- Use normal size or double size from **Edit > Insert**.
- Toggle contiguous or separated mosaics.
- Insert hold and release mosaic attributes.
- Edit mosaic cells with the six Teletext mosaic keys.
- Overwrite control-code cells while respecting the attribute state established
  earlier in the row.
- Keep double-width and double-size drawing clipped inside the grid.
- Choose the G0 national subset for the complete file or leave it on Auto.

## X/26 enhancements and diacritics

- Type supported precomposed letters as a G0 base character plus X/26 enhancement.
- Create, move, and delete Level 1.5 diacritics.
- Drag and drop a diacritic to another cell.
- Preserve existing raw enhancement chains when inserting new characters.
- Replace unusable triplets without discarding unrelated enhancement data.
- Inspect X/26 packets and decoded triplets in the enhancements sidebar.
- Copy complete packet diagnostics for before/after comparisons.
- Delete an individually selected triplet with undo support.
- Right-click a marked diacritic to remove only that character enhancement.

## Squash recovery

- Create a squashed stream directly from an open full broadcast.
- Keep singleton pages instead of automatically throwing them away.
- Build consensus rows from repeated receptions and parity quality.
- Preserve X/26 packets selected from the available versions.
- Filter by minimum body rows and minimum reception count.
- Restrict hexadecimal pages and the maximum subpage number.
- Filter service headers by learned similarity.
- Follow separate reading, profiling, filtering, and recovery progress phases.

## Page bookmarks

- Add a bookmark to any restored page or subpage.
- Keep page `100` as the automatic Index bookmark when available.
- Navigate to a bookmarked page by clicking it in the sidebar.
- Show or hide bookmarks independently from X/26 enhancements.
- Restore bookmarks when the same file is opened in a later session.
- Generate timestamp, page, subpage, and bookmark text after video export.

## Display and workflow options

- Choose any installed grid font; TIFAX is recommended.
- Simulate Teletext flash with a real timer.
- Suppress flashing when a steady display is preferred.
- Move all three toolbars below the grids with **View > Toolbar on Bottom**.
- Show or hide the X/26 and page-bookmark sidebar sections.
- Remember display toggles, font, recent files, panes, selected pages, and video
  settings between sessions.
- Reopen up to ten recent files in the pane and page where they were last used.

## Export

- Save the restored result as raw `.t42` packets.
- Export the current page as a PNG screenshot.
- Batch-export every restored page as PNG.
- Export a sequence of pages as video through FFmpeg.
- Choose an installed video codec and page duration.
- Include or suppress Teletext flashing in the video.
- Export at original resolution or 1080p.
- Keep the original aspect ratio or stretch to 4:3 at 1440 × 1080.
- Scale video with smooth filtering rather than nearest-neighbour sampling.
- Follow progress while frames are created and encoded.
- Abort a video render in progress.
- Reuse the last selected video settings.

FFmpeg must be available on the system before video export is enabled.

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

Typing advances automatically through the editable grid. When the page-bookmark
text field has focus, all typing and cursor keys remain inside that field.

## File format

### T42

- Raw sequence of 42-byte Teletext packets.
- Two address bytes followed by forty payload bytes.
- Read and written directly without converting the page to an unrelated format.
- Original packet data is preserved wherever possible during restoration.

## Releases and source builds

Ready-to-run packages are available from the
**[latest release](https://github.com/sdosenn/TeletextRecoveReese/releases/tag/0.2-beta)**:

- Windows x64 self-contained ZIP
- Linux x64 AppImage
- macOS Intel application ZIP
- macOS Apple Silicon application ZIP

Older and future versions remain available on the
[complete GitHub Releases page](https://github.com/sdosenn/TeletextRecoveReese/releases).

To build from source, install the .NET 10 SDK:

```bash
dotnet restore
dotnet run --project TeletextRecoveReese
```

Restore the previous session at startup:

```bash
dotnet run --project TeletextRecoveReese -- -loadlast
```

Build the complete solution:

```bash
dotnet build TeletextRecoveReese.sln
```

## Status

- Current version: **0.2 beta**
- Actively developed and used for real restoration work.
- Core `.t42` loading, comparison, editing, squashing, and export are available.
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

Thank you to their authors and contributors for building the foundations, solving
hard problems, and sharing their work with the Teletext preservation community.

---

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

---

## Message from Reese

<img width="584" height="500" alt="Reese" src="https://github.com/user-attachments/assets/81d6b8dd-63d3-4289-8fb7-4ed07bf13981" />
