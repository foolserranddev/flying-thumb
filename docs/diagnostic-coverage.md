# Flying Thumb diagnostic coverage

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

Implemented since this inventory: schema-v2 validation; structured results viewer; per-record flash checkpoints; stale-result erase; 120-second deadline task returning to recovery; both-core arithmetic/scheduling; SHA-256 known vector; NVS close/reopen persistence and cleanup; reserved-sector flash roundtrip; BLE controller initialization; SD one-bit/four-bit initialization and raw sector sampling; installed partition-layout validation; exact app0/boot-selection/coredump backups and restoration.

Also implemented: device MAC verification after recovery; backup hashes and durable session journal; interrupted-session restore command; independent restored-byte verification; parser/layout/session corruption tests; primary partition bounds and FAT BPB geometry; saved-network association/IP assignment without credential logging; companion network and Windows file-access roundtrips.

Remaining integration work: PSRAM board-profile handling, fixture/manual test prompts, full workflow fault tests and live end-to-end execution. Current COM6 does not answer recovery handshake, so it cannot yet provide that runtime evidence. Companion Windows tests require the user to identify the drive root; the Manager cannot infer that an arbitrary mounted volume belongs to the COM device. Network tests use an explicitly included discovered drive. Neither substitutes for printer-specific enumeration testing.

## Completion audit (2026-10-08)

### Live reset investigation

The original drive (MAC 44:1b:f6:ed:92:78) briefly became reachable on COM6. Security state and the installed partition layout matched the supported profile. The original sectors touched by the temporary tester, boot selection and crash-report region were preserved under `work/live-diagnostic-recovery/session.json` with SHA-256 hashes.

An RTC software reset failed to start the tester. Espressif's watchdog reset sequence did change the Windows port to COM7, confirming a different USB enumeration, but COM7 could not be opened (Windows error 31: device not functioning). A scoped PnP device restart failed with access denied. No successful diagnostic result retrieval or original-firmware restoration has been demonstrated. The temporary tester remains installed; physical recovery insertion is required before reading its report and restoring the saved session.

The preview now bundles a self-contained, MAC-checked watchdog boot helper and a revised tester return path with explicit USB detach and watchdog reset. Builds pass; this new return path is not yet verified on hardware. These artifacts must remain preview-only until an actual run/report/restore cycle succeeds.

Implemented source and build verification do not prove hardware operation. The Manager build passes, diagnostic firmware builds pass, and fault tests cover malformed/incomplete reports, identity extraction, layout rejection and modified backups. Internal die-temperature sensing is included. Live execution and automatic recovery/restoration remain unverified. No expanded diagnostic release has been published.

COM6 is the only registered serial port and repeatedly returns no ROM data. Earlier native USB resets and 1200-baud touches also failed. A physical recovery-mode insertion, or another remotely accessible board already in ROM recovery mode, is needed to test the complete workflow. Keep the goal unfinished until that evidence exists; do not advertise the source as a complete hardware certification.

Fixture-dependent coverage: rail voltage/current, antenna matching and RF output, physical LED colors/brightness, switch actuation, expansion-port continuity, USB signal quality, absolute crystal accuracy, and full peripheral pin routing require external observation or equipment. There is no software-only electrical continuity test for an open trace ending at an input-only peripheral. PSRAM is reported as skipped if disabled/unavailable rather than assumed faulty; determining an alternate PSRAM-enabled profile requires confirmed board memory wiring. Exhaustive flash/card capacity and wear testing requires a separate preserved-data stress workflow and is not covered by the bounded sample tests.

Primary references: Espressif GPIO documentation (https://docs.espressif.com/projects/esp-idf/en/v5.0/esp32s3/api-reference/peripherals/gpio.html), heap diagnostics (https://docs.espressif.com/projects/esp-idf/en/v5.5/esp32s3/api-reference/system/heap_debug.html), and partition APIs (https://documentation.espressif.com/projects/esp-idf/en/latest/esp32s3/api-reference/peripherals/spi_flash/index.html).
