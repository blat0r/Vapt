# Disk space visualizers (SpaceSniffer, WizTree, WinDirStat 2.x, TreeSize, GrandPerspective, dua/gdu/diskus/pdu) and how to build a fast SpaceSniffer-like analyzer

Research date: 2026-11-14. Focus: Windows 11.

## Q1. What exact UX features does SpaceSniffer have, and how do the other tools present data?

### Takeaway
SpaceSniffer (closed-source freeware, v2.2.0.27, March 2026) is a nested, labeled treemap. You navigate it by clicking: a single click reveals deeper levels, and a double-click zooms a folder to fill the view. A typed filter bar combines wildcards, size and age conditions, exclusions and color tags. WinDirStat 2.x (now 2.9.3) is the open-source reference: cushion-shaded treemap plus sunburst, flame graph, Hilbert and Moore layouts, MFT scanning and hardlink tracking. WizTree (4.33, Sept 2026) is the speed benchmark.

### Cited Findings
**SpaceSniffer**
- Developer: Uderzo Software (Umberto Uderzo, Italy). First release 18 Apr 2009. Latest stable 2.2.0.27, dated 25 Mar 2026. 64-bit only, portable (no installer), freeware, no source code — [Wikipedia](https://en.wikipedia.org/wiki/SpaceSniffer); version confirmed at [Official site](http://www.uderzo.it/main_products/space_sniffer/)
- The treemap draws folders and files as rectangles sized by disk usage — [Official site](http://www.uderzo.it/main_products/space_sniffer/)
- Navigation: **a single click on a folder shows its contents, and further clicks reveal deeper levels** (progressive detail). **A double-click zooms a small folder to fill the whole view** — [Official site](http://www.uderzo.it/main_products/space_sniffer/)
- Filter syntax (typed in the filter field, applied with Enter) — [Official site](http://www.uderzo.it/main_products/space_sniffer/):
  - `*.jpg` shows only JPEGs. `|*.jpg` shows everything except JPEGs (`|` means exclude).
  - `>2years` matches old files. Size and date terms combine with `;`, e.g. `*.jpg;>1mb;<3months;|:yellow`.
  - Tags: hover a file and press **Ctrl+1** to tag it red. Other keys tag yellow, green and blue. `:red` filters to red-tagged items, `:all` to all tagged items, and `|:red` excludes red ones.
- Export Module produces a textual report with a customizable layout — [Official site](http://www.uderzo.it/main_products/space_sniffer/)
- Filters by name, age and size; custom colors per file type; NTFS Alternate Data Streams support; command-line usage — [Wikipedia](https://en.wikipedia.org/wiki/SpaceSniffer)

**WinDirStat 2.x (open-source C++ rewrite, GitHub windirstat/windirstat)**
- 2.0.1 released 16 Oct 2024, the first official release in 19 years. 2.2.0 released 6 Jan 2025 — [WinDirStat blog](https://blog.windirstat.net/)
- The CHANGELOG's latest entry is **2.9.3**. Milestones — [CHANGELOG](https://raw.githubusercontent.com/windirstat/windirstat/master/CHANGELOG.md):
  - 2.0.1: per-drive multithreading, backup/restore privileges to read inaccessible files, logical vs physical size column, transparent compression support
  - 2.5.0: **direct NTFS MFT scanning option**, hardlink tracking, treemap can show logical or physical size
  - 2.6.0: improved treemap navigation and zooming
  - 2.8.0: Hilbert and Moore treemap layouts, flame graph and sunburst views, smaller buffers for faster remote scans
  - 2.9.0: batched updates for scan, tree and File Watcher; cancellable MFT loading; lower memory; sparse hashing for duplicates over 64 MiB
  - 2.9.3: faster direct NTFS volume scanning
- README: "accelerated local NTFS scanning when elevated" (File > Run Elevated). Scans can be paused, resumed and refreshed. Logical vs physical size, hardlink exploration and dedup, duplicate finder (off by default), runs on Windows 7 through Server 2025 — [GitHub README](https://github.com/windirstat/windirstat)
- Supply-chain caution: a trojanized fake WinDirStat installer has circulated — [search summary of WinDirStat blog / malwaretips](https://malwaretips.com/threads/windirstat-updates-thread.133269/)

**WizTree**
- Latest 4.33 (16 Sep 2026). WizTreeMac 1.00 is a new macOS 12+ release — [WizTree What's New](https://diskanalyzer.com/whats-new)
- The treemap shows all files and folders sized by space use. Also scans FAT/FAT32, network drives, USB and MTP/PTP devices — [About WizTree](https://diskanalyzer.com/about)

**TreeSize (JAM Software)**
- TreeSize Free uses the MFT for local NTFS drives. Without MFT access, or on network drives, it falls back to a file-system scan with 2 parallel threads (up to 32 in Professional). Admin rights are needed for installation, but the app runs without elevation, which suggests a privileged helper. TreeSize Free is not usable on Windows Server — [JAM changes page / search summary](https://www.jam-software.com/treesize_free/changes.shtml); [TreeSize features](https://www.jam-software.com/treesize/features.shtml)

**GrandPerspective (macOS, open source)**
- Treemap where each file is a rectangle with area proportional to its size. Files from the same folder are grouped, but their placement is arbitrary. After a scan it rescans only folders flagged as changed by macOS file-system events (live/incremental updates) — [GrandPerspective site](https://grandperspectiv.sourceforge.net/); [Softpedia](https://mac.softpedia.com/get/Utilities/GrandPerspective.shtml)
- Sources disagree on the current version (3.7.2, 3.8.0 or 3.8.1) — [FOSSHub](https://www.fosshub.com/GrandPerspective.html)

**Cushion treemaps (WinDirStat / SequoiaView style)**
- Van Wijk and van de Wetering, INFOVIS'99. Each subdivision adds a parabolic ridge, so every rectangle gets a shaded "cushion". The height scales down per level, deep valleys mark major subdivisions and shallow valleys separate siblings. SequoiaView (TU Eindhoven) is the original disk browser built on it — [Cushion Treemaps paper](https://www.win.tue.nl/~wstahw/papers/ctm.pdf); [SequoiaView](https://www.win.tue.nl/sequoiaview/Main_Frame/Sequoiaview.html)
- Critique: simple, fast and gives a good view of overall structure, but size perception can be ambiguous — [UBC slides](https://www.cs.ubc.ca/~tmm/courses/533-09/slides/hier.anika.pdf)

### Inferences
- SpaceSniffer's look differs from WinDirStat's in a key way. It uses **nested, labeled blocks**: each folder is a framed box with a title strip and children inside. WinDirStat draws only leaf files with cushion shading, and folders show up only as shading valleys. To get SpaceSniffer's look, use a squarified layout with `paddingTop` for a label strip plus outer and inner padding (see Q3). Render only to a depth budget (the "detail level"), and expand deeper levels on click or hover.
- Back navigation, a detail-level slider and live file-system updates are widely reported for SpaceSniffer (it re-renders when files change) but are not documented on the official page (see Gaps). For our app: keep a zoom stack (breadcrumb plus Back/Up via Backspace or mouse back), offer a detail-depth slider, and drive live updates with ReadDirectoryChangesW or USN journal deltas.

### Gaps
- Official documentation does not confirm SpaceSniffer's live-update behavior, its Back/Forward buttons or exact shortcuts, the "detail" slider, or its default color-by-type scheme. The official page and Wikipedia don't cover them, and no reliable review was found. Verify by running the app.
- No official WinDirStat release dates were found for 2.5 through 2.9.3 (the CHANGELOG has no dates).

## Q2. How do WizTree and similar tools achieve their speed? Is an MFT reader feasible from Node, Rust or C#, and does it need admin?

### Takeaway
The fast path reads the NTFS Master File Table directly from a raw volume handle (`\\.\C:`) and builds the tree in memory from parent references. This avoids millions of per-directory kernel round trips. Opening a volume handle requires Administrator rights, so every tool (WizTree, WinDirStat, TreeSize) either requires elevation or ships a privileged helper or service. Without admin, they fall back to multithreaded `FindFirstFileEx`/`NtQueryDirectoryFile` enumeration. This is feasible from Rust or C# (and from Node through a native addon). WinDirStat's open-source `FinderNtfs.cpp` is a ready blueprint.

### Cited Findings
**WizTree**
- On NTFS drives it reads the MFT directly from disk, which bypasses Windows file APIs and gives a large speed boost. The only timing given is a testimonial (anecdotal): "almost 18 minutes" in WinDirStat versus "just over 14 seconds" in WizTree — [About WizTree](https://diskanalyzer.com/about)
- Changelog items — [WizTree What's New](https://diskanalyzer.com/whats-new):
  - 4.21: more than 4x faster SSD scanning in non-admin mode, plus improved threading for non-admin, non-NTFS, network and folder scans
  - 4.28: up to 2x faster for non-admin, non-NTFS, network and folder scans
  - 4.20: optional MFT scan even when a folder is selected (off by default, can be forced with Ctrl); MFT up to 15% faster
  - 3.00: hardlink support. Hardlinked files are counted once in allocated totals, and **detecting them requires admin**. Also added per-file "Allocated Size".
  - 4.14: allocated size became the default sort
- Secondary sources say the MFT path needs WizTree run as Administrator and that it otherwise falls back to slower standard scanning — [search summary incl. tecnobits, geekchamp](https://tecnobits.com/en/wiztree-vs-windirstat) (secondary, consistent with the 4.21/4.28 notes)

**WinDirStat's MFT reader (open source, the best reference implementation)**, from `FinderNtfs.cpp` — [GitHub source](https://github.com/windirstat/windirstat/blob/master/windirstat/FinderNtfs.cpp):
1. Open the volume with `CreateFile("\\.\C:", FILE_READ_DATA | FILE_READ_ATTRIBUTES | SYNCHRONIZE, …)` using overlapped I/O.
2. Call `FSCTL_GET_NTFS_VOLUME_DATA` to get `BytesPerFileRecordSegment` (usually 1024) and `BytesPerCluster`.
3. Open the `$MFT` stream and get its extents (data runs) with `FSCTL_GET_RETRIEVAL_POINTERS`, retrying on `ERROR_MORE_DATA`. The total record count comes from the last VCN.
4. Read each run in **4 MiB chunks** using overlapped `ReadFile`, with **runs processed in parallel** via `std::for_each(std::execution::par, …)`. Each run has its own buffer, child map and name arena, so the only shared state is a `readFailed` atomic.
5. For each 1 KiB record: check the `FILE` signature, apply the update-sequence (fixup) array (512-byte sectors), and keep records flagged in-use.
6. Parse attributes:
   - 0x10 `$STANDARD_INFORMATION`: timestamps and attributes
   - 0x30 `$FILE_NAME`: skip DOS 8.3 names and `.`/`..`; key by `ParentDirectory`. **Every non-DOS name creates a child entry**, which is how hardlinks show up under multiple parents.
   - 0x80 `$DATA`: logical and physical size; named streams handled separately, including `WofCompressedData`
   - 0xC0 `$REPARSE_POINT`: reparse tag, WOF compression, junction detection
7. Collect extension records (`BaseFileRecordNumber > 0`) and merge them into their base record after the pass, then build the tree from a parent-to-children map.

**FSCTL_ENUM_USN_DATA (lighter alternative)**
- Enumerates MFT records via `DeviceIoControl` on a `\\.\X:` volume handle; the volume must be NTFS. Start with `StartFileReferenceNumber = 0`, and each output buffer begins with the next starting point. Not supported over SMB 3.0 — [Microsoft Learn: FSCTL_ENUM_USN_DATA](https://learn.microsoft.com/en-us/windows/win32/api/winioctl/ni-winioctl-fsctl_enum_usn_data)
- Returns USN_RECORD_V2/V3 entries depending on the `MFT_ENUM_DATA_V1` major-version range — [Microsoft Learn: MFT_ENUM_DATA_V1](https://learn.microsoft.com/en-us/windows/win32/api/WinIoCtl/ns-winioctl-mft_enum_data_v1)
- Everything (voidtools) reads the MFT for its initial index and then uses the USN Journal (`FSCTL_READ_USN_JOURNAL`) to stay up to date — [voidtools forum](https://ftp.voidtools.com/forum/viewtopic.php?p=51399)

**Fallback: Win32 enumeration**
- `FIND_FIRST_EX_LARGE_FETCH` uses a larger buffer for directory queries, which "can increase performance". It requires Windows 7 / Server 2008 R2 or later — [Microsoft Learn: FindFirstFileExA](https://docs.microsoft.com/en-us/windows/desktop/api/fileapi/nf-fileapi-findfirstfileexa)
- Raymond Chen recommends the flag for enumeration on a background thread but not on the UI thread — [The Old New Thing](https://devblogs.microsoft.com/oldnewthing/?p=2843)
- `FindExInfoBasic` (skips 8.3 short-name generation) gave more than 54x speedup in one benchmark (directory of 2,000 files, first run) versus calling `GetFileAttributesEx` per file — [C++ Stories](https://cppstories.com/2024/cpp-query-file-attribs-faster); similar at [Visual Assist blog](https://www.wholetomato.com/blog/how-to-query-file-attributes-50x-faster-on-windows/)
- Git for Windows switched to a 64 KiB buffer with `FIND_FIRST_EX_LARGE_FETCH` to reduce kernel round trips — [git patch](https://source-hub.etos-pro.ru/microsoft/git/commit/5dad60c9b76df053a42ca1e472bc8c2888a85a90.patch). Contradicted by a Delphi developer, who found no benefit locally and a slowdown for small directories, though it helps on network shares with many entries — [delphitools](https://www.delphitools.info/2013/11/25/efficient-file-enumeration/2/)

**Rust and other libraries**
- `ntfs` crate (Colin Finck, ReactOS): low-level, read-only, `no_std`-compatible NTFS 3.x reader (Windows 2000 through 11), safe on mounted partitions, works over any Read+Seek source — [docs.rs ntfs](https://docs.rs/crate/ntfs/latest)
- `mft` crate: MFT parser aimed at an extracted `$MFT` file (forensics use) — [docs.rs mft](https://docs.rs/crate/mft/0.7.0/source)
- `ntfs-core`: a separate crate over any Read+Seek source; claims are self-described — [docs.rs ntfs-core](https://docs.rs/crate/ntfs-core/0.9.1)

### Inferences
- **Admin requirement.** Opening `\\.\C:` for raw reads or for `FSCTL_ENUM_USN_DATA` requires an elevated token in practice. Microsoft's CreateFile docs on "Physical Disks and Volumes" state that the caller must have administrative privileges ([CreateFile docs](https://learn.microsoft.com/en-us/windows/win32/api/fileapi/nf-fileapi-createfilew)); this came from memory of the docs and was not re-fetched in this session. WizTree requires admin for MFT and hardlink detection, and WinDirStat says "when elevated". TreeSize's "admin for install, app runs unelevated" pattern implies a privileged service or helper.
- Recommended architecture for our app:
  - **(a) Elevated helper.** A small Rust or C# exe, launched via UAC `runas` or installed as a service, reads the MFT and streams a compact binary tree (record#, parent#, name, logical size, allocated size, attributes, reparse tag) over a named pipe to the UI.
  - **(b) Non-admin fallback.** A parallel directory walk with a work-stealing pool (Rust `jwalk`/rayon, or C# `Parallel` with a `ConcurrentQueue`). Use `FindFirstFileExW(FindExInfoBasic, FIND_FIRST_EX_LARGE_FETCH)` or `NtQueryDirectoryFile`/`GetFileInformationByHandleEx(FileIdExtdDirectoryInfo)` with a 64 KiB+ buffer.
  - **(c) Live updates.** Use the USN journal (`FSCTL_READ_USN_JOURNAL`) after an MFT scan, or `ReadDirectoryChangesW` in fallback mode.
- **Choosing between MFT methods.** `FSCTL_ENUM_USN_DATA` is simpler (no record parsing) but USN records carry **no file size**. You'd still need sizes, either per-file queries or a raw `$MFT` read. That is why WizTree and WinDirStat parse raw FILE records, where `$DATA` gives logical and allocated size in one pass.
- **Node.js and Electron.** Raw MFT parsing in pure JS is possible (`fs.open('\\\\.\\C:')` with an elevated process). It's better done in a native addon (napi-rs Rust, or N-API C++) or a sidecar exe. Never run the scanner in the renderer; stream results to the UI in batches. Rust/Tauri (sidecar or command) and C# (.NET 8+ with `SafeFileHandle` + `DeviceIoControl` P/Invoke) are both natural fits.

### Gaps
- No independent, controlled benchmark of WizTree files/sec was found; the official figure is anecdotal. Commonly cited numbers like "1M files in ~1–3 s" are unsourced here.
- I did not verify whether `FSCTL_ENUM_USN_DATA` works unelevated on Windows 11 (no primary source either way; one anecdotal snippet says admin is needed).
- MFT reading does not apply to ReFS (Dev Drive), FAT/exFAT or network shares. A fallback is required there; nothing found was specific to ReFS.

## Q3. Concrete implementation guidance: squarified treemap, Canvas rendering, hardlinks/junctions/reparse points, allocated vs logical size, permission errors, performance numbers

### Takeaway
Use d3-hierarchy's `treemapSquarify` (Bruls et al., golden-ratio default) with `paddingTop` for SpaceSniffer-style labeled nesting. Render to Canvas with a depth and minimum-pixel budget. Lay out lazily per zoom root. Count allocated size and deduplicate hardlinks by file ID. Never follow junctions or symlinks, and record access-denied directories as error nodes. Parallel Rust walkers process a 400k-file / 90 GB tree in about 4.5–6 s cold and 0.27–0.6 s warm on SSD.

### Cited Findings
**Squarified layout**
- d3-hierarchy `treemap()`. You must call `root.sum()` first, and sorting is recommended. The default tile is `treemapSquarify` (Bruls et al.) with ratio φ ≈ 1.618; the ratio must be ≥ 1 and is only a hint. — [d3 treemap docs](https://d3js.org/d3-hierarchy/treemap)
- `treemapResquarify` keeps previous adjacencies, which suits animated or live updates, though only the first layout is truly squarified. — [d3 treemap docs](https://d3js.org/d3-hierarchy/treemap)
- `.paddingTop(n)` reserves a strip for parent labels, alongside `.paddingInner`, `.paddingOuter` and `.round(true)`. A padding function is called per node with children. — [d3 treemap docs](https://d3js.org/d3-hierarchy/treemap)
- The original paper is Bruls, Huizing, van Wijk, "Squarified Treemaps" (2000) — [paper PDF](https://vanwijk.win.tue.nl/stm.pdf). Text could not be extracted in this session. The algorithm summary below is from general knowledge, so verify it against the paper.

**Hardlinks, size accounting, reparse points**
- WizTree counts hardlinked files once in allocated totals, which requires admin to detect. It shows "Allocated Size" (actual disk space) as the default sort — [WizTree What's New](https://diskanalyzer.com/whats-new)
- WinDirStat tracks logical vs physical size, WOF/NTFS compression, sparse files and hardlinks (with dedup fixes in 2.9.0). Its MFT reader detects junctions from `$REPARSE_POINT` and WOF-compressed files from the `WofCompressedData` stream — [CHANGELOG](https://raw.githubusercontent.com/windirstat/windirstat/master/CHANGELOG.md); [FinderNtfs.cpp](https://github.com/windirstat/windirstat/blob/master/windirstat/FinderNtfs.cpp)
- WinDirStat 2.0.1 uses backup/restore privileges to read inaccessible files when elevated — [CHANGELOG](https://raw.githubusercontent.com/windirstat/windirstat/master/CHANGELOG.md)
- Each tool takes a different hardlink policy:
  - gdu counts hardlinks once and flags them `H` — [gdu README](https://github.com/dundee/gdu)
  - pdu treats "all hardlinks as equally real", with optional dedup that slows it proportionally, and doesn't follow symlinks — [pdu README](https://github.com/KSXGitHub/parallel-disk-usage)
  - dua doesn't follow symlinks and has an `--apparent-size` flag — [dua README](https://github.com/Byron/dua-cli)

**Performance numbers**
- gdu README hyperfine benchmark on a 90 GB directory with 100k dirs and 400k files, on a 500 GB SSD (Linux) — [gdu README](https://github.com/dundee/gdu):

  | Tool | Cold cache | Warm cache |
  |---|---|---|
  | diskus | 4.489 s | 270.8 ms |
  | gdu | 4.716 s | 466.1 ms |
  | pdu | 5.969 s | 299.1 ms |
  | dua | 6.030 s | 590.6 ms |
  | dust | 6.181 s | 578.7 ms |
  | du | 30.6 s | 1255 ms |
  | ncdu | 33.2 s | 2222 ms |

  That works out to about 100k files/s cold and roughly 1.0–1.8M files/s warm for the parallel tools (derived: 500k entries ÷ time).
- dua memory: about 60 MB per million entries — [dua README](https://github.com/Byron/dua-cli)
- gdu is aimed at SSDs, where parallelism pays off; it offers `--sequential` for HDDs and `-m` to cap cores — [gdu README](https://github.com/dundee/gdu)
- TreeSize Free's non-MFT fallback uses 2 threads (Professional up to 32) — [TreeSize features](https://www.jam-software.com/treesize/features.shtml)

### Inferences (implementation guidance)
**1. Data model.** Store the scan as a flat struct-of-arrays rather than JS objects per file (dua's 60 MB/M entries is a good target):
- `parent: u32[]`, `firstChild`/`nextSibling` or child-index ranges
- `logical: u64[]`, `allocated: u64[]`, `attrs: u32[]`
- `nameOffset` into a single UTF-16 or UTF-8 string arena

In Electron, keep the arrays in the main process or a worker, transfer them as `ArrayBuffer`s, and never build millions of DOM nodes. Aggregate sizes bottom-up once, after the scan, in reverse topological order (MFT gives arbitrary order, so build children lists and then do a post-order pass).

**2. Squarify algorithm (Bruls et al.), to implement directly or via `d3.treemapSquarify`:**
- Sort children by size, descending.
- Grow a "row" along the shorter side of the remaining rectangle while the worst aspect ratio improves. With row sum s, side w, and min/max r⁻/r⁺ in the row, `worst = max(w²·r⁺/s², s²/(w²·r⁻))`.
- When adding the next item would make `worst` larger, lay out the row, shrink the rectangle and start a new row.
- Cost is O(n log n), from the sort.

For SpaceSniffer-style nesting: for each folder rectangle, subtract a header strip (about 14–18 px when the rect is large enough to label) and a 1–2 px border, then squarify the children inside the remainder. Equivalently: `d3.treemap().tile(d3.treemapSquarify).paddingTop(d => d.depth < maxDepth ? 16 : 0).paddingInner(1).paddingOuter(2).round(true)`.

**3. Level of detail and laziness:**
- Lay out only from the current zoom root, and stop recursing when a rect falls below about 3–4 px or the depth exceeds the "detail" slider value.
- Group items below the threshold into a single "N small files" tile, as SpaceSniffer and WizTree do visually.
- Re-layout on zoom or resize. Layout on the visible subset is cheap (tens of thousands of rects), so keep it under 16 ms.

**4. Canvas rendering:**
- Use one `<canvas>` (or OffscreenCanvas in a worker) and draw rects with `fillRect`, using a color per extension category (hashed or palette) and darker borders by depth.
- Draw labels only when `width > textWidth + pad`, with `measureText` cached per string.
- Optional cushion shading: either a per-rect linear/radial gradient, or the van Wijk parabolic surface computed per pixel in a WebGL fragment shader.
- Hit-testing: keep the laid-out rects in a flat array sorted by depth, or a quadtree. On mousemove, find the deepest rect containing the point for the tooltip/hover.
- Navigation: single click selects or expands one more level; double-click sets the zoom root (animate x0..x1 interpolation); Backspace or mouse-back pops the zoom stack; show a breadcrumb.
- For live updates, use `treemapResquarify` to keep tiles stable.

**5. Sizes:**
- Show **allocated size by default** (what actually frees space), with a toggle for logical size.
- In fallback mode, get allocated size via `GetFileInformationByHandleEx(FileStandardInfo).AllocationSize` or `FileIdExtdDirectoryInfo` (the directory-enumeration buffers include `AllocationSize`, so you avoid opening each file).
- Compressed and WOF files have allocated < logical. Sparse files likewise.
- Cloud placeholders (OneDrive Files On-Demand, `FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS`/`OFFLINE`) should count as allocated ≈ 0, and you must never read or hydrate them. WinDirStat notes that cloud physical allocation is unknown.

**6. Hardlinks:**
- Dedup on (volume serial, 64/128-bit FileId). In MFT mode the record number is the identity, so count bytes once (first parent seen) and mark the other occurrences.
- In fallback mode, only check `NumberOfLinks > 1` files via `GetFileInformationByHandle`, to keep it cheap.
- WinSxS and `C:\Windows` contain many hardlinks; without dedup, totals overstate disk usage.

**7. Reparse points and junctions:**
- Never traverse directories with `FILE_ATTRIBUTE_REPARSE_POINT` (junctions like `C:\Documents and Settings`, symlinks, mount points, AppExecLinks). Show them as zero-size leaf nodes with an icon.
- Treat dedup, WOF and cloud-file tags as normal files.
- Track visited volume IDs/FileIds to avoid cycles.

**8. Permission errors:**
- Treat `ERROR_ACCESS_DENIED` on `System Volume Information`, other users' profiles or `WindowsApps` as an "inaccessible" node with unknown size, and keep going.
- Offer "Restart as administrator", which enables MFT mode and `SeBackupPrivilege` (open with `FILE_FLAG_BACKUP_SEMANTICS`) as WinDirStat does.
- The volume's total "used" minus the scanned sum gives an "unknown / system / shadow copies" tile, which is a common trick (WizTree shows reserved space per its 4.21 notes).

**9. Threading model (fallback scanner):**
- Use a work-stealing pool sized at about 2× logical cores for SSD/NVMe, but 1–2 threads for HDD (gdu `--sequential`, TreeSize Free uses 2).
- Batch progress to the UI every ~100 ms; don't send per-file messages over IPC.
- In Rust: `jwalk` (rayon-based parallel walkdir) or a hand-rolled rayon scope with `windows-sys` calls. In C#: `Parallel.ForEach` over a channel, with `FileSystemEnumerable<T>` (uses `NtQueryDirectoryFile` internally, no 8.3 names). In Node: a native addon. `fs.opendir` per directory with libuv's threadpool works, but it's much slower.

**10. Stack recommendation for "Vapt":**
- If the app is Electron, add a Rust napi-rs addon or sidecar exe implementing MFT mode (port of WinDirStat's FinderNtfs logic, or Colin Finck's `ntfs` crate over a raw `\\.\C:` handle) and the parallel fallback. Stream a binary snapshot to a renderer that uses d3-hierarchy for layout and Canvas or WebGL for drawing.
- Tauri with Rust is the same design with less glue.

### Gaps
- I couldn't extract text from the Bruls et al. PDF, so the worst-ratio formula above comes from established knowledge rather than a fetched quote.
- No Windows-specific benchmarks (files/sec) for dua, gdu or pdu were found; the gdu numbers are from a Linux SSD. No published files/sec figures for WizTree, TreeSize or WinDirStat MFT mode were found beyond the anecdotal 14 s vs 18 min.
- pdu's own benchmark numbers are in images and a separate repo, not extracted. jwalk crate details were not fetched in this session.
