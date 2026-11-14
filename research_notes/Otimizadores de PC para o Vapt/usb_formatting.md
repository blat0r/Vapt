# USB Flash Drive Formatting and Bootable-Drive Tools: Implementing a Rufus-like Formatter in a Windows 11 Electron/Node App

Scope: Rufus, Ventoy, balenaEtcher (etcher-sdk), Windows Media Creation Tool, and the built-in Windows tools (diskpart, the Storage PowerShell module, format.com). Researched November 2026. All code blocks are syntheses of the cited docs, not copied verbatim from any source. Treat them as starting points and test them on throwaway drives before shipping.

---

## Q1. Rufus UI options and how each one is implemented

### Takeaway
Rufus's `FormatThread()` runs a fixed pipeline: lock the physical drive, remove drive letters, delete partitions (through VDS), zero the MBR/GPT areas, run the optional bad-block test, create the partitions, format (FormatEx in fmifs.dll, VDS, Rufus's own large-FAT32 code, or its own ext2/3/4 code), write the MBR/PBR boot records, then copy files or write a DD image. Most UI options map onto one step of that pipeline.

### Cited Findings
**Pipeline (src/format.c)**
- `FormatThread()` steps: `GetPhysicalHandle()` (with a lock) → `GetDriveLetters()` / `RemoveDriveLetters()` → `DeletePartition()` via VDS → `ClearMBRGPT()` + `InitializeDisk()` (if that fails, `CycleDevice()` and one retry) → optional `BadBlocks()` → `CreatePartition()` → `FormatPartition()` → `WriteMBR()` / `WriteSBR()` / `WritePBR()` → remount, extract files or install the bootloader, and optionally `CheckDisk()` for NTFS — [Rufus format.c](https://raw.githubusercontent.com/pbatard/rufus/master/src/format.c)
- Formatting back-ends:
  - `FormatNative()` calls `FormatEx` from `fmifs.dll`, gets progress through `FormatExCallback()`, and retries up to `WRITE_RETRIES` times.
  - `FormatNativeVds()` uses the Virtual Disk Service COM API (`CLSID_VdsLoader` → `IVdsVolumeMF3::FormatEx2`, then polls `IVdsAsync::QueryStatus`).
  - FAT32 goes to `FormatLargeFAT32()` when the disk is larger than `LARGE_FAT32_SIZE` or a force flag is set.
  - ext2/3/4 goes to `FormatExtFs()`.
  - Source: [Rufus format.c](https://raw.githubusercontent.com/pbatard/rufus/master/src/format.c)
- `ClearMBRGPT()` writes zeros over `MAX_SECTORS_TO_CLEAR` sectors at the start of the disk and over `MAX_SECTORS_TO_CLEAR/8` sectors at the end, which is where the backup GPT lives — [Rufus format.c](https://raw.githubusercontent.com/pbatard/rufus/master/src/format.c)
- `WriteMBR()` patches the partition type byte at 0x1C2 (0x0E for FAT16 LBA, 0x0C for FAT32 LBA) and sets the bootable flag at 0x1BE (0x80/0x81). It can write Syslinux, GRUB2, GRUB4DOS, ReactOS, KolibriOS, Windows/Rufus or zeroed MBRs, and it writes a protective MBR for GPT. `WritePBR()` writes FAT16/FAT32/NTFS boot records — [Rufus format.c](https://raw.githubusercontent.com/pbatard/rufus/master/src/format.c)

**Device and locking**
- Locking goes through `GetPhysicalHandle(..., lock, ...)` and `GetLogicalHandle()` plus `UnmountVolume()`.
- On Windows 11 or later, and whenever VDS is used, Rufus unlocks the physical drive before formatting. The code comment says these "require us to unlock the physical drive to format the drive."
- Locking is disabled for ESP, persistence and ext partitions.
- Source: [Rufus format.c](https://raw.githubusercontent.com/pbatard/rufus/master/src/format.c)

**Partition scheme / target system**
- MBR with "BIOS or UEFI-CSM" gives a drive that boots in BIOS mode. GPT gives a UEFI-only drive. The FAQ's example: "If you select _GPT_ then the USB will be bootable in UEFI mode only." — [Rufus FAQ](https://github.com/pbatard/rufus/wiki/FAQ)

**File system and cluster size**
- `FormatPartition()` rejects a non-zero allocation unit that is not a power of two.
- Combo-box values below 0x200, and ESP targets, become 0 (the default). UDF always uses 0.
- Source: [Rufus format.c](https://raw.githubusercontent.com/pbatard/rufus/master/src/format.c)

**Volume label**
- FAT labels are limited to 11 uppercase characters. Rufus creates an `autorun.inf` so that Windows can show a longer, mixed-case label — [Rufus FAQ](https://github.com/pbatard/rufus/wiki/FAQ)

**Quick format**
- Quick format is the `FP_QUICK` flag. In PowerShell the equivalent is leaving out `-Full` on `Format-Volume`; `-Full` "writes to every sector of the disk" and takes much longer — [Rufus format.c](https://raw.githubusercontent.com/pbatard/rufus/master/src/format.c); [Format-Volume docs](https://learn.microsoft.com/en-us/powershell/module/storage/format-volume)

**Check device for bad blocks**
- `BadBlocks()` runs with the number of passes the user chose and writes `rufus_<timestamp>.log` to `%USERPROFILE%`. The log is deleted if no bad blocks are found; otherwise Rufus offers Abort/Retry/Ignore.
- After the test, `ClearMBRGPT()` runs again, because a destructive test must be followed by zeroing.
- Source: [Rufus format.c](https://raw.githubusercontent.com/pbatard/rufus/master/src/format.c)
- The FAQ: "bad blocks check is disabled by default because this is a very slow process." Repeated checks are the only realistic way Rufus could wear out flash media — [Rufus FAQ](https://github.com/pbatard/rufus/wiki/FAQ)

**Persistence (Linux live media)**
- Rufus creates an extra ext partition labelled `casper-rw` (Ubuntu/casper) or `persistence` (Debian-style). For non-casper images it adds a `persistence.conf` (flag `FP_CREATE_PERSISTENCE_CONF`). Windows cannot format or mount these partitions, so Rufus formats them with its own ext code — [Rufus format.c](https://raw.githubusercontent.com/pbatard/rufus/master/src/format.c)
- Persistence and extra content are not possible in DD mode — [Rufus FAQ](https://github.com/pbatard/rufus/wiki/FAQ)

**Windows User Experience dialog (Windows 11 ISOs)**
- The dialog appears after you click START with a Windows 11 ISO. Its options:
  - Remove the requirement for 4GB+ RAM, Secure Boot and TPM 2.0
  - Remove the requirement for an online Microsoft account
  - Create a local account with a chosen username
  - Set regional options to the same values as this user's
  - Disable data collection (skip privacy questions)
  - Disable BitLocker automatic device encryption
  - Source: [Neowin, Rufus 3.20](https://www.neowin.net/news/rufus-320-is-out-with-bypass-for-microsoft-account-and-more-for-windows-11/); [Neowin, regional settings / local account](https://www.neowin.net/news/the-latest-rufus-update-lets-you-copy-regional-settings-and-create-a-local-windows-account/); [Pureinfotech guide](https://pureinfotech.com/rufus-create-bootable-windows-11-usb)
- The FAQ describes the dialog as "designed exclusively to remove annoyances that come in the way of a streamlined Windows installation" — [Rufus FAQ](https://github.com/pbatard/rufus/wiki/FAQ)
- Newer Rufus versions (4.10+) add "Use 'Windows CA 2023' signed bootloaders" for 25H2 media, and a "QoL" option that removes OneDrive/Outlook/Teams/Copilot and disables Fast Startup — [Rufus FAQ](https://github.com/pbatard/rufus/wiki/FAQ)
- The bypasses are applied to the installation media. They do not help in-place upgrades on unsupported hardware, and they do not fix missing CPU features such as POPCNT/SSE4.2 that 24H2 requires — [Pureinfotech](https://pureinfotech.com/rufus-create-bootable-windows-11-usb)

**Why Rufus needs admin**
- Formatting and altering boot records is a privileged operation. The FAQ says a non-administrator build of Rufus is not possible — [Rufus FAQ](https://github.com/pbatard/rufus/wiki/FAQ)

**Checksums**
- The ✓ button computes MD5, SHA-1 and SHA-256 in parallel — [Rufus FAQ](https://github.com/pbatard/rufus/wiki/FAQ)

**Ventoy (alternative model)**
- Ventoy is installed once; after that you just copy ISO/WIM/IMG/VHD(X) files to the first partition, and it scans folders recursively for its boot menu.
- The first partition is exFAT by default and can be reformatted to NTFS, FAT32, UDF, XFS or ext2/3/4.
- The Windows installer shows only USB drives by default. MBR is the default (GPT with `-g` on Linux), and Secure Boot support is opt-in (`-s`).
- Source: [Ventoy docs](https://www.ventoy.net/en/doc_start.html)

### Inferences
- How Rufus implements the Windows User Experience options is not stated in the sources I read. From general knowledge (unverified here), Rufus writes an `unattend.xml` and, for the TPM/Secure Boot/RAM bypass, adds the `LabConfig` registry values (`BypassTPMCheck`, `BypassSecureBootCheck`, `BypassRAMCheck`) during the windowsPE pass. A clean-room implementation can do the same with an `autounattend.xml` at the root of the USB drive. Verify this against Rufus's `wue.c` before relying on it.
- A UI option to put in the app: "List USB hard drives" (Rufus has a similar toggle). Keep it off by default so external SSDs/HDDs stay hidden.

### Gaps
- I did not fetch `src/wue.c` or the large-FAT32 implementation, so the exact unattend XML content and the value of `LARGE_FAT32_SIZE` are unverified. From memory it is 32 GB, which matches the FAQ text "only used for drives that are larger than 32 GB."
- I found no source on how the Windows Media Creation Tool works internally. As commonly understood (unverified), it downloads ESD files and builds a FAT32 drive, with install.esd under 4 GB.

---

## Q2. Safely enumerating only removable USB drives

### Takeaway
Use the Storage module (`Get-Disk`, the MSFT_Disk class). Filter on `BusType -eq 'USB'` (7), and optionally SD (12) or MMC (13). Hard-exclude disks where `IsBoot`, `IsSystem` or `IsReadOnly` is true. Key every later operation on `UniqueId`/`SerialNumber` plus `Size`, not on the disk number alone, because disk numbers can change.

### Cited Findings
- MSFT_Disk `BusType` values: 7 = USB, 12 = SD, 13 = MMC, 17 = NVMe, 11 = SATA, 15 = File Backed Virtual (VHD), 14 = Virtual — [MSFT_Disk](https://learn.microsoft.com/en-us/windows-hardware/drivers/storage/msft-disk)
- `IsBoot` is TRUE if the disk contains the boot partition, and `IsSystem` is TRUE if it contains the system partition. `BootFromDisk` is TRUE for the disk the firmware boots from (the disk with the ESP on UEFI) — [MSFT_Disk](https://learn.microsoft.com/en-us/windows-hardware/drivers/storage/msft-disk)
- "Disk numbers may not necessarily remain the same across restarts." `UniqueId` holds VPD page 0x83 identity data. `Path` can be used to open an OS handle to the disk — [MSFT_Disk](https://learn.microsoft.com/en-us/windows-hardware/drivers/storage/msft-disk)
- `PartitionStyle`: 0 = RAW/Unknown, 1 = MBR, 2 = GPT — [MSFT_Disk](https://learn.microsoft.com/en-us/windows-hardware/drivers/storage/msft-disk)
- Ventoy's Windows installer lists only USB drives by default and puts others behind "Show all devices" — [Ventoy docs](https://www.ventoy.net/en/doc_start.html)

### Recommended enumeration (synthesised from the docs above)
```powershell
# Run elevated; output JSON for Node
Get-Disk | Where-Object {
    $_.BusType -in @('USB','SD','MMC') -and
    -not $_.IsBoot -and -not $_.IsSystem -and -not $_.IsReadOnly -and
    $_.Size -gt 0
} | ForEach-Object {
    $d = $_
    [pscustomobject]@{
        Number = $d.Number; UniqueId = $d.UniqueId; Serial = $d.SerialNumber
        FriendlyName = $d.FriendlyName; Size = $d.Size; BusType = "$($d.BusType)"
        PartitionStyle = "$($d.PartitionStyle)"; Path = $d.Path
        Letters = @(Get-Partition -DiskNumber $d.Number -ErrorAction SilentlyContinue |
                    Where-Object DriveLetter | ForEach-Object { "$($_.DriveLetter):" })
    }
} | ConvertTo-Json -Depth 3
```
- Cross-check: `Get-CimInstance Win32_DiskDrive | Where-Object InterfaceType -eq 'USB'` (MediaType "Removable Media" or "External hard disk media"). The `Index` property equals the disk number. This is general WMI knowledge; I did not fetch a source for it in this session.

### Inferences
- Also exclude any disk that holds the page file, a hibernation file, a BitLocker-locked volume, the app's own install path, or the drive the selected ISO is on. Rufus refuses to target the drive the source image lives on; that is a sensible extra guard.
- Exclude `BusType` 14/15 (virtual/VHD) unless the user opts in.
- A USB-attached SSD also reports `BusType` USB, so add a size warning, for example above 128 GB or 256 GB, plus a confirmation that shows the friendly name and serial.

### Gaps
- None material.

---

## Q3. Exact PowerShell / diskpart sequences: clean, partition (MBR/GPT), format FAT32/exFAT/NTFS with label and cluster size

### Takeaway
`Clear-Disk -RemoveData -RemoveOEM` → `Initialize-Disk -PartitionStyle MBR|GPT` → `New-Partition -UseMaximumSize` (`-IsActive` for BIOS-bootable MBR) → `Format-Volume -FileSystem … -NewFileSystemLabel … -AllocationUnitSize …`. `Format-Volume` accepts FAT, FAT32, exFAT, NTFS and ReFS, not UDF or ext. On a stable Windows 11 release, the built-in tools refuse FAT32 on volumes above 32 GB. Insider builds from April 2026 raise the command-line limit to 2 TB.

### Cited Findings
- `Clear-Disk` "removes partition and volume information". It needs `-RemoveData` if the disk has data volumes, and it does not remove OEM recovery partitions unless `-RemoveOEM` is given. It supports `-UniqueId`, `-Confirm:$false` and `-PassThru` — [Clear-Disk](https://learn.microsoft.com/en-us/powershell/module/storage/clear-disk)
- `Format-Volume`:
  - `-FileSystem` accepts FAT, FAT32, exFAT, NTFS, ReFS.
  - `-AllocationUnitSize` (alias `ClusterSize`), `-NewFileSystemLabel`, `-Full` (writes every sector), `-Force`.
  - It can take a partition from the pipeline (`-Partition`).
  - Note: "The WhatIf switch does not work with this cmdlet."
  - Source: [Format-Volume](https://learn.microsoft.com/en-us/powershell/module/storage/format-volume)
- Microsoft's manual installer-USB steps: format FAT32 "to be able to boot either BIOS-based or UEFI-based PCs", then mark the partition active, using diskpart if Disk Management won't — [MS Learn: Install Windows from a flash drive](https://learn.microsoft.com/en-us/windows-hardware/manufacture/desktop/install-windows-from-a-usb-flash-drive)
- FAT32 32 GB limit:
  - Windows' format tools have historically capped FAT32 at 32 GB, although the on-disk format supports about 2 TB with 512-byte sectors.
  - Windows 11 Insider Dev build 26300.8170 and Beta build 26220.8165 (10 April 2026) raise the command-line `format` limit to 2 TB. The Explorer format dialog stays capped at 32 GB, and the 4 GiB per-file limit is unchanged.
  - Sources: [Neowin](https://www.neowin.net/news/microsoft-brings-windows-11-fat32-2tb-support-fixes-network-data-bug-with-new-builds/); [TechSpot](https://www.techspot.com/news/112058-microsoft-removing-32gb-size-limit-fat32-volumes-time.html); [Windows Latest](https://windowslatest.com/2026/04/13/tested-windows-11-just-fixed-slow-storage-management-and-removed-a-30-year-fat32-limit/)
  - These are secondary sources; I did not read the Insider blog itself. One 2024 report ([TechPowerUp](https://techpowerup.com/325802/microsoft-updates-the-30-year-old-fat32-file-system-with-2tb-volume-size-limits)) placed the change in a Canary build in August 2024.
- How Rufus works around the limit: for FAT32 on drives larger than 32 GB it uses its own "Large FAT32" formatter instead of the Microsoft one. The Alt-L cheat forces it for all sizes — [Rufus FAQ](https://github.com/pbatard/rufus/wiki/FAQ); [Rufus format.c](https://raw.githubusercontent.com/pbatard/rufus/master/src/format.c)

### Recommended sequences (synthesised from the docs above)
```powershell
# Parameters supplied by the app after the user confirms; $UniqueId comes from enumeration
$ErrorActionPreference = 'Stop'
$d = Get-Disk -UniqueId $UniqueId
if ($d.IsBoot -or $d.IsSystem -or $d.BusType -notin 'USB','SD','MMC' -or $d.Size -ne $ExpectedSize) { throw 'Safety check failed' }

# 1. Wipe (a RAW disk throws here; catch and continue)
if ($d.PartitionStyle -ne 'RAW') { $d | Clear-Disk -RemoveData -RemoveOEM -Confirm:$false }

# 2. Initialize: 'MBR' for BIOS/CSM or BIOS+UEFI, 'GPT' for UEFI-only
Initialize-Disk -UniqueId $UniqueId -PartitionStyle MBR   # or GPT

# 3. Partition (MBR: -IsActive makes it BIOS-bootable; on GPT use -GptType basic data)
$p = New-Partition -DiskId $d.UniqueId -UseMaximumSize -AssignDriveLetter -IsActive   # omit -IsActive for GPT
# GPT ESP-like FAT32 data partition: -GptType '{ebd0a0a2-b9e5-4433-87c0-68b6b72699c7}' (basic data)

# 4. Format
$p | Format-Volume -FileSystem FAT32 -NewFileSystemLabel 'VAPTUSB' -AllocationUnitSize 32768 -Force -Confirm:$false
#   exFAT: -FileSystem exFAT ; NTFS: -FileSystem NTFS -AllocationUnitSize 4096 ; add -Full for full format
```
Equivalent diskpart script (`diskpart /s script.txt`), useful as a fallback when the Storage WMI provider misbehaves:
```
select disk N
attributes disk clear readonly
clean
convert mbr          (or: convert gpt)
create partition primary
active               (MBR/BIOS only)
format fs=fat32 label="VAPTUSB" unit=32K quick
assign
```
- `format.com` equivalent for an existing volume: `format E: /FS:FAT32 /V:VAPTUSB /A:32K /Q /Y`. Per the 2026 Insider reports, this is the path that gains FAT32 up to 2 TB.

### Inferences
- Use `-DiskId`/`-UniqueId` rather than `-DiskNumber` in every step, and re-validate immediately before `Clear-Disk`.
- After `Clear-Disk`, Windows sometimes needs `Update-HostStorageCache` or a short retry loop before `Initialize-Disk` succeeds. Rufus has a similar retry (`CycleDevice()` plus a retry) — [Rufus format.c](https://raw.githubusercontent.com/pbatard/rufus/master/src/format.c)
- FAT32 above 32 GB on stable Windows 11 has three options:
  - (a) Implement your own FAT32 formatter that writes the boot sector, FSInfo and FATs directly to the volume handle. This is what fat32format/Rufus do. It is a modest amount of code, but write it clean-room, or use a permissively licensed implementation, to avoid the GPL.
  - (b) Create a ≤32 GB FAT32 partition, and optionally a second exFAT/NTFS partition for the rest.
  - (c) Require a build with the 2 TB `format` change and detect it by trying it.
- Cluster size defaults: let Windows choose (pass nothing) unless the user picks a value. Offer only powers of two from 512 B up, as Rufus does.

### Gaps
- `-WhatIf` cannot dry-run `Format-Volume` (documented). No equivalent dry-run exists for the whole pipeline, so build your own "plan preview."
- I did not verify that `Initialize-Disk` accepts `-UniqueId` on every Windows 11 build (the docs list `-UniqueId`, `-Number`, `-FriendlyName`, `-Path`, `-InputObject`). Piping the disk object is the safest form.

---

## Q4. Making a bootable Windows installer USB without Rufus binaries

### Takeaway
There are two proven layouts. (1) Microsoft's documented route: a single FAT32 partition, an MBR marked active for BIOS + UEFI or GPT for UEFI-only, all ISO files copied, and `install.wim` split into `.swm` files under 4 GB with `Dism /Split-Image`. (2) NTFS + UEFI:NTFS: a large NTFS partition plus a small FAT partition at the end holding the Microsoft-signed UEFI:NTFS bootloader (GPLv2), which chain-loads `\efi\boot\bootx64.efi` from the NTFS partition.

### Cited Findings
- Microsoft's FAT32 procedure: format FAT32, mark the partition active, then copy the ISO contents. If `install.wim` is over 4 GB:
  - `robocopy D: E: /s /max:3800000000` (copies everything except the large WIM)
  - `Dism /Split-Image /ImageFile:D:\sources\install.wim /SWMFile:E:\sources\install.swm /FileSize:3800`
  - Setup "automatically installs from this file, so long as you name it install.swm."
  - Source: [MS Learn](https://learn.microsoft.com/en-us/windows-hardware/manufacture/desktop/install-windows-from-a-usb-flash-drive)
- UEFI:NTFS:
  - Rufus creates a large NTFS partition with the Windows files and a small FAT partition at the end with an NTFS UEFI driver and the UEFI:NTFS loader. The loader runs `/efi/boot/boot<arch>.efi` from the NTFS partition.
  - The project ships `uefi-ntfs.img`, a FAT image you can write to a same-sized partition at the end of the drive.
  - License: GPL-2.0. It is signed by Microsoft for Secure Boot on x64, ia32 and ARM64 (ARM32 is unsigned).
  - Only the GPLv2 ntfs-3g driver can be used under Secure Boot; GPLv3 EfiFs drivers are excluded by Microsoft's signing policy.
  - Source: [pbatard/uefi-ntfs](https://github.com/pbatard/uefi-ntfs)
- Rufus 3.17+ ships Secure Boot-signed UEFI:NTFS. Some PCs need the "3rd party UEFI CA" enabled in firmware to boot it — [Rufus FAQ](https://github.com/pbatard/rufus/wiki/FAQ)
- For Windows 11 25H2 media with the newer boot certificate, Rufus offers "Use 'Windows CA 2023' signed bootloaders" (Rufus 4.10+) — [Rufus FAQ](https://github.com/pbatard/rufus/wiki/FAQ)

### Recommended pure-Windows flow (FAT32 route)
```powershell
$iso = Mount-DiskImage -ImagePath $IsoPath -PassThru
$src = ($iso | Get-Volume).DriveLetter + ':'
# ... wipe/partition/format USB as FAT32 (≤32 GB partition on stable builds), letter $dst ...
robocopy "$src\" "$dst\" /E /XF install.wim /R:1 /W:1 /NJH /NP   # robocopy exit codes 0–7 = success
$wim = "$src\sources\install.wim"
if ((Get-Item $wim).Length -gt 4GB - 1) {
  dism /Split-Image /ImageFile:$wim /SWMFile:"$dst\sources\install.swm" /FileSize:3800
} else { Copy-Item $wim "$dst\sources\" }
# Optional: copy autounattend.xml to $dst\ for TPM/online-account bypass (see Q1 inference)
Dismount-DiskImage -ImagePath $IsoPath
```
- Some ISOs ship `install.esd` instead of `install.wim`. ESD is usually under 4 GB; for large ESDs use the same Split-Image approach, or convert with `dism /Export-Image`.

### Inferences
- The FAT32 + split-WIM route needs no third-party binaries and no GPL code, and it boots on both BIOS (MBR plus active partition, with the bootmgr PBR that `format` writes) and UEFI (`\efi\boot\bootx64.efi`).
  - Caveat 1: on drives over 32 GB you need a ≤32 GB partition, or the 2026 `format` change, or a custom FAT32 formatter.
  - Caveat 2: `bootsect.exe /nt60 X: /mbr` from the ISO's `\boot` folder can be used to write BIOS boot code if the PBR/MBR lacks it.
- The NTFS + UEFI:NTFS route requires bundling `uefi-ntfs.img` (GPLv2). Distributing it alongside a proprietary app is "mere aggregation" if it is shipped as a separate file and written as data, but you must give the source offer and license text for it (see Q7).

### Gaps
- I did not verify whether the Media Creation Tool currently produces FAT32 with ESD for every edition. It is believed to, but unconfirmed in this session.

---

## Q5. Writing a raw .img/.iso (DD mode) from Node.js safely; ISO mode vs DD mode

### Takeaway
In DD mode the image is copied sector by sector to `\\.\PhysicalDriveN`. Before writing, every volume on that disk must be locked (`FSCTL_LOCK_VOLUME`) and dismounted (`FSCTL_DISMOUNT_VOLUME`), or Windows blocks writes into mounted file-system areas. Writes must be sector-aligned. From Node, the practical routes are etcher-sdk (Apache-2.0, used by balenaEtcher) or a small native/elevated helper.

### Cited Findings
- `FSCTL_LOCK_VOLUME` needs a volume handle (`\\.\X:`) opened with `FILE_SHARE_READ|FILE_SHARE_WRITE`.
  - It fails if any file on the volume is open, and on system or page-file volumes.
  - The lock lasts until `FSCTL_UNLOCK_VOLUME` or until the handle closes.
  - The system flushes cached data before locking.
  - `FSCTL_DISMOUNT_VOLUME` "does not check for open files", and "without a successful lock operation, a dismounted volume may be remounted by any process at any time."
  - Source: [FSCTL_LOCK_VOLUME](https://learn.microsoft.com/en-us/windows/win32/api/winioctl/ni-winioctl-fsctl_lock_volume)
- Since Vista, writes through a disk handle to sectors inside a mounted file system are blocked unless the volume is locked, or dismounted via a lock/dismount request. Sectors outside any file system (for example the MBR) are not restricted — [KB942448 mirror](https://mskb.pkisolutions.com/kb/942448); [Restricted direct disk access (MS)](https://msdn.microsoft.com/ko-kr/library/windows/hardware/dn653576)
- Rufus's `WriteDrive()`:
  - Uses double-buffered async reads (`NUM_BUFFERS`=2) and synchronous `WriteFile` with retries (`WRITE_RETRIES`).
  - Decompresses compressed images on the fly (bled) and buffers partial sectors in `sector_write()`.
  - Mounts VHD/VHDX and applies FFU images through `dism.exe`.
  - When zeroing, it can "fast-zero" by reading first and skipping blocks that are already blank.
  - Source: [Rufus format.c](https://raw.githubusercontent.com/pbatard/rufus/master/src/format.c)
- ISO mode vs DD mode:
  - Rufus recommends ISO mode for ISOHybrid images.
  - DD mode cannot add extra content or persistence, and sometimes makes Windows unable to use the drive's full capacity.
  - With GPT, a DD write leaves the backup GPT table misplaced at the image's end rather than the disk's end, so strict UEFI firmware may refuse to boot.
  - Source: [Rufus FAQ](https://github.com/pbatard/rufus/wiki/FAQ)
- etcher-sdk is Apache-2.0. Its README states that writing to block devices requires elevated permissions; Etcher launches a privileged helper process for this — [etcher-sdk](https://github.com/balena-io-modules/etcher-sdk)

### Recommended Node.js design
1. The main Electron process stays unelevated. A separate elevated helper does the work: a PowerShell script or small exe launched with `Start-Process -Verb RunAs`, or the whole app ships with a `requireAdministrator` manifest. The helper talks to the UI over a named pipe or stdout JSON lines.
2. The helper re-validates the target (Q2 checks plus UniqueId, serial and size match).
3. For each volume on the disk: open `\\.\X:`, `FSCTL_LOCK_VOLUME` (retry roughly 5 times with backoff), then `FSCTL_DISMOUNT_VOLUME`, and keep those handles open until the write is done. Optionally remove the mount points (`Remove-PartitionAccessPath`) so Explorer doesn't grab the volume.
4. Open `\\.\PhysicalDriveN` with `GENERIC_READ|GENERIC_WRITE`, `FILE_SHARE_READ|FILE_SHARE_WRITE`, and `FILE_FLAG_NO_BUFFERING|FILE_FLAG_WRITE_THROUGH`.
5. Write in aligned chunks of 1–4 MiB that are multiples of `LogicalSectorSize`. Pad the last chunk to a sector boundary.
6. `FlushFileBuffers`, then report progress as bytes written / image size, with speed and ETA.
7. Verify by reading back and hashing (SHA-256 or xxhash of the written range) and comparing with the source hash.
8. Close handles. Optionally zero the last ~1 MiB first so stale backup GPTs don't confuse Windows. Then `Update-Disk` / `Update-HostStorageCache` to refresh.

- Node specifics:
  - `fs.open('\\\\.\\PhysicalDrive3', 'r+')` works from an elevated process, but Node's fs cannot issue `DeviceIoControl` for the lock and dismount.
  - Options: etcher-sdk / `@balena/mountutils` / `drivelist` (native modules), a small C# or Rust helper exe, or koffi/ffi-napi bindings to `CreateFileW` + `DeviceIoControl`.
  - Unaligned buffers fail with `FILE_FLAG_NO_BUFFERING`. Node `Buffer`s are not guaranteed to be sector-aligned in memory, which is another reason to use a native helper.

### Inferences
- Detecting ISOHybrid: check for a 0x55AA MBR signature at offset 510 of the ISO plus a partition table, or for "EFI PART" at LBA 1. Windows ISOs are not hybrid, so they must use ISO/extract mode. Default to extract mode and offer DD as a fallback, as Rufus does.
- Never write past the end of the target. Refuse if the image size is larger than the disk size.

### Gaps
- The current Microsoft page on restricted direct disk access returned 404 at its new URL. I relied on the KB942448 mirror and the archived MSDN page.
- I could not fetch etcher-sdk's API docs (`doc/` folder), so its class names (`sourceDestination.BlockDevice`, `multiWrite.decompressThenFlash`, verification options) are from memory and unverified.

---

## Q6. Verification, bad-block checking, progress reporting, admin, guardrails

### Takeaway
Verify by read-back hashing. Bad-block testing means a destructive write/read pattern test (Rufus ports e2fsprogs' `badblocks`, with 1–4 passes). Run everything from an elevated helper. Guardrails must re-check identity right before the destructive step.

### Cited Findings
- Rufus bad blocks: user-selected passes, log file, Abort/Retry/Ignore on errors, and a re-zero afterward. It is off by default because it is slow and wears the flash — [Rufus format.c](https://raw.githubusercontent.com/pbatard/rufus/master/src/format.c); [Rufus FAQ](https://github.com/pbatard/rufus/wiki/FAQ)
- `Format-Volume -Full` writes every sector, a weaker form of surface check — [Format-Volume](https://learn.microsoft.com/en-us/powershell/module/storage/format-volume)
- Rufus's format progress comes from the FormatEx callback (fmifs) or VDS async status polling — [Rufus format.c](https://raw.githubusercontent.com/pbatard/rufus/master/src/format.c)
- Admin is required for formatting and boot-record writes, and etcher-sdk requires elevation for block-device writes — [Rufus FAQ](https://github.com/pbatard/rufus/wiki/FAQ); [etcher-sdk](https://github.com/balena-io-modules/etcher-sdk)

### Recommended guardrails (synthesised)
- Allow-list only `BusType` USB/SD/MMC. Never allow `IsBoot`, `IsSystem` or `BootFromDisk`, or a disk holding the page file or the source ISO.
- Re-validate UniqueId + SerialNumber + Size inside the elevated helper immediately before `Clear-Disk` or opening PhysicalDrive. Abort if the disk count changed (hot-plug) during the confirmation dialog.
- Confirmation UI: show the friendly name, size, current volume labels and letters, and require typing the drive letter or label for drives over a size threshold.
- Use `-Confirm:$false` only in the helper, never in a generic command runner. Don't expose an IPC endpoint that accepts arbitrary disk numbers without the same checks, which avoids a confused-deputy bug.
- Progress: for PowerShell steps report per stage (wipe → partition → format → copy → split WIM → verify). For file copy, report bytes copied out of the total size. For DD, report bytes written. For a quick format there is no meaningful percentage, so use an indeterminate bar.
- Logging: write a per-operation log with disk identity before and after.

### Inferences
- A non-destructive bad-block test (read-only) is safe on drives that hold data. A destructive 4-pattern test (0xAA, 0x55, 0xFF, 0x00, as in e2fsprogs) takes about 4× (write + read) of capacity. Detecting fake-capacity drives is better done with an H2testw/f3-style test: write position-tagged blocks across the whole disk, then read them back.

### Gaps
- I did not fetch `badblocks.c` from Rufus to confirm its exact patterns.

---

## Q7. Licensing: Rufus GPLv3, UEFI:NTFS GPLv2, Ventoy, etcher-sdk

### Takeaway
You can't copy Rufus source into a closed-source app without GPL-licensing the combined work. Shipping the unmodified Rufus.exe next to your app and invoking it as a separate program counts as "mere aggregation", which the GPL allows, provided you meet the GPL obligations for that binary (corresponding source or a written offer, and the license text). Rufus has no official CLI, so automating it this way is impractical. Clean-room reimplementation with Windows APIs and PowerShell, plus permissively licensed pieces such as etcher-sdk (Apache-2.0), is the cleanest path.

### Cited Findings
- GPL FAQ:
  - The GPL permits distributing an aggregate "even when the licenses of the other software are nonfree."
  - "pipes, sockets and command-line arguments are communication mechanisms normally used between two separate programs", and fork/exec of a separate program normally keeps the two separate.
  - Linking into the same executable or address space "almost surely means combining."
  - "If you distribute binaries, you must distribute the complete corresponding source code too", or a written offer for commercial distribution.
  - The FAQ notes the line between separate and combined programs is ultimately a legal question for judges.
  - Source: [GNU GPL FAQ](https://www.gnu.org/licenses/gpl-faq.en.html)
- The Rufus FAQ describes Rufus as 100% open source and Free Software. Its wiki content is CC-BY-SA 4.0. The command-line support FAQ entry exists, but its answer was not retrieved — [Rufus FAQ](https://github.com/pbatard/rufus/wiki/FAQ)
- UEFI:NTFS is GPL-2.0, and its Secure Boot-signed binaries are in Rufus's `res/uefi` folder — [uefi-ntfs](https://github.com/pbatard/uefi-ntfs)
- The NTFS UEFI driver that Microsoft would sign had to be GPLv2 (ntfs-3g), because Microsoft's signing rules exclude GPLv3 — [uefi-ntfs](https://github.com/pbatard/uefi-ntfs); [Rufus FAQ](https://github.com/pbatard/rufus/wiki/FAQ)
- etcher-sdk: Apache-2.0 — [etcher-sdk](https://github.com/balena-io-modules/etcher-sdk)

### Inferences
- Rufus's GPLv3 license comes from the GitHub repository's license file, which I did not open in this session; the user's brief also states GPLv3. GPLv3 adds anti-tivoization and patent clauses.
- Porting algorithms such as the large-FAT32 layout from the code counts as a derivative work. Reimplementing from the FAT32 specification (Microsoft's FAT spec) is safe.
- Bundling `uefi-ntfs.img` (GPLv2) as a separate data file is aggregation. Include the GPLv2 text and a source link or offer (github.com/pbatard/uefi-ntfs at the exact tag).
- Ventoy is GPLv3 as generally known, but the docs page I read only links to a license page, so this is unverified. Embedding Ventoy's installer has the same aggregation considerations as Rufus.
- Not legal advice. Have the project's counsel confirm before distribution.

### Gaps
- I did not retrieve Rufus's LICENSE file or Ventoy's license page directly.
- I did not find the Rufus FAQ's answer on command-line support. From memory, Rufus has no official CLI beyond a few startup flags, but that is unverified.
