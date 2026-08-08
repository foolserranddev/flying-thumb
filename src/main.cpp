#include <Arduino.h>
#include <SD_MMC.h>
#include <USB.h>
#include <USBMSC.h>
#include <tusb.h>
#include "board_config.h"
#include "display.h"
#include "fileserver.h"
#include "ota_health.h"
#include "usb_disk.h"

USBMSC msc;
namespace {
volatile uint32_t lastRead = 0, lastWrite = 0;
uint32_t pressedAt = 0;
bool resetHandled = false;
bool wakeOnlyPress = false;
bool usbDiskReady = false, usbUpdateActive = false, usbManagedMode = false;
volatile bool usbWritesBlocked = false;

int32_t onWrite(uint32_t lba, uint32_t offset, uint8_t *buffer, uint32_t size) {
  if (usbWritesBlocked) return -1;
  const uint32_t sector = SD_MMC.sectorSize();
  if (!sector || sector > 512) return -1;
  uint8_t scratch[512];
  uint32_t completed = 0;
  while (completed < size) {
    const uint32_t absolute = offset + completed;
    const uint32_t targetLba = lba + absolute / sector;
    const uint32_t withinSector = absolute % sector;
    const uint32_t chunk = min(size - completed, sector - withinSector);
    if (withinSector == 0 && chunk == sector) {
      if (!SD_MMC.writeRAW(buffer + completed, targetLba)) return -1;
    } else {
      if (!SD_MMC.readRAW(scratch, targetLba)) return -1;
      memcpy(scratch + withinSector, buffer + completed, chunk);
      if (!SD_MMC.writeRAW(scratch, targetLba)) return -1;
    }
    completed += chunk;
  }
  lastWrite = millis();
  return size;
}
int32_t onRead(uint32_t lba, uint32_t offset, void *buffer, uint32_t size) {
  const uint32_t sector = SD_MMC.sectorSize();
  if (!sector || sector > 512) return -1;
  uint8_t scratch[512];
  auto *destination = static_cast<uint8_t *>(buffer);
  uint32_t completed = 0;
  while (completed < size) {
    const uint32_t absolute = offset + completed;
    const uint32_t sourceLba = lba + absolute / sector;
    const uint32_t withinSector = absolute % sector;
    const uint32_t chunk = min(size - completed, sector - withinSector);
    if (withinSector == 0 && chunk == sector) {
      if (!SD_MMC.readRAW(destination + completed, sourceLba)) return -1;
    } else {
      if (!SD_MMC.readRAW(scratch, sourceLba)) return -1;
      memcpy(destination + completed, scratch + withinSector, chunk);
    }
    completed += chunk;
  }
  lastRead = millis();
  return size;
}
bool onStartStop(uint8_t, bool, bool) { return true; }
void startUsbDisk() {
  msc.vendorID("FlyingThumb"); msc.productID("WiFi Storage"); msc.productRevision("1.0");
  msc.onRead(onRead); msc.onWrite(onWrite); msc.onStartStop(onStartStop);
  msc.isWritable(true);
  msc.mediaPresent(true);
  usbDiskReady = msc.begin(SD_MMC.numSectors(), SD_MMC.sectorSize());
  if (usbDiskReady) USB.begin();
}
void serviceButton() {
  const bool down = digitalRead(PIN_BUTTON) == LOW;
  if (down && !pressedAt) { pressedAt = millis(); resetHandled = false; wakeOnlyPress = wakeDisplay(); }
  if (down && !resetHandled && millis() - pressedAt >= RESET_HOLD_MS) {
    resetHandled = true; displayMessage("RESETTING", "WiFi cleared", "Setup mode");
    clearNetworkSettings(); delay(800); ESP.restart();
  }
  if (!down && pressedAt) {
    const uint32_t duration = millis() - pressedAt; pressedAt = 0;
    if (!resetHandled && !wakeOnlyPress && duration >= DEBOUNCE_MS && duration < RESET_HOLD_MS) {
      displayMessage("WPS STARTING", "Releasing memory", "Please wait"); beginWpsPairing();
    }
    wakeOnlyPress = false;
  }
}
}

bool beginUsbFileUpdate() {
  if (!usbDiskReady) return false;
  if (usbUpdateActive) return true;
  usbWritesBlocked = true;
  if (!usbManagedMode) {
    // Stop accepting new host writes, allow any callback already in flight to
    // finish, then make Windows/machine hosts re-query write protection.
    delay(500);
    msc.isWritable(false);
    msc.mediaPresent(false);
    delay(750);
    // USB may have changed FAT metadata since boot. Remount before using the
    // file-level API so it cannot operate from a stale filesystem cache.
    SD_MMC.end();
    delay(100);
    if (!SD_MMC.begin("/sdcard", false)) {
      usbDiskReady = false;
      displayMessage("TF CARD ERROR", "Managed mode failed", "Replug device");
      return false;
    }
    msc.mediaPresent(true);
    delay(250);
    usbManagedMode = true;
    displayMessage("MANAGED MODE", "USB read-only", "Manager: release");
  }
  usbUpdateActive = true;
  return true;
}

void finishUsbFileUpdate() {
  if (!usbDiskReady || !usbUpdateActive) return;
  // Keep USB electrically connected. A logical media change makes the host
  // discard cached FAT metadata and reload the completed read-only volume.
  msc.mediaPresent(false);
  delay(750);
  msc.isWritable(false);
  msc.mediaPresent(true);
  usbUpdateActive = false;
}

bool usbFileUpdateActive() { return usbUpdateActive; }
bool usbManagedModeActive() { return usbManagedMode; }

bool releaseUsbManagedMode() {
  if (!usbDiskReady || usbUpdateActive) return false;
  if (!usbManagedMode) return true;
  usbWritesBlocked = true;
  msc.mediaPresent(false);
  delay(750);
  SD_MMC.end();
  delay(100);
  if (!SD_MMC.begin("/sdcard", false)) {
    usbDiskReady = false;
    displayMessage("TF CARD ERROR", "USB release failed", "Replug device");
    return false;
  }
  msc.isWritable(true);
  usbManagedMode = false;
  usbWritesBlocked = false;
  msc.mediaPresent(true);
  delay(250);
  displayMessage("USB WRITABLE", "Manager released", "WiFi ready");
  return true;
}

void setup() {
  Serial.begin(115200); pinMode(PIN_BUTTON, INPUT_PULLUP); initDisplay();
  displayMessage("FLYING THUMB", "Starting...", "");
  initNetworkAndServer();
  if (wpsPairingBootActive()) return;
  delay(50);
  SD_MMC.setPins(PIN_SD_CLK, PIN_SD_CMD, PIN_SD_D0, PIN_SD_D1, PIN_SD_D2, PIN_SD_D3);
  const bool cardReady = SD_MMC.begin("/sdcard", false);
  if (cardReady) {
    startUsbDisk();
  } else {
    // Setup and recovery networking must remain usable even without a readable card.
    Serial.println("TF card unavailable; Wi-Fi setup remains active");
    displayMessage("TF CARD ERROR", "Card unavailable", "WiFi still ready");
  }
  finishOtaHealthCheck(cardReady);
}
void loop() {
  serviceButton(); handleNetworkAndServer();
  setActivityLed(millis() - lastRead < 250, millis() - lastWrite < 250); handleDisplayPower(); delay(2);
}
