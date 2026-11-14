# Windows 11 optimization and tweaking utilities: tweak catalog and how each is implemented (Optimizer, WinUtil, Sophia Script, O&O ShutUp10++, Winaero Tweaker)

Scope: hellzerg/Optimizer (source read: `Optimizer/OptimizeHelper.cs`, `StartupHelper.cs`), Chris Titus Tech WinUtil (source read: `config/tweaks.json`), Sophia Script (README plus Win11 preset `Sophia.ps1`), O&O ShutUp10++ and Winaero Tweaker (official pages plus secondary sources). Research date: 2026-11-14.

Notation: HKLM = HKEY_LOCAL_MACHINE, HKCU = HKEY_CURRENT_USER, CCS = SYSTEM\CurrentControlSet, `...\Explorer\Advanced` = `HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced`. Values are DWORD unless noted. **Ratings (safe / moderate / risky) and reversibility labels are my own judgment, based on the mechanism the source code shows. They are not quoted from the tools.**

---

## Q1. Which feature categories does hellzerg/Optimizer have, and how does it apply and revert each?

### Takeaway
Optimizer (v17.0, .NET Framework 4.8, Windows 7 through 11) is a C# WinForms app. Almost every tweak is a hard-coded Disable/Enable method pair that writes registry values, sets service `Start` values, runs `.bat`/`.reg` resources, or calls `icacls`/`schtasks`/`sc`/`fsutil`. Revert usually deletes the values it wrote or sets the service back to Start=2. Several reverts are incomplete (policy values left behind, icacls deny not undone, blocked processes not unblocked). The README documents no restore point, backup or undo system.

### Cited Findings
**Product facts**
- Latest version 17.0 (released 12 Nov 2026). Supports Windows 7/8/8.1/10/11 and needs .NET Framework 4.8. Windows Server 2008–2022 only runs with the `/unsafe` switch. Supports 24 languages. Silent runs use a template file (documented in CONFS.md and AUTOMATION.md). — [Optimizer README](https://github.com/hellzerg/optimizer)
- Features the README lists:
  - Privacy: disable telemetry, Cortana, Office 2016+ telemetry, and Copilot AI in Windows 11 and Edge; stop automatic updates.
  - System: enhance system and network performance; disable unnecessary services; advanced tweaks (HPET, OneDrive); UTC time; uninstall UWP apps; clean the system drive and browser profiles; fix common registry issues; disk activity monitor; file lock handle finder and killer; hardware inspector.
  - Startup manager.
  - Network: ping and latency, SHODAN lookup, DNS presets, flush DNS.
  - Editors: HOSTS file and PATH variables.
  - Integrator: desktop right-click menu items and custom Run-dialog commands.
  - Bulk app downloader. — [Optimizer README](https://github.com/hellzerg/optimizer)
- The README does not mention restore points, backup or undo. — [Optimizer README](https://github.com/hellzerg/optimizer)

**Implementation from `OptimizeHelper.cs`** — all items in this subsection: [OptimizeHelper.cs](https://raw.githubusercontent.com/hellzerg/optimizer/master/Optimizer/OptimizeHelper.cs)

- **Telemetry tasks**
  - Runs `icacls "<...\DiagnosisAutoLogger>" /deny SYSTEM:(OI)(CI)F`.
  - Blocks `CompatTelRunner.exe` and `DeviceCensus.exe` from running.
  - Runs `DisableTelemetryTasks.bat`.
  - Revert runs `EnableTelemetryTasks.bat`. **The icacls deny is not reverted.**
- **Telemetry services**
  - Stops these services and sets `CCS\Services\<svc>\Start=4`: `DiagTrack`, `diagnosticshub.standardcollector.service`, `dmwappushservice`, `DcpSvc`. Also disables `WdiServiceHost` via `DisableProtectedService`.
  - Policies it sets:
    - `HKLM\SOFTWARE\Policies\Microsoft\Windows\AppCompat`: DisableEngine=1, SbEnable=0, AITEnable="0", DisableInventory=1, DisablePCA=1, DisableUAR=1. The same is written under the Wow6432Node path.
    - `HKLM\SOFTWARE\Policies\Microsoft\Windows\System`: PublishUserActivities="0".
    - `HKLM\SOFTWARE\Policies\Microsoft\SQMClient\Windows`: CEIPEnable="0".
    - `HKLM\...\CurrentVersion\Device Metadata`: PreventDeviceMetadataFromNetwork="1".
    - `HKLM\CCS\Control\WMI\AutoLogger\SQMLogger`: Start=0.
    - `HKLM\SOFTWARE\Microsoft\PolicyManager\current\device\System`: AllowExperimentation=0.
    - Wi-Fi Sense keys under `PolicyManager\default\WiFi`.
  - Revert sets Start=2 and starts the services. **The policy values are left in place.**
- **Enhance privacy (EnhancePrivacy / CompromisePrivacy)**
  - `HKLM\SOFTWARE\Policies\Microsoft\Windows\DataCollection`: AllowTelemetry=0, DoNotShowFeedbackNotifications=1.
  - `HKLM\CCS\Control\WMI\AutoLogger\AutoLogger-Diagtrack-Listener`: Start=0.
  - `HKLM\SOFTWARE\Policies\Microsoft\Windows\LocationAndSensors`: DisableLocation=1, DisableLocationScripting=1, DisableWindowsLocationProvider=1.
  - `HKLM\SOFTWARE\Policies\Microsoft\Biometrics`: Enabled=0.
  - `HKLM\SOFTWARE\Policies\Microsoft\FindMyDevice`: AllowFindMyDevice=0.
  - `HKLM\SOFTWARE\Policies\Microsoft\Windows\System`: EnableActivityFeed=0, EnableCdp=0, UploadUserActivities=0.
  - `HKCU\...\ContentDeliveryManager`: RotatingLockScreenEnabled=0, plus Spotlight and tailored-experience values.
  - The method sets about 100 values in total. Revert deletes them.
- **Cortana and search**
  - `HKLM\SOFTWARE\Policies\Microsoft\Windows\Windows Search`: AllowCortana=0, DisableWebSearch=1, ConnectedSearchUseWeb="0", ConnectedSearchUseWebOverMeteredConnections="0", AllowCloudSearch="0".
  - `HKCU\...\CurrentVersion\Search`: BingSearchEnabled, CortanaConsent, HistoryViewEnabled, DeviceHistoryEnabled, AllowSearchToUseLocation, all "0".
  - `HKCU\...\SearchSettings`: IsDeviceSearchHistoryEnabled="0".
  - Revert deletes all of these.
- **Start menu ads (DisableStartMenuAds)**
  - `HKCU\...\ContentDeliveryManager`, all set to 0: SubscribedContent-88000326Enabled, -314559, -338387, -338389, -338393, -353694, -353696, -310093, -338388, ContentDeliveryAllowed, PreInstalledAppsEverEnabled, SilentInstalledAppsEnabled, SystemPaneSuggestionsEnabled, SubscribedContentEnabled, SoftLandingEnabled, FeatureManagementEnabled.
  - `HKCU\...\UserProfileEngagement`: ScoobeSystemSettingEnabled=0.
  - `HKCU\...\Notifications\Settings\Windows.SystemToast.Suggested`: Enabled=0.
  - `HKCU\...\Mobility`: OptedIn=0.
  - `DisableSearchBoxSuggestions=1` under `HKCU` and `HKLM\Software\Policies\Microsoft\Windows\Explorer`.
  - `HKLM\...\Policies\Explorer`: AllowOnlineTips=0.
- **Store auto-install**
  - `HKCU\...\ContentDeliveryManager`: SilentInstalledAppsEnabled=0, PreInstalledAppsEnabled=0, OemPreInstalledAppsEnabled=0.
  - `HKLM\SOFTWARE\Policies\Microsoft\Windows\CloudContent`: DisableSoftLanding=1, DisableWindowsConsumerFeatures=1.
  - `HKLM\SOFTWARE\Policies\Microsoft\WindowsStore`: AutoDownload=2.
- **Automatic updates**
  - `HKLM\SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate\AU`: NoAutoUpdate=0, AUOptions=2 (notify before download), NoAutoRebootWithLoggedOnUsers=1.
  - `HKLM\SOFTWARE\Microsoft\WindowsUpdate\UX\Settings`: UxOption=1.
  - Delivery Optimization DownloadMode=0 under `HKU\S-1-5-20\...\DeliveryOptimization\Settings`, and `HKLM\...\DeliveryOptimization\Config` DODownloadMode=0.
  - `HKLM\SOFTWARE\Policies\Microsoft\Speech`: AllowSpeechModelUpdate=0.
  - Revert deletes these values and `Schedule\Maintenance\MaintenanceDisabled`.
- **Performance tweaks**
  - `HKLM\...\CrashControl`: CrashDumpEnabled=3.
  - `HKCU\Control Panel\Desktop`: AutoEndTasks="1", HungAppTimeout="1000", WaitToKillAppTimeout="2000", LowLevelHooksTimeout="1000".
  - `HKLM\SYSTEM\CCS\Control`: WaitToKillServiceTimeout="2000" (default "5000").
  - `HKLM\...\Multimedia\SystemProfile`: SystemResponsiveness=1 (default 14), NoLazyMode=1, AlwaysOn=1.
  - `...\SystemProfile\Tasks\Games`: GPU Priority=8, Priority=6, Scheduling Category="High", SFIO Priority="High".
  - `HKLM\SOFTWARE\WOW6432Node\Microsoft\Windows Media Foundation`: EnableFrameServerMode=0.
  - Explorer policies: NoLowDiskSpaceChecks, LinkResolveIgnoreLinkInfo, NoResolveSearch, NoResolveTrack, NoInternetOpenWith, all 1.
  - Adds "Copy To/Move To" context-menu handlers.
  - Disables DiagTrack, diagsvc, diagnosticshub and dmwappushservice. Runs `sc config "RemoteRegistry" start= disabled`, **which is not reverted.**
- **Network throttling**
  - `HKLM\...\Multimedia\SystemProfile`: NetworkThrottlingIndex=0xFFFFFFFF.
  - `HKLM\SOFTWARE\Policies\Microsoft\Windows\Psched`: NonBestEffortLimit=0.
  - Revert: NonBestEffortLimit=80 and deletes NetworkThrottlingIndex.
- **Superfetch**
  - Sets SysMain Start=4.
  - `...\Memory Management\PrefetchParameters`: EnableSuperfetch=0, EnablePrefetcher=0, SfTracingState=1.
- **NTFS last-access timestamp**: `fsutil behavior set disablelastaccess 1`. Revert uses `2`.
- **Error reporting**
  - `HKLM\SOFTWARE\Policies\Microsoft\Windows\Windows Error Reporting`: Disabled=1.
  - `HKLM\SOFTWARE\Policies\Microsoft\PCHealth\ErrorReporting`: DoReport=0.
  - Sets WerSvc and wercplsupport Start=4.
- **Other services set to Start=4**
  - Program Compatibility Assistant: `PcaSvc`.
  - Xbox: `XboxNetApiSvc`, `XblAuthManager`, `XblGameSave`, `XboxGipSvc`, `xbgm`, plus `DisableXboxTasks.bat`.
  - Media sharing: `WMPNetworkSvc`.
  - Sensors: `SensrSvc`, `SensorService`.
  - HomeGroup, which is obsolete on Windows 11.
  - Print: Spooler is set to Start=3, not 4.
- **SMBv1/v2**: `CCS\Services\LanmanServer\Parameters` `SMB{v}`=0.
- **Game Bar and DVR**
  - Set to "0": `HKCU\...\GameDVR` AppCaptureEnabled, AudioCaptureEnabled, CursorCaptureEnabled; `HKCU\Software\Microsoft\GameBar` UseNexusForGameBarEnabled, ShowStartupPanel; `HKCU\System\GameConfigStore` GameDVR_Enabled.
  - `HKLM\Software\Policies\Microsoft\Windows\GameDVR`: AllowGameDVR=0.
- **OneDrive**
  - Policy: `HKLM\SOFTWARE\Policies\Microsoft\Windows\OneDrive` DisableFileSyncNGSC=1.
  - Uninstall:
    - Runs `OneDrive_Uninstaller.cmd`.
    - Deletes the OneDrive folders.
    - Deletes the tasks "OneDrive Standalone Update Task" and "...v2".
    - Sets `HKCR\CLSID\{018D5C66-4533-4307-9B53-224DE2ED1FE6}` System.IsPinnedToNameSpaceTree=0.
    - Deletes `HKCU\...\Run\OneDriveSetup`.
  - Reinstall runs `OneDriveSetup.exe`.
- **Browser and vendor telemetry**
  - Edge: `HKLM/HKCU\SOFTWARE\Policies\Microsoft\Edge` PersonalizationReportingEnabled=0, UserFeedbackAllowed=0, MetricsReportingEnabled=0, SpotlightExperiencesAndRecommendationsEnabled=0, Edge3PSerpTelemetryEnabled=0.
  - Chrome: `HKLM\SOFTWARE\Policies\Google\Chrome` MetricsReportingEnabled=0, ChromeCleanupReportingEnabled=0, ChromeCleanupEnabled=0, UserFeedbackAllowed=0, DeviceMetricsReportingEnabled=0. It also blocks `software_reporter_tool.exe`, **which is not reverted.**
  - Firefox: `HKLM\SOFTWARE\Policies\Mozilla\Firefox` DisableTelemetry=1, DisableDefaultBrowserAgent=1, and runs `schtasks /change /disable /tn "\Mozilla\Firefox Default Browser Agent <hash>"`.
  - NVIDIA: NvTelemetryContainer Start=4, and `schtasks /change /tn NvTmRep_/NvTmRepOnLogon_/NvTmMon_{B2FE1952-0186-46C3-BAEC-A80AA35AC5B8} /disable`.
  - Office 2016: runs `.bat` and `.reg` resources.
- **Cloud clipboard**
  - `HKLM\SOFTWARE\Policies\Microsoft\Windows\System`: AllowClipboardHistory=0, AllowCrossDeviceClipboard=0.
  - `HKCU\Software\Microsoft\Clipboard`: EnableClipboardHistory=0.
- **Windows 11 UI**
  - Taskbar alignment: `...\Explorer\Advanced` TaskbarAl=0 (left) or 1 (center).
  - Widgets: TaskbarDa=0. Revert deletes the value.
  - Chat/Meet Now: TaskbarMn=0, and HideSCAMeetNow=1 under HKCU and HKLM `...\Policies\Explorer`.
  - Snap Assist: EnableSnapAssistFlyout=0 and `HKCU\Control Panel\Desktop` DockMoving="0".
  - Compact mode: UseCompactMode=1.
  - Show all tray icons: `HKCU\...\Explorer` EnableAutoTray=0.
  - Classic context menu ("Show more options"): creates an empty `HKCU\Software\Classes\CLSID\{86ca1aa0-34aa-4e8b-a509-50c905bae2a2}\InprocServer32`. Revert deletes the CLSID tree.
  - Stickers: `HKLM\SOFTWARE\Microsoft\PolicyManager\current\device\Stickers` EnableStickers.
- **Copilot and AI**
  - `HKCU\...\Explorer\Advanced`: ShowCopilotButton=0.
  - `HKCU+HKLM\Software\Policies\Microsoft\Windows\WindowsCopilot`: TurnOffWindowsCopilot=1.
  - `HKCU+HKLM\Software\Policies\Microsoft\Windows\WindowsAI`: DisableAIDataAnalysis=1 (Recall snapshots).
  - `HKLM\SOFTWARE\Policies\Microsoft\Edge`: ComposeInlineEnabled=0, DefaultBrowserSettingsCampaignEnabled=0.
- **Security and system-level changes**
  - Virtualization-based security: `HKLM\SYSTEM\CCS\Control\DeviceGuard` EnableVirtualizationBasedSecurity=0.
  - Modern Standby (forces S3 sleep): `HKLM\SYSTEM\CCS\Control\Power` PlatformAoAcOverride=0.
  - UTC clock: `HKLM\SYSTEM\CCS\Control\TimeZoneInformation` RealTimeIsUniversal=1.
  - TPM/CPU check bypass:
    - `HKLM\SYSTEM\Setup\MoSetup`: AllowUpgradesWithUnsupportedTPMOrCPU=1.
    - `HKLM\SYSTEM\Setup\LabConfig`: BypassCPUCheck, BypassStorageCheck, BypassTPMCheck, BypassRAMCheck, BypassSecureBootCheck, all 1.
    - `HKCU\Control Panel\UnsupportedHardwareNotificationCache`: SV2=0, **which is not reverted.**
  - The file also contains Defender-disable and `DisableSystemRestore` code; the latter deletes shadow copies with `vssadmin`. The fetch summarised these but did not extract their exact keys.
- **Startup manager (`StartupHelper.cs`)**
  - Enumerates values under `Software\Microsoft\Windows\CurrentVersion\Run` and `\RunOnce` in HKLM and HKCU, plus `Software\Wow6432Node\Microsoft\Windows\CurrentVersion\Run` and `\RunOnce` (HKLM, 64-bit only).
  - Enumerates the startup folders `%ProgramData%\Microsoft\Windows\Start Menu\Programs\Startup` and `%AppData%\Microsoft\Windows\Start Menu\Programs\Startup`, recursively, for .exe, .bat, .cmd and .lnk files.
  - Calls a `StartupApproval.Refresh(item)` class whose key paths are not in that file.
  - Does not enumerate scheduled tasks. — [StartupHelper.cs](https://raw.githubusercontent.com/hellzerg/optimizer/master/Optimizer/StartupHelper.cs)

### Inferences
- Optimizer's design is "apply hard-coded value / delete it on revert". It does not snapshot the prior value, so "revert" means "back to what Optimizer assumes is the default". That is wrong when the user or an MDM had a different value. Vapt should store the original value, or record that it was absent (WinUtil's `OriginalValue`/`<RemoveEntry>` pattern), before writing.
- My rating of the Optimizer items:
  - **Safe and reversible**: taskbar alignment, widgets/chat buttons, compact mode, tray icons, classic context menu, Copilot policies, Start-menu ad and ContentDeliveryManager values, browser telemetry policies, cloud clipboard, Game Bar values, UTC.
  - **Moderate**: telemetry services, error reporting, SysMain/prefetch, Search/Cortana policies, update policies (AUOptions=2), Xbox services (breaks Game Pass and Xbox app sign-in), Spooler (breaks printing), sensors (breaks auto-rotation), NTFS last-access.
  - **Risky**: VBS off, LabConfig/MoSetup bypass, Modern Standby override, SMB changes, AppCompat engine disable, icacls deny on the DiagnosisAutoLogger folder (not reverted), System Restore disable with `vssadmin` deletion (irreversible), Defender disable, and the `RemoteRegistry` and process-block steps that are not reverted.

### Gaps
- I did not read the exact Defender, HPET, Hibernation, SystemRestore and Search methods, the `StartupApproval` class, or CONFS.md/AUTOMATION.md (template and CLI syntax). The fetch output was truncated or summarised for those sections.
- The contents of the `.bat` resources (which scheduled tasks `DisableTelemetryTasks.bat` and `DisableXboxTasks.bat` disable) were not read.

---

## Q2. Which telemetry, privacy, performance, UI and gaming tweaks are most common across the tools, and which registry keys and commands implement them?

### Takeaway
Telemetry is handled the same way everywhere: disable the DiagTrack service, set the `AllowTelemetry` policy, turn off the advertising ID and tailored experiences, block ContentDeliveryManager suggestions, disable activity history and location, and set Copilot/WindowsAI policies. UI tweaks converge on the same `Explorer\Advanced` values. Gaming and performance tweaks (MMCSS SystemProfile, network throttling, Game Bar/DVR, Game Mode, HAGS, power plans, visual effects) are the least standardized and least evidence-backed. WinUtil is the best machine-readable reference because each entry records `Path/Name/Value/OriginalValue` and an `InvokeScript`/`UndoScript`.

### Cited Findings
**WinUtil (`config/tweaks.json`)** — all items in this subsection: [WinUtil tweaks.json](https://raw.githubusercontent.com/ChrisTitusTech/winutil/main/config/tweaks.json)

- **Telemetry (`WPFTweaksTelemetry`)**
  - Services: `diagtrack` → Disabled, `WerSvc` → Disabled.
  - `HKCU\...\AdvertisingInfo`: Enabled=0.
  - `HKCU\...\Privacy`: TailoredExperiencesWithDiagnosticDataEnabled=0.
  - `HKCU\Software\Microsoft\Speech_OneCore\Settings\OnlineSpeechPrivacy`: HasAccepted=0.
  - `HKCU\Software\Microsoft\Input\TIPC`: Enabled=0.
  - `HKCU\Software\Microsoft\InputPersonalization`: RestrictImplicitInkCollection=1, RestrictImplicitTextCollection=1.
  - `...\InputPersonalization\TrainedDataStore`: HarvestContacts=0.
  - `HKCU\Software\Microsoft\Personalization\Settings`: AcceptedPrivacyPolicy=0.
  - `HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\DataCollection`: AllowTelemetry=0.
  - `...\Explorer\Advanced`: Start_TrackProgs=0.
  - `HKCU\Software\Microsoft\Siuf\Rules`: NumberOfSIUFInPeriod=0.
  - Script: `Set-MpPreference -SubmitSamplesConsent 2` and machine environment variable `POWERSHELL_TELEMETRY_OPTOUT=1`. Undo resets SubmitSamplesConsent to 1 and removes the variable.
- **Activity history**: `HKLM\SOFTWARE\Policies\Microsoft\Windows\System` EnableActivityFeed (the JSON writes 1 here, as extracted), PublishUserActivities=0, UploadUserActivities=0.
- **Location**
  - `lfsvc` → Disabled.
  - `HKLM\...\CapabilityAccessManager\ConsentStore\location`: Value="Deny".
  - `HKLM\...\Sensor\Overrides\{BFA794E4-F964-4FDB-90F6-51056BFE4B44}`: SensorPermissionState=0.
  - `HKLM\SYSTEM\Maps`: AutoUpdateEnabled=0.
- **Delivery Optimization**: `HKLM\SOFTWARE\Policies\Microsoft\Windows\DeliveryOptimization` DODownloadMode=0.
- **Consumer features**: `HKLM\SOFTWARE\Policies\Microsoft\Windows\CloudContent` DisableWindowsConsumerFeatures=1.
- **Device metadata**: `HKLM\SOFTWARE\Policies\Microsoft\Windows\Device Metadata` PreventDeviceMetadataFromNetwork=1.
- **WPBT**: `HKLM\SYSTEM\CCS\Control\Session Manager` DisableWpbtExecution=1.
- **Services**: CscService → Disabled, DiagTrack → Disabled, MapsBroker → Manual, StorSvc → Manual, SharedAccess → Disabled. The title says "Manual", but two of these are set to Disabled.
- **Hibernation**: `powercfg.exe /hibernate off`, plus `HKLM\System\CCS\Control\Session Manager\Power` HibernateEnabled=0 and `...\Explorer\FlyoutMenuSettings` ShowHibernateOption=0. Undo: `powercfg /hibernate on`.
- **Disk cleanup**: `cleanmgr.exe /d C: /VERYLOWDISK` and `Dism.exe /online /Cleanup-Image /StartComponentCleanup /ResetBase`. `/ResetBase` is irreversible: superseded updates can no longer be uninstalled.
- **Temp files**: deletes `$env:TEMP\*` and `$env:SystemRoot\Temp\*`.
- **Widgets removal**: `Remove-AppxPackage` of `Microsoft.WidgetsPlatformRuntime` and `MicrosoftWindows.Client.WebExperience` (-AllUsers). No undo.
- **Windows AI**
  - `HKLM\...\Policies\Explorer`: SettingsPageVisibility="hide:aicomponents".
  - `HKLM\SOFTWARE\Policies\WindowsNotepad`: DisableAIFeatures=1.
  - Writes an `Appx\AppxAllUserStore\EndOfLife\<SID>` key for CoreAI.
  - Removes the Copilot, `MicrosoftWindows.Client.CoreAI` and `MicrosoftOfficeHub` packages.
  - Disables the `WSAIFabricSvc` service and the optional feature `Recall`.
  - No undo.
- **Edge**
  - Debloat sets about 16 policies under `HKLM\SOFTWARE\Policies\Microsoft\Edge`, for example HideFirstRunExperience=1, EdgeShoppingAssistantEnabled=0, ShowMicrosoftRewards=0, DiagnosticData=0.
  - Removal runs `setup.exe --uninstall --system-level --force-uninstall --delete-profile`. Undo: `winget install Microsoft.Edge`.
- **OneDrive removal**: `OneDriveSetup.exe /uninstall`, a temporary icacls deny to protect user files, and the OneSyncSvc template set to Disabled. Undo: `winget install Microsoft.Onedrive`.
- **Visual effects (best performance)**
  - `HKCU\Control Panel\Desktop`: DragFullWindows="0", MenuShowDelay="200".
  - `HKCU\Control Panel\Desktop\WindowMetrics`: MinAnimate="0".
  - `HKCU\Control Panel\Keyboard`: KeyboardDelay="0".
  - `...\Explorer\Advanced`: ListviewAlphaSelect=0, ListviewShadow=0, TaskbarAnimations=0.
  - `...\Explorer\VisualEffects`: VisualFXSetting=3.
  - `HKCU\Software\Microsoft\Windows\DWM`: EnableAeroPeek=0.
  - Binary `UserPreferencesMask` = 90 12 03 80 10 00 00 00.
- **Network (IPv6 / Teredo)**: `HKLM\SYSTEM\CCS\Services\Tcpip6\Parameters` DisabledComponents = 32 (prefer IPv4), 1 (Teredo off, plus `netsh interface teredo set state disabled`) or 255 (IPv6 off, plus `Disable-NetAdapterBinding -Name * -ComponentID ms_tcpip6`).
- **Storage**: Reserved Storage via `DISM /Online /Set-ReservedStorageState /State:Disabled`. Storage Sense via `HKCU\...\StorageSense\Parameters\StoragePolicy` "01"=0.
- **svchost splitting**: `HKLM\SYSTEM\CCS\Control` SvcHostSplitThresholdInKB = installed RAM in KB. Undo value 3670016.
- **Background apps**: `HKCU\...\BackgroundAccessApplications` GlobalUserDisabled=1.
- **Notifications**: `HKCU\Software\Policies\Microsoft\Windows\Explorer` DisableNotificationCenter=1 and `HKCU\...\PushNotifications` ToastEnabled=0.
- **Classic context menu**: same CLSID `{86ca1aa0-...}` InprocServer32 trick as Optimizer, followed by an Explorer restart.
- **Search indexing**: WSearch → Disabled.
- **End Task in taskbar**: `...\Explorer\Advanced\TaskbarDeveloperSettings` TaskbarEndTask=1.
- **Home and Gallery removal**: `HKCU\Software\Classes\CLSID\{f874310e-b6b7-47dc-bc84-b9e6b38f5903}` and `{e88865ea-0e1c-4e20-9aa6-edcd0212c87c}` System.IsPinnedToNameSpaceTree=0, and LaunchTo=1.
- **Toggles**
  - Dark mode: `HKCU\...\Themes\Personalize` AppsUseLightTheme=0, SystemUsesLightTheme=0.
  - Verbose BSOD: `CrashControl` DisplayParameters=1, DisableEmoticon=1.
  - Verbose logon: `HKLM\...\Policies\System` VerboseStatus=1.
  - Mouse acceleration off: `HKCU\Control Panel\Mouse` MouseSpeed="0", MouseThreshold1="0", MouseThreshold2="0".
  - NumLock at boot: InitialKeyboardIndicators="2" under `HKU\.Default` and HKCU `Control Panel\Keyboard`.
  - Sticky Keys off: `HKCU\Control Panel\Accessibility\StickyKeys` Flags="506".
  - Bing in Start: `HKCU\...\Search` BingSearchEnabled.
  - Taskbar search: SearchboxTaskbarMode. Task View button: ShowTaskViewButton.
  - Game Mode: `HKCU\Software\Microsoft\GameBar` AllowAutoGameMode, AutoGameModeEnabled.
  - Long paths: `HKLM\SYSTEM\CCS\Control\FileSystem` LongPathsEnabled=1.
  - Multiplane overlay: `HKLM\SOFTWARE\Microsoft\Windows\Dwm` OverlayTestMode=5 and `HKLM\SYSTEM\CCS\Control\GraphicsDrivers` DisableOverlays=1.
  - Lock screen off: `HKLM\SOFTWARE\Policies\Microsoft\Windows\Personalization` NoLockScreen=1.
  - Start Recommended section: `HideRecommendedSection` under `HKLM\SOFTWARE\Policies\Microsoft\Windows\Explorer` and `PolicyManager\current\device\Start`, plus `PolicyManager\current\device\Education` IsEducationEnvironment.
  - Settings Home: `HKCU\...\Policies\Explorer` SettingsPageVisibility="hide:home".
  - Battery percentage: IsBatteryPercentageEnabled=1.
  - S3 sleep: PlatformAoAcOverride=0.
- **DNS presets** (combobox): DHCP, Google, Cloudflare (including malware and adult variants), OpenDNS, Quad9, AdGuard.
- **Other**: an "Ultimate Performance" power plan button and a button that downloads and runs O&O ShutUp10++.

**Sophia Script** — [Sophia Win11 preset Sophia.ps1](https://raw.githubusercontent.com/farag2/Sophia-Script-for-Windows/master/src/Sophia_Script_for_Windows_11/Sophia.ps1)
- Each function takes a parameter (`-Disable/-Enable`, `-Hide/-Show`), and each tweak has a function that restores the default. Policies it applies appear in gpedit.msc. — [Sophia README](https://github.com/farag2/Sophia-Script-for-Windows)
- The default Windows 11 preset (v8.1.0, 31 Oct 2026) runs, in order:
  - `InitialActions` (exits on failure), then `CreateRestorePoint`.
  - Privacy: `DiagTrackService -Disable`, `DiagnosticDataLevel -Minimal`, `ErrorReporting -Disable`, `FeedbackFrequency -Never`, `ScheduledTasks -Disable`, `SigninInfo -Disable`, `LanguageListAccess -Disable`, `AdvertisingID -Disable`, `WindowsWelcomeExperience -Hide`, `SettingsSuggestedContent -Hide`, `AppsSilentInstalling -Disable`, `WhatsNewInWindows -Disable`, `TailoredExperiences -Disable`, `BingSearch -Disable`.
  - Taskbar and Start: `TaskbarWidgets -Hide`, `SearchHighlights -Hide`, `TaskbarEndTask -Enable`, `StartRecommendedSection -Hide`.
  - System: `Hibernation -Disable`, `DeliveryOptimization -Disable`, `ReservedStorage -Disable`, `PowerPlan -High`, `NetworkAdaptersSavePower -Disable`, `RegistryBackup -Enable`, `WindowsAI -Disable`.
  - Gaming: `XBOXGameBar -Disable`, `GPUScheduling -Enable` (hardware-accelerated GPU scheduling).
  - Scheduled tasks: `CleanupTask`, `SoftwareDistributionTask` and `TempTask` with `-Register`.
  - **Security hardening (enables, not disables)**: `NetworkProtection -Enable`, `PUAppsDetection -Enable`, `DefenderSandbox -Enable`, `AppsSmartScreen -Enable`, `DNSoverHTTPS -Cloudflare`.
- Additional functions available (not all run by default): `Uninstall-UWPApps`, `OneDrive -Uninstall`, `Install-WSL`, `Set-Association` (default app per extension), `Set-UserShellFolderLocation`. — [Sophia Win11 preset Sophia.ps1](https://raw.githubusercontent.com/farag2/Sophia-Script-for-Windows/master/src/Sophia_Script_for_Windows_11/Sophia.ps1)
- **Version conflict**: the README says Windows 11 25H2+ is supported, but the preset header says 26H2+. — [Sophia README](https://github.com/farag2/Sophia-Script-for-Windows) vs [preset](https://raw.githubusercontent.com/farag2/Sophia-Script-for-Windows/master/src/Sophia_Script_for_Windows_11/Sophia.ps1)

**O&O ShutUp10++**
- Version 3.7.1143 (10 Nov 2026). Free, portable, Windows 10/11. A one-click recommended-settings option and continuous monitoring that re-applies settings after Windows updates are Premium-only. — [O&O ShutUp10++ page](https://www.oo-software.com/en/shutup10)

**Winaero Tweaker**
- Version 1.65; supports Windows 7 through 11.
- Categories: shortcut tools (run as admin without a UAC prompt, Safe Mode shortcuts); apps and features (disable Telemetry, Defender, Windows Update); networking (RDP port, mapped drives for elevated apps); appearance (taskbar transparency, clock seconds, Navigation Pane entries); context menus.
- Windows 11 tweaks: classic Start menu and taskbar, taskbar position, managing removed File Explorer settings.
- Undo: tweak export/import, an automatic Registry backup option, and "Reset all Group Policy options". — [Winaero Tweaker](https://winaerotweaker.com/)

### Inferences
- **Core consensus set for Vapt, rated safe and reversible when the original value is stored:**
  - AdvertisingInfo Enabled=0, TailoredExperiences=0, Start_TrackProgs=0, Siuf NumberOfSIUFInPeriod=0.
  - ContentDeliveryManager SubscribedContent-*/SilentInstalledAppsEnabled=0, CloudContent DisableWindowsConsumerFeatures=1.
  - Activity history policies; Bing in Start (BingSearchEnabled=0 / DisableSearchBoxSuggestions=1).
  - Copilot/WindowsAI policies.
  - Explorer\Advanced UI values (TaskbarAl, TaskbarDa, ShowTaskViewButton, HideFileExt, Hidden, LaunchTo, TaskbarEndTask).
  - Classic context-menu CLSID; dark mode; NumLock; Sticky Keys; mouse acceleration; long paths; verbose BSOD; Game Bar/DVR values.
- **Moderate**: DiagTrack and WerSvc disable; SysMain; WSearch; Delivery Optimization; Storage Sense; hibernation (which also disables Fast Startup); Background apps; notifications; IPv6 prefer-IPv4 (32); Teredo; svchost split threshold; reserved storage (should be re-enabled before feature updates).
- **Risky or irreversible**:
  - IPv6=255 (Microsoft does not recommend disabling IPv6).
  - DISM `/ResetBase`.
  - AppX removals (Widgets, CoreAI, Copilot) and Edge/OneDrive uninstall, which are only "reversible" by reinstalling from winget.
  - VBS off, LabConfig bypass, Defender off.
  - BitLocker disable (WinUtil offers it).
  - Multiplane overlay disable; PlatformAoAcOverride.

### Gaps
- No source fetched in this session gave the exact registry implementation of individual ShutUp10++ settings. Its .cfg format maps setting IDs (P001…, S001…) to its own internal actions. Sophia's function bodies (`Sophia.psm1`) were not read, so its exact keys are not listed here.

---

## Q3. Which tweaks are known to break things (Windows Update, Defender, Store, search) or are placebo on Windows 11 24H2/25H2?

### Takeaway
The breakages that are best documented come from **component removal**: AppX/App Installer/WebExperience packages, Edge WebView, and stripped images. Setting registry values is rarely the cause. Several "classic" tweaks are ignored or downgraded by modern Windows:
- `DisableAntiSpyware` has been ignored since 2020 because of Tamper Protection.
- `AllowTelemetry=0` is treated as 1 (Required) on Home and Pro.
- Timer and MMCSS/network "gaming" tweaks (HPET/useplatformclock, SystemResponsiveness, NetworkThrottlingIndex) have no controlled evidence of benefit.

### Cited Findings
- The `DisableAntiSpyware` registry value was discontinued and is ignored on client devices since Defender platform 4.18.2007.8 (August 2020). It is a legacy OEM/IT setting, and Defender already turns itself off when another antivirus registers. — [Microsoft Learn unattend DisableAntiSpyware](https://learn.microsoft.com/nb-no/windows-hardware/customize/desktop/unattend/security-malware-windows-defender-disableantispyware); [Winaero](https://winaero.com/microsoft-deprecates-the-disableantispyware-option-to-disable-defender)
- Microsoft removed the option partly because malware abused it to bypass Tamper Protection. BleepingComputer-reported tests found the value worked only for one session before Tamper Protection re-enabled Defender. — [TechRadar](https://techradar.com/news/microsoft-explains-why-it-wont-let-you-disable-this-annoying-windows-10-feature-any-more); [Wccftech](https://wccftech.com/microsoft-plans-removes-disableantispyware-to-support-tamper-protection/)
- AllowTelemetry values:
  - 0 = Diagnostic data off ("Security"); 1 = Required; 2 = Enhanced; 3 = Optional. 0 is honored only on Enterprise, Education and Server; Pro treats 0 as 1. This rests on secondary sources; I could not confirm the exact Microsoft Learn wording. — [cloudinfra.net](https://cloudinfra.net/configure-windows-diagnostic-data-using-intune/); [Computerworld](https://www.computerworld.com/article/1633303/microsoft-previews-new-enterprise-only-telemetry-option-for-windows-10.html)
  - CIS recommends at least Required if the organization relies on Windows Update. — [Tenable CIS audit item](https://www.tenable.com/audits/items/CIS_MS_InTune_for_Windows_11_Level_1_Next_Generation_Security_v2.0.0.audit:bf997f944eb311ce2f5c1fa7821a4807); [Policy CSP System](https://learn.microsoft.com/windows/client-management/mdm/policy-csp-system)
- Debloat breakage on 24H2:
  - A 24H2 test write-up says nearly every "debloat broke my PC" case traces back to a script removing core components, such as the App Installer package that provides winget. — [computingforgeeks](https://computingforgeeks.com/debloat-windows-11/)
  - An NTLite user reports that the Store would not open after component removal from a 24H2 image. — [NTLite forum](https://ntlite.com/community/threads/microsoft-store-not-working-on-windows-11-24h2-image-preset-attached.6183/latest)
  - A r/sysadmin thread discusses GPOs that can break the Store. — [r/sysadmin mirror](https://lr.eu.psf.lt/r/sysadmin/comments/1kbg15i/gpos_that_can_break_windows_store)
- Windows Search broke on many machines after 24H2 even without tweaks. A Microsoft Q&A advisor linked it to a change in the Windows Web Experience Pack, the same package WinUtil's "Widgets - Remove" uninstalls. — [Microsoft Q&A](https://learn.microsoft.com/en-us/answers/a/7915904)
- Removal tweaks and reset prompts in WinUtil itself:
  - "Widgets - Remove" uninstalls `MicrosoftWindows.Client.WebExperience`.
  - WinUtil says "Start Menu Previous Layout" (`FeatureManagement\Overrides\8\3036241548` EnabledState=1) does not work on newer Windows.
  - It advises re-enabling Reserved Storage before major feature updates.
  - Its "File Explorer Automatic Folder Discovery" tweak disables Explorer grouping and requires sign-out. — [WinUtil tweaks.json](https://raw.githubusercontent.com/ChrisTitusTech/winutil/main/config/tweaks.json)
- **Supported debloat alternative**: Windows 11 25H2 adds the policy "Remove Default Microsoft Store packages from the system" (Administrative Templates > Windows Components > App Package Deployment).
  - It writes subkeys named by package family under `HKLM\SOFTWARE\Policies\Microsoft\Windows\Appx\RemoveDefaultMicrosoftStorePackages`, covering about 25 apps (Xbox, Copilot, Teams, Clipchamp, Weather…), and prevents them from being restored.
  - Microsoft says it is for Enterprise/Education 25H2. Pureinfotech claims it also works on Pro, so sources conflict. — [Microsoft Tech Community](https://techcommunity.microsoft.com/blog/windows-itpro-blog/policy-based-removal-of-pre-installed-microsoft-store-apps/4463835); [PatchMyPC](https://patchmypc.com/blog/remove-default-microsoft-store-app-packages-windows11-25h2/); [Pureinfotech](https://pureinfotech.com/windows-11-25h2-debloat-option-remove-default-apps/)
- **HPET / `bcdedit /set useplatformclock` / `useplatformtick`**:
  - Effects are hardware-dependent and community tests are contradictory; one user called the results placebo, and another found `disabledynamictick` was the actual change.
  - Guides even disagree on which command enables HPET and which disables it.
  - One thread warns that toggling timers can make a system unbootable.
  - I found no controlled benchmarks. — [Blur Busters forum](https://forums.blurbusters.com/viewtopic.php?p=110514); [Neowin forum](https://www.neowin.net/forum/topic/1075781-tweak-enable-hpet-in-bios-and-os-for-better-performance-and-fps/page/4/); [Overclock.net](https://www.overclock.net/threads/win-10-1909-bcdedit-set-useplatformtick-yes-causes-input-lag.1742922/page-5)
- **O&O ShutUp10++ reversion and breakage**:
  - Windows updates can reset settings. The FAQ says to rerun the tool and review toggles shown in red. — [O&O FAQ](https://manuals.oo-software.com/ooshutup10/docs/faq)
  - A reader comment reports a Windows 10 install broken badly enough to need reinstalling. This is anecdotal. — [Chipp.in](https://chipp.in/software/windows/oo-shutup10-review-tame-windows-data-hunger/)

### Inferences
- **Disabling Windows Update or Defender**: Vapt should not offer these by default.
  - Only use supported policies: WindowsUpdate\AU AUOptions/NoAutoRebootWithLoggedOnUsers, deferral policies, and active hours.
  - Never disable the wuauserv, UsoSvc, WaaSMedicSvc or BITS services. WaaSMedicSvc is self-protected and restores them anyway. This is general knowledge, not verified in this session.
  - Defender: offer only exclusions, sample-submission consent and the scan schedule.
- **Search**: WSearch disable breaks Start, Settings and Outlook search speed and filtering.
- **Store**: avoid removing App Installer, Store, VCLibs/UI.Xaml frameworks, WebExperience, and Edge WebView2.
- **Xbox and Game Pass**: disabling the XblAuthManager/XboxGipSvc services breaks sign-in and controllers.
- **Likely placebo or obsolete on Windows 11** (label as such, or omit):
  - Disabling HomeGroup (removed since Windows 10 1803).
  - DcpSvc and xbgm (services that no longer exist).
  - Wi-Fi Sense keys.
  - DisableAntiSpyware.
  - AllowTelemetry=0 on Home/Pro (effectively 1).
  - SystemResponsiveness, NetworkThrottlingIndex, Psched NonBestEffortLimit=0 (the 20% QoS bandwidth reservation myth).
  - HPET/useplatformclock.
  - "Start Menu Previous Layout" FeatureManagement override.
  - SvcHostSplitThresholdInKB (cosmetic: it only reduces the process count).

### Gaps
- No authoritative benchmark was found for SystemResponsiveness, NetworkThrottlingIndex or Game Mode. No official Microsoft known-issue entry ties a specific registry tweak to broken Windows Update on 24H2/25H2. The Windows release health dashboard was not checked.

---

## Q4. How do the tools handle restore points, backups and undo?

### Takeaway
Three undo models exist:
1. Per-tweak recorded original values plus undo scripts (WinUtil).
2. Paired Enable/Disable (or "default") functions plus an upfront restore point (Sophia, Optimizer). Optimizer's README does not mention restore points.
3. Restore point offered before applying, plus config export/import (ShutUp10++, Winaero).

None of them snapshots full registry keys per change. Package removals are reversible only by reinstalling.

### Cited Findings
- **WinUtil**
  - Each registry entry stores `OriginalValue`; `<RemoveEntry>` means the value did not exist before. Services store their original startup type, and most script tweaks have an `UndoScript`.
  - "Restore Point - Create" is unchecked by default. It sets `HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\SystemRestore` SystemRestorePointCreationFrequency=0 (default 1440 minutes, which otherwise throttles creation to one per 24 h). It enables System Restore if needed and runs `Checkpoint-Computer -Description "System Restore Point created by WinUtil"`.
  - Widgets/AI removal, disk cleanup and temp deletion have no undo. — [WinUtil tweaks.json](https://raw.githubusercontent.com/ChrisTitusTech/winutil/main/config/tweaks.json)
- **Sophia**: the preset runs `InitialActions` (aborts if `$Global:Failed`), then `CreateRestorePoint` in its "Protection" region before any change. `RegistryBackup -Enable` turns on Windows' periodic registry backup. Every tweak has a restore-default counterpart. — [Sophia preset](https://raw.githubusercontent.com/farag2/Sophia-Script-for-Windows/master/src/Sophia_Script_for_Windows_11/Sophia.ps1); [Sophia README](https://github.com/farag2/Sophia-Script-for-Windows)
- **Optimizer**: Disable/Enable method pairs. The Enable side mostly deletes the value or sets Start=2, and sometimes does not revert at all (see Q1). — [OptimizeHelper.cs](https://raw.githubusercontent.com/hellzerg/optimizer/master/Optimizer/OptimizeHelper.cs)
- **O&O ShutUp10++**
  - Offers to create a restore point before applying changes. — [O&O FAQ](https://manuals.oo-software.com/ooshutup10/docs/faq)
  - Silent import: `OOSU10.exe <path>.cfg /quiet`. — [binarymagic WinUtil mirror commit](https://git.binarymagic.net/AIR/winutil/commit/de424ce636485cd3c4afdb7d4fef392eb4178e83); [ElevenForum](https://www.elevenforum.com/t/how-to-run-o-o-shutup-10-in-ntlite.36439/latest)
  - `/nosrp` suppresses restore-point creation. — [Neowin](https://www.neowin.net/news/oo-shutup10-1313581)
- **Winaero Tweaker**: export/import of changed tweaks (since 0.8), an automatic Registry backup option, and "Reset all Group Policy options at once". — [Winaero Tweaker](https://winaerotweaker.com/)

### Inferences
- Recommended Vapt design: a journal of changes.
  - Before every write, record the hive, path, value name, type and prior data, or "absent".
  - Record prior service start types (Start DWORD and DelayedAutostart), prior scheduled-task enabled state, and installed AppX package names.
  - Persist the journal as JSON and offer per-tweak and per-session undo.
  - Also create a restore point first: WMI `SystemRestore.CreateRestorePoint` or PowerShell `Checkpoint-Computer`, after temporarily setting SystemRestorePointCreationFrequency=0. Warn that System Protection is often off by default on the C: drive.
  - Mark irreversible actions explicitly: DISM /ResetBase, vssadmin delete, AppX removal, file deletion.
- WinUtil's tweaks.json schema (Path/Name/Type/Value/OriginalValue plus InvokeScript/UndoScript) is a good template for Vapt's data-driven tweak definitions.

### Gaps
- I did not confirm whether WinUtil's "Undo selected" uses OriginalValue for every entry type. The behaviour of ShutUp10++ "Undo all changes" and its .cfg format was not verified from primary documentation.

---

## Q5. Startup app management: which registry Run keys, StartupApproved keys, folders and scheduled tasks are used?

### Takeaway
Startup entries live in six Run/RunOnce keys (HKLM, HKCU and HKLM Wow6432Node) and two Startup folders. Task Manager's enabled/disabled state is stored separately in `Explorer\StartupApproved\*` as 12-byte binary values: the first byte 0x02 means enabled, 0x03 means disabled (with a timestamp in the remaining bytes). Optimizer deletes entries rather than toggling StartupApproved, and does not cover scheduled tasks.

### Cited Findings
- Optimizer enumerates:
  - Registry: `HKLM` and `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` and `\RunOnce`; `HKLM\Software\Wow6432Node\Microsoft\Windows\CurrentVersion\Run` and `\RunOnce`.
  - Folders: `%ProgramData%\Microsoft\Windows\Start Menu\Programs\Startup` and `%AppData%\Microsoft\Windows\Start Menu\Programs\Startup`.
  - No scheduled tasks; StartupApproval is handled in a separate class that was not read. — [StartupHelper.cs](https://raw.githubusercontent.com/hellzerg/optimizer/master/Optimizer/StartupHelper.cs)
- StartupApproved behaviour:
  - Task Manager does not delete the Run value when you disable an entry. It writes the state to a StartupApproved key in the same hive, so Autoruns may not show Task-Manager-disabled entries.
  - The data is 12 bytes; 0x02 followed by zeros means enabled, and 0x03 plus extra bytes (probably a timestamp) means disabled. The 0x03 detail rests on informal sources, and the format is undocumented by Microsoft.
  - Deleting a Run value with RegEdit leaves an orphaned StartupApproved\Run entry. — [Windows IR blog (StartupApproved pt II)](https://windowsir.blogspot.com/2022/07/startupapprovedrun-pt-ii.html); [Windows IR blog](https://windowsir.blogspot.com/2022/07/does-autostart-really-mean-autostart.html); [TweakTown](https://www.tweaktown.com/guides/11602/i-stopped-trusting-the-startup-apps-tab-after-i-opened-this-utility-on-my-pc/index.html); [winhelponline](https://www.winhelponline.com/blog/task-manager-startup-tab-entries-remove-invalid/); [TenForums tutorial](https://www.tenforums.com/tutorials/2944-startup-items-add-delete-enable-disable-windows-10-a.html)

### Inferences
These paths come from prior knowledge and the winhelponline and TenForums pages listed above. Verify them on a 25H2 machine.
- StartupApproved paths:
  - `HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run`, `\Run32` (for Wow6432Node Run entries) and `\StartupFolder`.
  - The same three subkeys under HKLM for machine-wide entries.
- Recommended Vapt approach: **toggle** an entry by writing `03 00 00 00` plus an 8-byte FILETIME to disable it, or `02 00 00 00 00 00 00 00 00 00 00 00` to enable it, the same way Task Manager does. Delete only on explicit request, and back up the Run value data before any delete.
- Also list:
  - Scheduled tasks with logon/boot triggers (`Get-ScheduledTask | ? { $_.Triggers.CimClass.CimClassName -match 'Logon|Boot' }`), toggled with `Disable-ScheduledTask`/`Enable-ScheduledTask`.
  - Packaged (MSIX) startup tasks, which Task Manager shows. Their state lives under `HKCU\Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\SystemAppData\<PackageFamily>\<TaskId>` State. This is not verified in this session.
  - Optionally, auto-start services.

### Gaps
- The exact StartupApproved path layout (Run32, StartupFolder) and the packaged-app StartupTask state key were not confirmed from a primary or Microsoft source in this session. Microsoft does not officially document the StartupApproved binary format. The telemetry scheduled-task list was not extracted from Optimizer's `.bat` resources; commonly cited tasks such as `\Microsoft\Windows\Application Experience\Microsoft Compatibility Appraiser`, `\Customer Experience Improvement Program\Consolidator` and `UsbCeip`, and `\Autochk\Proxy` remain unverified here.
