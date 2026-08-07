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
uint32_t lastRead = 0, lastWrite = 0, pressedAt = 0;
bool resetHandled = false;
bool usbDiskReady = false, usbUpdateActive = false;

int32_t onWrite(uint32_t lba, uint32_t offset, uint8_t *buffer, uint32_t size) {
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
  msc.mediaPresent(true);
  usbDiskReady = msc.begin(SD_MMC.numSectors(), SD_MMC.sectorSize());
  if (usbDiskReady) USB.begin();
}
void serviceButton() {
  const bool down = digitalRead(PIN_BUTTON) == LOW;
  if (down && !pressedAt) { pressedAt = millis(); resetHandled = false; }
  if (down && !resetHandled && millis() - pressedAt >= RESET_HOLD_MS) {
    resetHandled = true; displayMessage("RESETTING", "WiFi cleared", "Setup mode");
    clearNetworkSettings(); delay(800); ESP.restart();
  }
  if (!down && pressedAt) {
    const uint32_t duration = millis() - pressedAt; pressedAt = 0;
    if (!resetHandled && duration >= DEBOUNCE_MS && duration < RESET_HOLD_MS) {
      displayMessage("WPS PAIRING", "Press router", "WPS button"); beginWpsPairing();
    }
  }
}
}

bool beginUsbFileUpdate() {
  if (!usbDiskReady) return false;
  if (usbUpdateActive) return true;
  msc.mediaPresent(false);
  delay(75);
  tud_disconnect();
  delay(250);
  usbUpdateActive = true;
  return true;
}

void finishUsbFileUpdate() {
  if (!usbDiskReady || !usbUpdateActive) return;
  msc.mediaPresent(true);
  tud_connect();
  usbUpdateActive = false;
}

bool usbFileUpdateActive() { return usbUpdateActive; }

void setup() {
  Serial.begin(115200); pinMode(PIN_BUTTON, INPUT_PULLUP); initDisplay();
  displayMessage("FLYING THUMB", "Starting...", "");
  initNetworkAndServer();
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
  setActivityLed(millis() - lastRead < 250, millis() - lastWrite < 250); delay(2);
}
