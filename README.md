# Flying Thumb v2

Flying Thumb turns a **LILYGO T-Dongle-S3** and microSD card into a Wi-Fi-managed USB drive. Version 2 adds automatic shop-wide discovery and the native Flying Thumb Manager for Windows.

## What is included

- USB mass storage backed by the built-in microSD slot
- Browser upload, download, and deletion
- Setup hotspot and WPS provisioning
- Five-second button hold to forget Wi-Fi
- Friendly machine names such as `LongArm-1`
- A permanent hardware-derived drive ID
- mDNS service advertisement at `_flyingthumb._tcp.local`
- UDP discovery on port 4210 for reliable Windows discovery
- Shop-key protection for uploads, deletes, renaming, and restarts
- Batch upload support that reconnects USB only after the complete batch
- A self-contained Windows manager for discovery, bulk upload, folder sync, and renaming

## First installation and USB recovery

Run **`dist/manager/FlyingThumbManager.exe`** and choose **Install / Recover USB**. The manager checks for and verifies the latest recovery firmware before walking through the button-hold insertion, detecting the recovery COM port, and writing the complete image. Its confirmation and activity log state the exact firmware version, image source, and size. If internet access is unavailable, it explicitly identifies and uses the version bundled beside the Manager. Recovery requires no Python, PlatformIO, or VS Code and does not format or erase the microSD card.

Use this same path to recover a dongle locally if a wireless firmware upgrade is interrupted or the installed firmware cannot boot. The separate **`Flash Flying Thumb.cmd`** development script remains available as a backup on a development PC.
## Set up each dongle

1. Insert a FAT32 or exFAT microSD card and flash the firmware.
2. Join the `FlyingThumb-XXXX` network using password `flyingthumb`.
3. Open `http://192.168.77.1`. Windows can keep its wired connection active; this less-common setup range avoids most office-network address conflicts.
4. Give it a meaningful machine name, enter the machine's Wi-Fi credentials, and enter your shop management key.
5. Use the same management key for every dongle that should be managed together.

After restarting, the setup hotspot turns off. The display shows the friendly machine name and IP address but never the joined Wi-Fi name.

A short button press restarts into a memory-reserved, pairing-only mode and starts a two-minute WPS window before USB storage and web services load. Flying Thumb automatically retries transient WPS failures while you press the router's WPS button; success or timeout restores the display with the final result and then restarts into normal drive mode. The pairing screen includes the running firmware version. Holding the button for five seconds erases the stored Wi-Fi network and returns the drive to setup mode. The drive name and management key are retained.

The LCD and backlight turn off after three minutes without a status change or button press. A short press while the screen is off only wakes the display; it does not start WPS. Holding for five seconds still performs the Wi-Fi reset. On an established Wi-Fi connection, firmware 2.4.0 and later use maximum modem sleep: the radio wakes for access-point beacons and queued traffic, keeping discovery and file access available while reducing idle radio power. Setup access-point mode remains fully awake.

## Flying Thumb Manager

The manager is organized around everyday file work. Select drives, then drag files anywhere onto the window or choose **Add Files to Selected**. This distributes files immediately without requiring the drives to be synchronized.

Each file row has its own checkbox, and the checkbox in the column header toggles every visible file on or off. Check any set of files and **Sync...** will synchronize only that set; when nothing is checked, **Sync...** synchronizes the complete file view. The **Add files**, **Sync...**, **Refresh**, and confirmed **Delete** buttons sit together directly above the file list. Ctrl/Shift row selection remains available for right-click actions and Delete-key use.

While files are being added or synchronized, the bottom status bar shows live byte progress, the current filename and destination, and the completed transfer count. Large single-file transfers therefore continue to show movement instead of only displaying a busy cursor.

The **Files across all drives** view shows the additive union of filenames and creates one status/size column per discovered drive. Missing files show `—`; present files show their size. Same-name files with different sizes are marked as conflicts.

Choose **Sync...** to keep unique files additive across the included drives. For each differing same-name file, choose which drive's copy should win; check **Apply option to all files** to reuse that drive for the remaining conflicts. Select any number of file rows and right-click **Sync selected files...** to resolve or distribute only that selection; the one-row wording remains **Sync this file...**. Synchronization never deletes files. Select one or more rows and press **Delete**, or right-click and choose **Delete**, to remove every listed copy from the included drives after confirmation.

Discovery, renaming, update checks, and USB installation/recovery live in the **File** and **Drives** menus. The manager checks for updates after discovering drives and also provides **File > Check for Updates**. When an update is available, choose **Update Now**; a typical drive update takes about five seconds.
## Automatic update checks

The first installation uses the USB button-hold installer. After that, the manager checks GitHub Releases after discovering drives and compares each discovered drive with the latest available software. It only displays an update notice when something can be updated. Choose **Update Now** to update every discovered outdated drive; a typical drive update takes about five seconds. Wi-Fi credentials, friendly names, shop keys, and microSD files are retained.

Use **File > Check for Updates** to check manually. Downloads are delivered over HTTPS and verified against the SHA-256 values in the release manifest before installation.
## Removable demo drives

Run **`scripts/create-demo-drives.ps1`** to create a local `demo-drives` folder containing three folder-backed simulated drives. The manager discovers them through a separate discovery provider and accesses them through the same `FlyingThumbClient` API used for real network drives. The UI, file matrix, drag-and-drop distribution, and additive synchronization therefore use the same application logic in both cases.

The sample set includes shared files, machine-specific files, missing-file cases, and a deliberate `Shared Shop Notes.txt` conflict. The generated folder is intentionally excluded from Git so real test files are never published accidentally. Delete the single `demo-drives` folder to remove and disable every simulated drive; no source or configuration change is required.
## Firmware files

- `dist/FlyingThumb-v2-full.bin` is the complete image for flashing at address `0x0`.
- `dist/FlyingThumb-v2-wifi-update.bin` is the wireless drive-software update image.

To build or upload through PlatformIO:

```powershell
py -3 -m platformio run
py -3 -m platformio run --target upload
```

To rebuild the manager:

```powershell
dotnet build manager/FlyingThumbManager.csproj -c Release
```

## Network protocol

Managers broadcast the ASCII message `FLYINGTHUMB_DISCOVER_V1` to UDP port `4210`. Each dongle replies directly with its JSON identity, friendly name, address, firmware version, free space, and enrollment state. Drives also advertise `_flyingthumb._tcp.local` over mDNS.

Mutating HTTP requests use the `X-FlyingThumb-Key` header. Credentials and management keys are never included in discovery responses.

## Storage safety

Every physical plug-in starts as a normal writable USB thumb drive. Discovery and file viewing do not change that state. Before the first Manager/web file mutation, firmware 2.2.0 or newer blocks USB writes, logically refreshes the medium as read-only, remounts FatFs to discard stale metadata, and grants the network side exclusive write control. With firmware 2.3.0 or newer, choose **Drives > Return USB to Writable Mode...** to safely remount and reconnect the disk as writable without physically unplugging it. A later Manager mutation repeats the managed-mode handoff.

Uploads are transactional: data is written to a hidden temporary file, verified, swapped into place while preserving the previous file as a rollback copy, and verified again. After a completed batch, the firmware reports a logical media change so the attached host reloads the directory; the ESP32 and USB controller remain connected. Manager 1.0.3 or newer refuses file changes on older firmware because the former surprise-disconnect method could damage FAT metadata.

## Activity LED

- Blue: idle
- Green: USB read
- Red: USB write
- Yellow: recent read and write

The firmware uses LILYGO's official 16 MB flash, no-PSRAM T-Dongle-S3 hardware profile and the maintained PIOArduino ESP32 platform. The hardware pin assignments follow LILYGO's official T-Dongle-S3 documentation. The original USB mass-storage proof of concept came from ThingPulse's `esp32-s3-pendrive-wireless-usb-disk` project.
