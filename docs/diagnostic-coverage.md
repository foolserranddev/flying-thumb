# Flying Thumb diagnostic coverage

## Firmware 2.5.4 transfer regression

On 2026-10-10 the physical drive `44:1b:f6:ed:92:78` reproduced connection-reset failures and reported unavailable storage under 2.5.3. The transfer path previously blocked USB writes but re-exposed media for raw reads during the network batch, including around card unmount/remount. Firmware 2.5.4 serializes/drains raw callbacks before remount, blocks all raw access for the batch, and exposes media only after the filesystem is ready again. It also increases the multipart upload inactivity deadline from five to thirty seconds and adds last-upload error/byte count, uptime, reset cause and RSSI to device status. These address concrete access and timeout vulnerabilities; they do not independently prove the precise cause of every earlier disconnect.

The final physical regression uploaded and exactly read back 2,906,584, 2,633,684 and 24,027,584-byte temporary files in one batch, then successfully handed storage back to USB. A separate test using the Manager's actual `UploadAsync`/`DownloadAsync`/`CommitFileBatchAsync` uploaded 24,027,584 bytes in 61.1 seconds, matched every readback byte, deleted the test file and returned USB to writable mode. Final status: storage ready, managed mode off, zero raw read/write failures, uninterrupted uptime through the successful tests. Earlier reboot suspicion was not established by a before/after uptime measurement; final reset reason 3 reflects the deliberate OTA software restart. Only uniquely named temporary test files/directories were removed. Existing user files and Wi-Fi settings were preserved.

Manager 1.1.13 retains underlying transport exceptions in transfer error dialogs and explicitly labels request timeouts. Local tests cover both message cases.

## Manager 1.1.10 / firmware 2.5.3

The Drives menu exposes one diagnostic command: Diagnose a Drive over USB. It queries the explicitly identified Flying Thumb USB console first, locks and dismounts its Windows volume before requesting recovery, and follows its MAC across re-enumeration. Already-recovering devices enter the existing test workflow directly. A matching interrupted backup is restored and verified automatically before another test. After firmware restoration, an identity-matched Windows volume receives a unique temporary 64 KiB nested-folder write/read/rename/delete test. The restored drive is also discovered by its firmware-derived device ID for a temporary network upload/readback/delete test when reachable and authenticated. Missing volumes/network access are reported as skipped, and host failures remain failures.

Normal firmware now exposes USB CDC alongside storage, including when no card mounts. It reports read/write failure counters and the last failing sector before recovery; these counters are reset by reboot. Opening its console cannot trigger line-state resets. Firmware older than 2.5.3 or an unresponsive application cannot accept this new command, so a one-time firmware upgrade or physical recovery may still be required. The new normal-to-recovery path is compiled but has not yet been physically exercised.

Manager uploads/sync support cancellation of active HTTP transfers and stop scheduling subsequent files, then finalize any opened file batches without the cancelled token. Confirmed files remain cached; cancelled/unacknowledged uploads are not claimed successful. Persistent timestamped logs record drive address, firmware, path, size, server error and exception, accessible via Help > View Diagnostic Log. Upload/download cancellation has local automated tests. The transfer timeout was increased from 90 seconds to ten minutes to avoid aborting slow but progressing large copies; this is not a fix for USB storage hardware/transport errors.

Results must distinguish PASS, FAIL, SKIP, UNVERIFIED and incomplete execution. A successful command does not establish that every circuit attached to it works.

| Failure point | Available evidence/test | Coverage limit |
| --- | --- | --- |
| COM/cable/USB ROM transport | ROM handshake, repeated chip identity, flash reads and verified writes | Cannot isolate cable, port, power or chip if handshake fails |
| CPU identity/revision/cores | ROM identity and runtime chip information | Full CPU instruction/BIST and second-core workload still needed |
| Flash identification/security | JEDEC capacity, security information | Security state is configuration, not a functional pass/fail |
| Installed firmware | Application digest comparison | Different version is not hardware failure; active slot must be identified |
| Firmware restoration | Preserve app0 and boot-selection bytes before replacing app0; restore originals | Durable backup retained for interrupted recovery |
| Internal RAM | Four data patterns XOR address over allocated block; heap integrity before/after | Does not test RAM occupied by program, stack or peripherals |
| PSRAM | Same patterns when region is enabled and allocatable | Disabled/not fitted must be distinguished by board profile |
| Timing | Runtime timer versus scheduler delay | Requires external reference for absolute crystal calibration |
| RNG | Nonconstant output sample | Not a statistical entropy or security certification |
| LED clock/data | Pull levels, driven readback, mutual shorts | Does not prove trace continuity, LED supply or light output |
| LED colors | Send red/green/blue/white/off | Physical light requires observer or external sensor |
| Button | Released/pressed input | Actuation test requires physical press; static input cannot prove continuity |
| SD bus/card | Card initialization and capacity | Missing card, wiring and bad card may produce same failure |
| SD filesystem | Unique temporary folder; 64 KiB pattern write/read, rename and cleanup | Does not establish full-card capacity or detect every bad sector |
| Wi-Fi receive | Scan, network count, RSSI and channel | Zero networks is inconclusive; antenna cannot be isolated |
| Wi-Fi transmit | AP creation | Remote peer exchange required to prove over-the-air transmit |
| Wi-Fi provisioning | WPS requires router PBC; saved-credentials association requires real AP | External environment required; diagnostic must not erase saved credentials |
| USB mass storage | Windows host enumeration, file roundtrip and reconnect | ROM COM alone does not test MSC; printer compatibility needs actual printer |
| Power/brownout | Reset cause, observed reboot behavior under load | Board lacks calibrated rail/current measurement |
| Unused GPIO / expansion | Board schematic and fixture required | Never blindly drive flash, PSRAM, USB or unknown attached nets |
| Display | Legacy-only optional visual test | No display on current product; no automatic optical feedback |
| Flash wear/full-capacity | Reserved scratch erase/program/readback, or preserved full-image test | Must reserve scratch explicitly; never overwrite user/NVS/OTA areas |
| NVS/settings | Read and decode metadata, bounded separate namespace roundtrip | Do not print credentials; avoid mutating customer namespaces |
| BLE | Controller initialization and remote scan/connect | External peer required for radio proof |
| AES | AES-128 encrypt/decrypt known vector | Does not certify every cryptographic mode or silicon transistor |
| UART1/2 | Internal TX/RX FIFO loopback with no GPIO routing | External UART wiring requires a fixture; existing console is preserved |
| SPI2/3 | MOSI/MISO feedback on known LCD/LED nets | LCD chip select held inactive; no unknown expansion pins driven |
| PWM/LEDC | 1 kHz 25-percent-duty feedback at LED data pin | LED clock held low; sampling tolerates scheduling jitter |
| RMT | Timed TX/RX pulse capture at LED data pin | LED clock held low; tests pulse engine rather than an external protocol peer |

Implemented since this inventory: schema-v2 validation; structured results viewer; per-record flash checkpoints; stale-result erase; 120-second deadline task returning to recovery; both-core arithmetic/scheduling; SHA-256 known vector; NVS close/reopen persistence and cleanup; reserved-sector flash roundtrip; BLE controller initialization; SD one-bit/four-bit initialization and raw sector sampling; installed partition-layout validation; exact app0/boot-selection/coredump backups and restoration.

Also implemented: device MAC verification after recovery; backup hashes and durable session journal; interrupted-session restore command; independent restored-byte verification; parser/layout/session corruption tests; primary partition bounds and FAT BPB geometry; saved-network association/IP assignment without credential logging; companion network and Windows file-access roundtrips.

Fixture-dependent and unavailable prerequisites remain explicitly skipped or unverified, rather than declared healthy. PSRAM is tested only when enabled and allocatable. Companion Windows tests require an identified drive root; network tests use an explicitly selected discovered drive. Neither substitutes for printer-specific enumeration testing. The current automatic COM workflow has now passed end-to-end on the physical board, including independent restoration verification.

## Completion audit (2026-10-08)

### Live reset investigation

The original drive (MAC 44:1b:f6:ed:92:78) briefly became reachable on COM6. Security state and the installed partition layout matched the supported profile. The original sectors touched by the temporary tester, boot selection and crash-report region were preserved under `work/live-diagnostic-recovery/session.json` with SHA-256 hashes.

An RTC software reset failed to start the tester. Espressif's watchdog reset sequence did change the Windows port to COM7, confirming a different USB enumeration, but COM7 could not be opened (Windows error 31: device not functioning). A scoped PnP device restart failed with access denied. No successful diagnostic result retrieval or original-firmware restoration has been demonstrated. The temporary tester remains installed; physical recovery insertion is required before reading its report and restoring the saved session.

The preview now bundles a self-contained, MAC-checked watchdog boot helper and a revised tester return path with explicit USB detach and watchdog reset. Builds pass; this new return path is not yet verified on hardware. These artifacts must remain preview-only until an actual run/report/restore cycle succeeds.

### Current state (2026-10-09)

The original transport was superseded after a reset/re-enumeration loop. Diagnostics now compile specifically for hardware CDC/JTAG (`HWCDC_V1`), while normal mass-storage firmware keeps TinyUSB. The current tester uses the same hardware USB peripheral as ROM recovery, avoiding a PHY/controller switch. Compilation is proven; a live automatic return is still required.

A saved live report proved RAM patterns, both cores, SHA-256, temperature sensing, heap integrity, NVS persistence, scratch-flash writes, BLE controller initialization, LED GPIO levels/cross-shorts/color commands, Wi-Fi scan/AP startup, SD raw reads and filesystem roundtrip/rename/cleanup. It completed in approximately nine seconds. The apparent SD partition-bound failure used filesystem capacity from `numSectors()` rather than physical capacity; diagnostics and USB MSC now use `cardSize()/sectorSize()`.

Normal firmware 2.5.1 was installed and independently verified on the original board; Windows subsequently mounted it as E:. The temporary tester was removed. The hardware-serial diagnostic and its new return path remain preview-only. A complete run via the Manager with report retrieval and verified restoration is the outstanding runtime requirement; no connected drive is visible at the latest check.

Windows host-side verification subsequently identified E: by USB serial 441BF6ED9278 and passed a 256 KiB temporary-file test with nested directories, exact readback hash, rename and cleanup. Only the unique test folder was removed. Buffered Windows reads may be cached; this is evidence of functional filesystem access, not a raw USB read-speed or exhaustive media certification. The repeatable host script uses write-through plus flush for writes.

The hardware-serial tester returned automatically and produced a complete report with FAT geometry passing. The Manager's own unattended entry point then completed the same workflow: no failures in the completed checks, original application/boot/crash-report storage restored and independently verified, and normal firmware restarted. Evidence is retained at `work/native-transport-live-test/manager-report-quiet.txt`. The Manager deliberately waits for the bounded tester before probing recovery, avoiding line-state interference during execution.

Manager 1.1.9 adds compatible-helper detection/download, reports firmware-reference differences separately from hardware failures, and exposes authenticated network recovery. Firmware 2.5.2 includes the network hook and keeps LCD-plus-LED output on all drives. Client tests verify the recovery endpoint, key header, busy refusal and authentication rejection. Full network-hook execution additionally requires the drive to be reachable on Wi-Fi with a configured shop key.

The expanded packaged run passed AES, UART1/2 and RMT pulse capture. SPI/PWM feedback initially failed because the test called `gpio_set_direction` after peripheral setup, replacing the peripheral output routing with ordinary GPIO. Espressif's driver explicitly performs that replacement (https://raw.githubusercontent.com/espressif/esp-idf/v5.5/components/esp_driver_gpio/src/gpio.c). SPI now reconnects its output/input signals and PWM enables only the input buffer. The corrected packaged run passed SPI2, SPI3, PWM and every other completed automatic stage, then restored and independently verified normal firmware 2.5.2. Evidence is retained at `work/native-transport-live-test/corrected-release-report.txt` and its stage log. No failures or incomplete required stages were reported.

Implemented source and build verification do not prove hardware operation. The Manager build passes, diagnostic firmware builds pass, and fault tests cover malformed/incomplete reports, identity extraction, layout rejection and modified backups. Internal die-temperature sensing is included. Live execution and automatic recovery/restoration remain unverified. No expanded diagnostic release has been published.

COM6 is the only registered serial port and repeatedly returns no ROM data. Earlier native USB resets and 1200-baud touches also failed. A physical recovery-mode insertion, or another remotely accessible board already in ROM recovery mode, is needed to test the complete workflow. Keep the goal unfinished until that evidence exists; do not advertise the source as a complete hardware certification.

Fixture-dependent coverage: rail voltage/current, antenna matching and RF output, physical LED colors/brightness, switch actuation, expansion-port continuity, USB signal quality, absolute crystal accuracy, and full peripheral pin routing require external observation or equipment. There is no software-only electrical continuity test for an open trace ending at an input-only peripheral. PSRAM is reported as skipped if disabled/unavailable rather than assumed faulty; determining an alternate PSRAM-enabled profile requires confirmed board memory wiring. Exhaustive flash/card capacity and wear testing requires a separate preserved-data stress workflow and is not covered by the bounded sample tests.

Primary references: Espressif GPIO documentation (https://docs.espressif.com/projects/esp-idf/en/v5.0/esp32s3/api-reference/peripherals/gpio.html), heap diagnostics (https://docs.espressif.com/projects/esp-idf/en/v5.5/esp32s3/api-reference/system/heap_debug.html), and partition APIs (https://documentation.espressif.com/projects/esp-idf/en/latest/esp32s3/api-reference/peripherals/spi_flash/index.html).
