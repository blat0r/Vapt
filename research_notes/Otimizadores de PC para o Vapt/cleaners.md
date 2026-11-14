# Windows Junk-File Cleaners (BleachBit, CCleaner, Wise, Storage Sense, cleanmgr, DISM) on Windows 11

## Key Question 1: Precise list of cleanable locations, how each is cleaned, and how to detect size before deleting

### Takeaway
BleachBit's open-source cleaner XML and Python code give a verifiable, path-exact inventory: user/system temp, WER reports, logs, memory dumps, Prefetch, thumbcache, the Recycle Bin (via the Shell APIs), Windows Update leftovers, Defender history and definition backups, and per-profile browser caches. The supported Microsoft routes are cleanmgr `/sageset`/`/sagerun` (VolumeCaches registry handlers), Storage Sense (HKCU StoragePolicy plus HKLM policy keys), DISM `/StartComponentCleanup` for WinSxS, and `Delete-DeliveryOptimizationCache` for Delivery Optimization (DO). Most of these sources also expose the size before you delete: SHQueryRecycleBin, DISM `/AnalyzeComponentStore`, `Get-DeliveryOptimizationPerfSnap`, or a recursive file-size sum.

### Cited Findings

**Windows temp, logs, dumps, Prefetch (BleachBit "System" cleaner, `bleachbit/Cleaner.py`)**
- `tmp`: deletes the children of `%TEMP%` and `%WINDIR%\Temp\`. It skips the `low` subfolder itself but not its contents, and keeps `gdbus-nonce-file-*` files modified in the last 7 days — [BleachBit Cleaner.py](https://raw.githubusercontent.com/bleachbit/bleachbit/master/bleachbit/Cleaner.py)
- `logs` globs:
  - Windows Error Reporting (WER): `%LocalAppData%\Microsoft\Windows\WER\ReportArchive\*\*`, `...\WER\ReportQueue\*\*`, and the same two under `%ProgramData%\Microsoft\Windows\WER\`
  - `%WINDIR%\*.log`, `%WINDIR%\Debug\*.log`, `%WINDIR%\Debug\UserMode\*.log`
  - `%WINDIR%\Microsoft.NET\Framework\*\*.log`, `%WINDIR%\security\logs\*.log` and `*.old`
  - `%WINDIR%\SoftwareDistribution\*.log` and `SoftwareDistribution\DataStore\Logs\*`
  - `System32\LogFiles\Firewall\pfirewall.log*`, `LogFiles\WMI\RTBackup\EtwRT.*etl`, `LogFiles\Scm\SCM.EVM*`, `wbem\Logs\*.log`
  - Paths are expanded with expandvars and matched with `glob.iglob`.
  — [BleachBit Cleaner.py](https://raw.githubusercontent.com/bleachbit/bleachbit/master/bleachbit/Cleaner.py)
- `memory_dump`: `%WINDIR%\memory.dmp` and `%WINDIR%\Minidump\*.dmp` — [BleachBit Cleaner.py](https://raw.githubusercontent.com/bleachbit/bleachbit/master/bleachbit/Cleaner.py)
- `prefetch`: `%WINDIR%\Prefetch\*.pf` — [BleachBit Cleaner.py](https://raw.githubusercontent.com/bleachbit/bleachbit/master/bleachbit/Cleaner.py)
- `muicache`: registry keys `HKCU\Software\Microsoft\Windows\ShellNoRoam\MUICache` and `HKCU\Software\Classes\Local Settings\Software\Microsoft\Windows\Shell\MuiCache` — [BleachBit Cleaner.py](https://raw.githubusercontent.com/bleachbit/bleachbit/master/bleachbit/Cleaner.py)
- `dns_cache` runs `ipconfig.exe /flushdns`. `clipboard` clears the clipboard — [BleachBit Windows.py](https://raw.githubusercontent.com/bleachbit/bleachbit/master/bleachbit/Windows.py)

**Recycle Bin (APIs)**
- BleachBit gets the size and item count with `SHQueryRecycleBin(path)` and empties with `SHEmptyRecycleBin(None, path, SHERB_NOSOUND|SHERB_NOCONFIRMATION|SHERB_NOPROGRESSUI)`. A `None` path targets all bins. Preview mode only returns the bytes that would be freed — [BleachBit Windows.py](https://raw.githubusercontent.com/bleachbit/bleachbit/master/bleachbit/Windows.py)
- It enumerates items with `SHGetSpecialFolderLocation(CSIDL_BITBUCKET)`, then `SHGetDesktopFolder`, `BindToObject`, and `GetDisplayNameOf(SHGDN_FORPARSING)`. This allows per-item listing and a keep-list. The code avoids a blanket `SHEmptyRecycleBin(NULL)` when there are items to keep — [BleachBit Windows.py](https://raw.githubusercontent.com/bleachbit/bleachbit/master/bleachbit/Windows.py); [Cleaner.py](https://raw.githubusercontent.com/bleachbit/bleachbit/master/bleachbit/Cleaner.py)

**Windows Update leftovers (BleachBit `delete_updates`)**
- Requires admin: it checks `IsUserAnAdmin()` and returns with a warning if not elevated — [BleachBit Windows.py](https://raw.githubusercontent.com/bleachbit/bleachbit/master/bleachbit/Windows.py)
- Deletes `%WINDIR%\$NtUninstallKB*`, `%WINDIR%\SoftwareDistribution` contents, `SoftwareDistribution.old`/`.bak`, `ie7updates`, `ie8updates`, `%SystemDrive%\windows.old`, `$windows.~bt` and `$windows.~ws`. The `catroot2` entry is commented out — [BleachBit Windows.py](https://raw.githubusercontent.com/bleachbit/bleachbit/master/bleachbit/Windows.py)
- If SoftwareDistribution has files, it stops the running services among `wuauserv`, `cryptsvc`, `bits` and `msiserver` (plus their running dependents), then restarts them, including on abort. Warning label: "may prevent uninstalling some updates" — [BleachBit Windows.py](https://raw.githubusercontent.com/bleachbit/bleachbit/master/bleachbit/Windows.py); [Cleaner.py](https://raw.githubusercontent.com/bleachbit/bleachbit/master/bleachbit/Cleaner.py)

**Windows Explorer (BleachBit `windows_explorer.xml`)**
- `thumbnails`: kills `explorer.exe` for the same user, deletes `%LOCALAPPDATA%\Microsoft\Windows\Explorer\thumbcache*.db`, then relaunches `%WINDIR%\explorer.exe`. Warning: restarts Explorer — [windows_explorer.xml](https://raw.githubusercontent.com/bleachbit/bleachbit/master/cleaners/windows_explorer.xml)
- `recent_documents`: deletes `%APPDATA%\Microsoft\Windows\Recent\*.lnk`, `Recent\AutomaticDestinations\*.automaticDestinations-ms` and `Recent\CustomDestinations\*customDestinations-ms`, plus registry key `HKCU\...\Explorer\RecentDocs`. Warning: resets pinned Quick Access items — [windows_explorer.xml](https://raw.githubusercontent.com/bleachbit/bleachbit/master/cleaners/windows_explorer.xml)
- `mru`: registry keys `HKCU\...\Explorer\UserAssist\{GUID}\Count` (4 GUIDs), `ComDlg32\LastVisitedPidlMRU`, `ComDlg32\OpenSavePidlMRU`, `Map Network Drive MRU` and others. After cleaning it sends a shell change notify — [windows_explorer.xml](https://raw.githubusercontent.com/bleachbit/bleachbit/master/cleaners/windows_explorer.xml)
- `run`: `HKCU\...\Explorer\RunMRU`. `search_history`: `...\Explorer\WordWheelQuery` — [windows_explorer.xml](https://raw.githubusercontent.com/bleachbit/bleachbit/master/cleaners/windows_explorer.xml)
- `shellbags`: `HKCU\Software\Microsoft\Windows\Shell\Bags`/`BagMRU`, `ShellNoRoam\...`, and `HKCU\Software\Classes\Local Settings\Software\Microsoft\Windows\Shell\Bags`/`BagMRU`. Warning: resets desktop icon positions — [windows_explorer.xml](https://raw.githubusercontent.com/bleachbit/bleachbit/master/cleaners/windows_explorer.xml)

**Windows Defender (BleachBit `windows_defender.xml`)**
- History: `%ProgramData%\Microsoft\Windows Defender\Scans\History\Results\Quick\` and `...\Results\Resource\` — [windows_defender.xml](https://raw.githubusercontent.com/bleachbit/bleachbit/master/cleaners/windows_defender.xml)
- Temp: `%WINDIR%\Temp\MpCmdRun.log`, `MpSigStub.log`, `%WINDIR%\SoftwareDistribution\Download\Install\AS_*.exe` and `mpas-*.exe`, and `%ProgramData%\Microsoft\Windows Defender\Definition Updates\Updates\` — [windows_defender.xml](https://raw.githubusercontent.com/bleachbit/bleachbit/master/cleaners/windows_defender.xml)
- Backup: `...\Definition Updates\Backup\`. Quarantine: `...\Windows Defender\Quarantine`. Logs: `...\Support\MPLog-*.log`, `...\Scans\History\Service\Detections.log` — [windows_defender.xml](https://raw.githubusercontent.com/bleachbit/bleachbit/master/cleaners/windows_defender.xml)

**Chrome (Chromium layout; BleachBit `google_chrome.xml`)**
- Base: `%LocalAppData%\Google\Chrome\User Data`. Profiles: `User Data\Default` plus the glob `User Data\Profile *` — [google_chrome.xml](https://raw.githubusercontent.com/bleachbit/bleachbit/master/cleaners/google_chrome.xml)
- Cache:
  - Profile: `Cache\`, `Code Cache\`, `Media Cache\`, `GPUCache\`, `DawnGraphiteCache\`, `DawnWebGPUCache\`, `Network\Network Persistent State`
  - Base: `ShaderCache`, `GrShaderCache`, `GraphiteDawnCache`, `GPUPersistentCache`, `component_crx_cache`, `extensions_crx_cache`
  — [google_chrome.xml](https://raw.githubusercontent.com/bleachbit/bleachbit/master/cleaners/google_chrome.xml)
- AI models (new, potentially large):
  - Base: `OptGuideOnDeviceModel`, `OnDeviceHeadSuggestModel`, `OptGuideOnDeviceClassifierModel`
  - Profile: `OptGuideOnDeviceModel`, `OptGuideOnDeviceClassifierModel`
  — [google_chrome.xml](https://raw.githubusercontent.com/bleachbit/bleachbit/master/cleaners/google_chrome.xml)
- Crash reports: `User Data\Crashpad\`, `Crash Reports\` — [google_chrome.xml](https://raw.githubusercontent.com/bleachbit/bleachbit/master/cleaners/google_chrome.xml)
- Cookies: `Network\Cookies`, `Network\Cookies-journal`, `Extension Cookies`, `Network\TransportSecurity` — [google_chrome.xml](https://raw.githubusercontent.com/bleachbit/bleachbit/master/cleaners/google_chrome.xml)
- History: `History`, `Favicons`, `Top Sites`, `Shortcuts`, `Visited Links`, `DIPS`. These use SQLite-aware commands (`chrome.history`, `chrome.favicons`). Form history uses `Web Data` via `chrome.autofill` — [google_chrome.xml](https://raw.githubusercontent.com/bleachbit/bleachbit/master/cleaners/google_chrome.xml)
- Session: `Sessions\`, `Current Session`, `Last Session`, `Current Tabs`, `Last Tabs` — [google_chrome.xml](https://raw.githubusercontent.com/bleachbit/bleachbit/master/cleaners/google_chrome.xml)
- Site data: `IndexedDB`, `Local Storage`, `Service Worker`, `Session Storage\`, `File System`, `QuotaManager` — [google_chrome.xml](https://raw.githubusercontent.com/bleachbit/bleachbit/master/cleaners/google_chrome.xml)
- Passwords: `Login Data` and `Login Data-journal`, with the warning "This option will delete your saved passwords." There is also a "Vacuum" option that compacts SQLite files without deleting data — [google_chrome.xml](https://raw.githubusercontent.com/bleachbit/bleachbit/master/cleaners/google_chrome.xml)

**Firefox (BleachBit `firefox.xml`)**
- Cache: `%LocalAppData%\Mozilla\Firefox\Profiles\*\cache2`, `jumpListCache`, `OfflineCache`. The profile data root is `%AppData%\Mozilla\Firefox\Profiles\*` — [firefox.xml](https://raw.githubusercontent.com/bleachbit/bleachbit/master/cleaners/firefox.xml)
- Cookies: `cookies.sqlite`. History: `places.sqlite`, `favicons.sqlite`, `SiteSecurityServiceState.*`. Session: `sessionstore*.js*`, `sessionstore-backups/`. Passwords: `logins.json` and `signons.sqlite`, with a warning — [firefox.xml](https://raw.githubusercontent.com/bleachbit/bleachbit/master/cleaners/firefox.xml)

**Disk Cleanup (cleanmgr)**
- `cleanmgr /sageset:n` (n = 0–65535) writes REG_DWORD `StateFlagsNNNN` under each handler in `HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\VolumeCaches\<handler>` (0 = unchecked, 2 = checked). `cleanmgr /sagerun:n` runs that profile on all drives. `/d <drive>` picks a drive and is not used together with `/sagerun` — [Microsoft Learn: Automating Disk Cleanup](https://learn.microsoft.com/en-us/troubleshoot/windows-server/backup-and-storage/automating-disk-cleanup-tool)
- So a tool can run cleanmgr silently by writing `StateFlags` values (admin, HKLM) and then calling `/sagerun`. Each handler is a COM Disk Cleanup handler (IEmptyVolumeCache), per the linked "Creating a Disk Cleanup Handler" doc — [Microsoft Learn](https://learn.microsoft.com/en-us/troubleshoot/windows-server/backup-and-storage/automating-disk-cleanup-tool)
- Disk Cleanup's "clean up system files" mode includes Windows Update cleanup, which shrinks the component store — [Microsoft Learn: Clean up WinSxS](https://learn.microsoft.com/en-us/windows-hardware/manufacture/desktop/clean-up-the-winsxs-folder)

**WinSxS / DISM**
- Never delete WinSxS manually. Doing so "may severely damage your system so that your PC might not boot and make it impossible to update" — [Microsoft Learn](https://learn.microsoft.com/en-us/windows-hardware/manufacture/desktop/clean-up-the-winsxs-folder)
- Scheduled task `\Microsoft\Windows\Servicing\StartComponentCleanup`. It waits at least 30 days after an update before removing superseded components, has a 1-hour timeout, and can be run with `schtasks.exe /Run /TN "\Microsoft\Windows\Servicing\StartComponentCleanup"` — [Microsoft Learn](https://learn.microsoft.com/en-us/windows-hardware/manufacture/desktop/clean-up-the-winsxs-folder)
- `Dism.exe /online /Cleanup-Image /StartComponentCleanup` (elevated) does the same immediately, with no 30-day grace period and no timeout — [Microsoft Learn](https://learn.microsoft.com/en-us/windows-hardware/manufacture/desktop/clean-up-the-winsxs-folder)
- Adding `/ResetBase` removes all superseded versions. After that, "all existing update packages can't be uninstalled", though future updates can still be uninstalled. `/SPSuperseded` is listed only for Windows 10 — [Microsoft Learn](https://learn.microsoft.com/en-us/windows-hardware/manufacture/desktop/clean-up-the-winsxs-folder)
- Microsoft "strongly recommends not disabling component cleanup" — [Microsoft Learn](https://learn.microsoft.com/en-us/windows-hardware/manufacture/desktop/clean-up-the-winsxs-folder)

**Delivery Optimization**
- `Get-DeliveryOptimizationPerfSnap` returns `CacheSizeBytes` and `Files`, which lets you preview the cache size. `Get-DOConfig -Verbose` returns `WorkingDirectory`, the local cache folder. Use this instead of hardcoding a path — [Microsoft Learn: Monitor DO](https://learn.microsoft.com/windows/deployment/do/waas-delivery-optimization-monitor)
- `Delete-DeliveryOptimizationCache [-FileID] [-IncludePinnedFiles] [-Force]` clears the cache and its persisted data. `Get-DeliveryOptimizationLog` reads the DoSvc log directory and needs admin — [Microsoft Learn: Monitor DO](https://learn.microsoft.com/windows/deployment/do/waas-delivery-optimization-monitor)
- Third-party guides disagree on the cache path: some say `C:\ProgramData\Microsoft\Windows\Delivery Optimization\Cache`, others `%LocalAppData%\...` — [Appuals](https://appuals.com/clear-the-delivery-optimization-cache); conflicting guides in search results. Resolve it via `Get-DOConfig` `WorkingDirectory`.

**Storage Sense**
- Per-user key `HKCU\Software\Microsoft\Windows\CurrentVersion\StorageSense\Parameters\StoragePolicy`, values per a community script:

  | Value | Meaning |
  |---|---|
  | `01` | Enabled |
  | `04` | Clear temp files |
  | `08` | Clear Recycle Bin |
  | `32` | Clear Downloads |
  | `256` | Recycle Bin age, in days |
  | `512` | Downloads age, in days |
  | `2048` | Cadence |
  | `CloudfilePolicyConsent` | Allow clearing the OneDrive cache |

  — [CyberDrain](https://cyberdrain.com/monitoring-with-powershell-monitoring-storage-sense-settings/). The script maps 2048 = 1 to "Weekly", which conflicts with Microsoft's mapping below.
- Policy (HKLM) key `Software\Policies\Microsoft\Windows\StorageSense`:
  - `AllowStorageSenseGlobal`, `AllowStorageSenseTemporaryFilesCleanup`
  - Cadence: 0 = low free disk space (default), 1 = daily, 7 = weekly, 30 = monthly
  - Recycle Bin threshold: 0–365 days, default 30
  - Downloads threshold and cloud-content dehydration threshold: 0–365 days, default 0 = never
  - Not configured: Storage Sense is off until low disk space or the user enables it.
  — [Microsoft Learn: Policy CSP Storage](https://learn.microsoft.com/windows/client-management/mdm/policy-csp-storage)

### Inferences
- Size preview by category should be done the way BleachBit does it: enumerate the same globs in a dry run and sum the file sizes. Add `SHQueryRecycleBin` for the Recycle Bin, DISM `/AnalyzeComponentStore` (mentioned in the Microsoft "determine actual size of WinSxS" doc linked from the cleanup page) for WinSxS, and `Get-DeliveryOptimizationPerfSnap` for DO. Naive WinSxS folder sizes are inflated by hard links, so use DISM's report.
- Edge, Brave and Opera use the Chromium layout, so Chrome's per-profile relative paths apply to them. The standard user-data roots are `%LocalAppData%\Microsoft\Edge\User Data`, `%LocalAppData%\BraveSoftware\Brave-Browser\User Data`, and `%AppData%\Opera Software\Opera Stable` (Opera keeps profile data in Roaming and caches in `%LocalAppData%\Opera Software\Opera Stable`). These roots come from general knowledge and were not verified against BleachBit's edge/brave/opera XML in this session.
- Other commonly cleaned locations not verified in this session:
  - `%WINDIR%\SoftwareDistribution\Download`
  - The Delivery Optimization working directory (on many systems `%WINDIR%\ServiceProfiles\NetworkService\AppData\Local\Microsoft\Windows\DeliveryOptimization\Cache`)
  - `%LocalAppData%\CrashDumps`
  - `%LocalAppData%\D3DSCache`, NVIDIA DXCache/GLCache
  - `%WINDIR%\Logs\CBS`
  - `%ProgramData%\Microsoft\Windows\WER`
  
  Confirm each with `Get-DOConfig` and testing.
- For a product, the safest wholesale path is cleanmgr `/sagerun` with chosen VolumeCaches handlers, plus DISM `/StartComponentCleanup` without `/ResetBase`. These are Microsoft-supported and handle service and lock coordination internally.

### Gaps
- Could not fetch BleachBit's Edge, Brave and Opera XML, `deepscan.xml`, or the system XML for Windows. Browser roots other than Chrome and Firefox are inferred, not verified.
- No primary source found for the exact Windows 11 list of VolumeCaches handler subkey names (e.g., "Delivery Optimization Files", "Update Cleanup", "Windows Error Reporting Files", "D3D Shader Cache"). Enumerate the key on a live system.
- Wise Disk Cleaner, Wise Care 365 and CCleaner are closed-source. No exact path lists were found. CCleaner 7 reportedly does not support winapp2.ini — [Winapp2 GitHub](https://github.com/MoscaDotTo/Winapp2) (via search summary).
- Storage Sense value `1024` is undocumented.

## Key Question 2: Locked files / files in use, and which operations need admin

### Takeaway
Files in use should be skipped, or scheduled for deletion on reboot with `MoveFileExW(MOVEFILE_DELAY_UNTIL_REBOOT)`, as BleachBit does. Some caches need the owning process stopped first: Explorer for thumbcache, the browser for its profile, and the Windows Update and BITS services for SoftwareDistribution. These operations need admin: anything under `%WINDIR%` or `%ProgramData%`, update and service cleanup, DISM, cleanmgr `/sageset` (HKLM), Storage Sense policy keys, and DO cache commands and logs.

### Cited Findings
- BleachBit's `delete_locked_file` calls `MoveFileExW(path, NULL, MOVEFILE_DELAY_UNTIL_REBOOT)` (flag 4). A `PermissionError` is mapped to `EACCES`, which the worker treats as "flag for later deletion" — [BleachBit Windows.py](https://raw.githubusercontent.com/bleachbit/bleachbit/master/bleachbit/Windows.py)
- When elevated and working outside the user profile, BleachBit holds a no-delete-share handle on the parent directory so it can't be swapped. It refuses symlinks and junctions, which protects against redirect attacks during privileged deletes — [BleachBit Windows.py](https://raw.githubusercontent.com/bleachbit/bleachbit/master/bleachbit/Windows.py)
- Elevation: if already admin, BleachBit enables `SeBackupPrivilege` and `SeRestorePrivilege`. Otherwise it relaunches via `ShellExecuteEx` with the `runas` verb. A UAC cancel returns error 1223 — [BleachBit Windows.py](https://raw.githubusercontent.com/bleachbit/bleachbit/master/bleachbit/Windows.py)
- Windows Update cleanup requires admin and stops `wuauserv`, `cryptsvc`, `bits` and `msiserver` before deleting, then restarts them — [BleachBit Windows.py](https://raw.githubusercontent.com/bleachbit/bleachbit/master/bleachbit/Windows.py)
- The thumbnail cache is locked by Explorer. BleachBit terminates `explorer.exe`, deletes the cache, and relaunches Explorer — [windows_explorer.xml](https://raw.githubusercontent.com/bleachbit/bleachbit/master/cleaners/windows_explorer.xml)
- DISM component cleanup must run from an elevated prompt — [Microsoft Learn](https://learn.microsoft.com/en-us/windows-hardware/manufacture/desktop/clean-up-the-winsxs-folder)
- `Get-DeliveryOptimizationLog` without `-Path` needs admin, and `-Flush` stops DoSvc. The `Set-DO*` cmdlets require elevation — [Microsoft Learn: Monitor DO](https://learn.microsoft.com/windows/deployment/do/waas-delivery-optimization-monitor)
- cleanmgr profiles are stored in HKLM (`VolumeCaches\...\StateFlagsNNNN`), so writing them needs admin — [Microsoft Learn](https://learn.microsoft.com/en-us/troubleshoot/windows-server/backup-and-storage/automating-disk-cleanup-tool)

### Inferences
- Recommended design: a user-scope pass with no elevation (`%TEMP%`, `%LocalAppData%` caches, browser caches, thumbcache, the user's Recycle Bin) and a separate elevated "system" pass (`%WINDIR%\Temp`, logs, WER in ProgramData, SoftwareDistribution, DO, memory dumps, DISM). Use a separate elevated helper process rather than running the whole app as admin.
- Detect running browsers (by process name) and warn or skip their profiles, instead of force-killing them.
- Count skipped and locked files and report them, rather than failing the whole category.

### Gaps
- No Microsoft doc was fetched on the Restart Manager API (RmGetList), which can identify which process locks a file. It is commonly used for this but not verified here.

## Key Question 3: What is controversial or harmful (registry cleaners, Prefetch, driver store, ResetBase, browser data, RAM optimizers)

### Takeaway
Registry cleaning is unsupported by Microsoft, and damage from it may be unrecoverable. Deleting Prefetch is a long-debunked myth that slightly slows app launches. `/ResetBase` and update-uninstaller deletion remove the ability to roll back updates. Deleting browser cookies or passwords is destructive to user data, not "junk". "RAM optimization" (EmptyWorkingSet or standby-list purge) mostly throws away useful cache and has no controlled evidence of benefit, apart from niche cases.

### Cited Findings
- Microsoft does not support registry cleaners. Problems they cause "may not be fixable", and lost data may not be recoverable (KB 2563254) — [Microsoft Support KB2563254](https://support.Microsoft.com/kb/2563254); summary via [TheWindowsClub](https://www.thewindowsclub.com/microsoft-support-registry-cleaners-windows)
- Prefetch: Microsoft Q&A says the folder is self-maintaining and doesn't need to be emptied. Windows reads an app's .pf only when that app launches. Deleting the folder also wipes SysMain's databases stored there, causing a temporary slowdown — [Windows Latest, Oct 2026](https://www.windowslatest.com/2026/10/26/microsoft-explains-why-deleting-prefetch-can-slow-down-windows-11-as-its-designed-to-speed-up-app-launches/); [Ed Bott](https://edbott.com/2005/06/01/one-more-time-do-not-clean-out-your-prefetch-folder/); [MS archive blog (Ryan Myers)](https://learn.microsoft.com/en-nz/archive/blogs/ryanmy/misinformation-and-the-the-prefetch-flag)
- Disabling SysMain barely changed boot time and slightly slowed cold app launches in a Windows Latest benchmark — [Windows Latest](https://www.windowslatest.com/2026/10/26/everyone-says-disabling-sysmain-speeds-up-windows-11-so-i-benchmarked-it-on-two-pcs-k4l9/)
- Even BleachBit, a privacy cleaner, includes Prefetch only as an option, and it flags warnings on shellbags (icon positions), recent docs (Quick Access pins) and passwords — [windows_explorer.xml](https://raw.githubusercontent.com/bleachbit/bleachbit/master/cleaners/windows_explorer.xml); [google_chrome.xml](https://raw.githubusercontent.com/bleachbit/bleachbit/master/cleaners/google_chrome.xml)
- `/ResetBase` makes existing updates impossible to uninstall. Deleting WinSxS manually can make Windows unbootable — [Microsoft Learn](https://learn.microsoft.com/en-us/windows-hardware/manufacture/desktop/clean-up-the-winsxs-folder)
- BleachBit's Windows Update cleaner deletes `windows.old` and `$NtUninstallKB*`, which removes rollback to the previous Windows version and some update uninstalls. It warns "may prevent uninstalling some updates" — [BleachBit Windows.py](https://raw.githubusercontent.com/bleachbit/bleachbit/master/bleachbit/Windows.py)
- RAM optimizers:
  - RAMMap's Empty Working Sets moves process pages to the standby or modified list. Tools like ISLC and AmazeMem call `NtSetSystemInformation(MemoryPurgeStandbyList)` and psapi `EmptyWorkingSet`.
  - The standby list is the file cache, so purging it forces later disk reads. "Empty Priority 0 Standby List" is the least harmful variant.
  - No controlled benchmark was found. Dramatic claims came from unattributed pages.
  
  — [Bitsum (vendor, has a commercial interest)](https://bitsum.com/?p=2933); [AmazeMem (MajorGeeks)](https://www.majorgeeks.com/files/details/amazemem.html); ["The Memory Optimization Hoax" (IT Pro, PDF)](https://www.tweakhound.com/BadTweaks_pdfback/The%20Memory-Optimization%20Hoax%20_%20IT%20Pro.pdf); [comss.ru on standby list](https://www.comss.ru/page.php?id=21206)
- CCleaner extras (Driver Updater, registry cleaner) draw mostly anecdotal criticism. No authoritative 2025 controversy source was found. The Driver Updater database was marketed as ">90 million drivers" in v6.25 — [AskWoody](https://askwoody.com/forums/topic/reader-feedback-on-driver-software-updaters-and-more); [MalwareTips CCleaner thread](https://malwaretips.com/threads/ccleaner-updates-thread.110554/page-4)
- Piriform (CCleaner) asked BleachBit to remove its winapp2.ini import option. This shows the cleaning-rule ecosystem is contested — [gHacks](https://www.ghacks.net/?p=83972)

### Inferences
- Risk tiers for a product UI:
  - **Safe** (default on): user and system temp older than about 24h, WER report queues, browser *cache* (not cookies), thumbcache, the Delivery Optimization cache, Defender scan history, Recycle Bin (with confirmation and size shown), DISM StartComponentCleanup (no ResetBase), old logs.
  - **Caution** (default off, explained): memory dumps (needed for crash debugging), `windows.old` and `$Windows.~BT` (removes rollback), `SoftwareDistribution\Download` (only with WU stopped), browser cookies and sessions (logs the user out), MRU, jump lists and Quick Access.
  - **Avoid / don't offer:** registry cleaning, Prefetch deletion, `/ResetBase` (or offer only with a strong warning), WinSxS manual deletion, browser passwords, driver store purges, RAM "boosters".
- On the driver store: removing old packages with `pnputil /delete-driver oemNN.inf` is the supported mechanism. Bulk removal can remove fallback drivers needed for rollback or for reconnecting hardware. This comes from general knowledge, not verified this session.
- RAM purge has a legitimate niche: an old Windows 10 game-stutter bug popularised ISLC. On Windows 11 the evidence is anecdotal. Present it as "no measurable benefit expected" or omit it.

### Gaps
- No primary Microsoft source fetched for the driver store (pnputil, DriverStore\FileRepository) cleanup risks.
- No controlled benchmark of standby-list purging on Windows 11 found.
- The 2017 CCleaner 5.33 supply-chain compromise and Avast-era data-collection controversies were not researched or cited in this session.
- Wise Care 365 and Wise Memory Optimizer claims were not checked against independent tests. One review summary notes that lower "used memory" doesn't prove better performance (search summary only).

## Key Question 4: Good UX patterns (scan → preview sizes by category → clean)

### Takeaway
The reference tools follow the same pattern: per-category checkboxes with descriptions and warnings, a dry-run "preview" that computes bytes and file counts without deleting, then a clean pass with a skipped/locked report. Microsoft's own tools add "View Files" per category and age thresholds (Storage Sense, cleanmgr temp ">1 week").

### Cited Findings
- BleachBit's architecture:
  - Each option has a label, a description and an optional `<warning>`.
  - Preview mode runs the same actions without deleting. For example, `empty_recycle_bin` returns only the size in preview, and recycle-bin emptying is wrapped so that it's skipped in preview.
  - Process kills and relaunches are explicit, as with thumbnails and Explorer.
  
  — [BleachBit Cleaner.py](https://raw.githubusercontent.com/bleachbit/bleachbit/master/bleachbit/Cleaner.py); [windows_explorer.xml](https://raw.githubusercontent.com/bleachbit/bleachbit/master/cleaners/windows_explorer.xml)
- Disk Cleanup lists categories with descriptions and "View Files" buttons. It notes temp files "not modified in over a week" are safe to delete. Only Recycle Bin and catalog files are available on non-system drives — [Microsoft Learn](https://learn.microsoft.com/en-us/troubleshoot/windows-server/backup-and-storage/automating-disk-cleanup-tool)
- Storage Sense uses age thresholds (Recycle Bin 30 days by default, Downloads never by default) and a cadence (on low disk space by default) — [Microsoft Learn: Policy CSP Storage](https://learn.microsoft.com/windows/client-management/mdm/policy-csp-storage)
- BleachBit keeps temp files that are newer than 7 days or in use (the gdbus nonce example), which is a safety margin for recently used files — [BleachBit Cleaner.py](https://raw.githubusercontent.com/bleachbit/bleachbit/master/bleachbit/Cleaner.py)

### Inferences
- Recommended flow:
  1. Scan without elevation for user categories, with an optional "Include system files (admin)" toggle, as cleanmgr does.
  2. Show a category tree with size, file count and a risk badge, and expandable file lists.
  3. Pre-check only Safe items, and require explicit opt-in for Caution items with an inline consequence ("you will be logged out of sites", "can't uninstall current updates").
  4. Apply an age filter (e.g., temp files older than 24h or 7d).
  5. Detect running apps (browsers, Explorer) and offer to close them.
  6. Clean, then show freed bytes, skipped/locked counts, and items scheduled for deletion at reboot.
- Send to the Recycle Bin or log deletions where feasible. Never touch the Downloads folder by default (Microsoft's default is "never").
- Use Microsoft-native back-ends where they exist (cleanmgr `/sagerun`, DISM, `Delete-DeliveryOptimizationCache`). They are supported and handle service coordination themselves.

### Gaps
- No UX study or usability research found comparing cleaner UIs. These recommendations are derived from tool behaviour, not user research.
