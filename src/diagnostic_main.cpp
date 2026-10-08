#include <Arduino.h>
#include <FastLED.h>
#include <SD_MMC.h>
#include <WiFi.h>
#include <esp_partition.h>
#include <esp_system.h>
#include <esp_heap_caps.h>
#include <esp_chip_info.h>
#include <esp_random.h>
#include <esp_timer.h>
#include <driver/temperature_sensor.h>
#include <Preferences.h>
#include <mbedtls/sha256.h>
#include "esp32-hal-bt.h"
#include "esp32-hal-alloc-ble-mem.h"
#include "esp32-hal-tinyusb.h"
#include "board_config.h"
#include "soc/rtc_cntl_reg.h"
#include "soc/usb_serial_jtag_reg.h"

namespace {
CRGB diagnosticLed;
uint32_t lastHeartbeat = 0;
bool lastButton = false;
String report;
const esp_partition_t *resultPartition = nullptr;
size_t storedBytes = 0;
volatile bool diagnosticFinished = false;

[[noreturn]] void returnToRecovery() {
  // Give the host a real detach interval, then reset the whole digital domain.
  REG_WRITE(RTC_CNTL_OPTION1_REG, RTC_CNTL_FORCE_DOWNLOAD_BOOT);
  pinMode(19, OUTPUT_OPEN_DRAIN); pinMode(20, OUTPUT_OPEN_DRAIN);
  digitalWrite(19, LOW); digitalWrite(20, LOW);
  delay(100);
  CLEAR_PERI_REG_MASK(RTC_CNTL_USB_CONF_REG, RTC_CNTL_SW_HW_USB_PHY_SEL | RTC_CNTL_SW_USB_PHY_SEL | RTC_CNTL_USB_PAD_ENABLE);
  CLEAR_PERI_REG_MASK(USB_SERIAL_JTAG_CONF0_REG, USB_SERIAL_JTAG_PHY_SEL);
  REG_WRITE(RTC_CNTL_WDTWPROTECT_REG, 0x50D83AA1);
  REG_WRITE(RTC_CNTL_WDTCONFIG1_REG, 2000);
  REG_WRITE(RTC_CNTL_WDTCONFIG0_REG, (1u << 31) | (5u << 28) | (1u << 8) | 2u);
  REG_WRITE(RTC_CNTL_WDTWPROTECT_REG, 0);
  while (true) {}
}

void recoveryDeadline(void *) {
  const uint32_t start = millis();
  while (!diagnosticFinished && millis() - start < 120000) vTaskDelay(pdMS_TO_TICKS(100));
  if (!diagnosticFinished) {
    // A hung test still returns control to the Manager; preceding records survive.
    returnToRecovery();
  }
  vTaskDelete(nullptr);
}

void appendReport(const String &line) {
  report += line;
  report += '\n';
  if (resultPartition && storedBytes + line.length() + 2 < resultPartition->size - 4096) {
    const String record = line + "\n";
    if (esp_partition_write(resultPartition, storedBytes, record.c_str(), record.length()) == ESP_OK)
      storedBytes += record.length();
  }
  Serial.println(line);
  Serial.flush();
}

void checkpoint(const char *name, const char *result = nullptr) {
  String line = "FTDIAG|" + String(millis()) + "|" + name;
  if (result) line += "|" + String(result);
  appendReport(line);
}

void showColor(const char *name, const CRGB &color) {
  checkpoint("LED_COMMAND", name);
  diagnosticLed = color;
  FastLED.show();
  delay(600);
  checkpoint("LED_COMMAND_COMPLETE", name);
}

bool testPinLevels(int pin, const char *name) {
  bool pass = true;
  pinMode(pin, INPUT_PULLDOWN); delay(10); const int pulledDown = digitalRead(pin);
  pinMode(pin, INPUT_PULLUP); delay(10); const int pulledUp = digitalRead(pin);
  pinMode(pin, OUTPUT); digitalWrite(pin, LOW); delay(10); const int drivenLow = digitalRead(pin);
  digitalWrite(pin, HIGH); delay(10); const int drivenHigh = digitalRead(pin);
  pass = pulledDown == LOW && pulledUp == HIGH && drivenLow == LOW && drivenHigh == HIGH;
  char line[180];
  snprintf(line, sizeof(line), "FTDIAG|%lu|GPIO_LEVELS|%s|%s|PULLDOWN=%d|PULLUP=%d|LOW=%d|HIGH=%d",
           millis(), name, pass ? "PASS" : "FAIL", pulledDown, pulledUp, drivenLow, drivenHigh);
  appendReport(line);
  pinMode(pin, INPUT);
  return pass;
}

bool testCrossShort(int first, int second, const char *firstName, const char *secondName) {
  pinMode(second, INPUT_PULLDOWN);
  pinMode(first, OUTPUT); digitalWrite(first, HIGH); delay(10);
  const bool coupledHigh = digitalRead(second) == HIGH;
  digitalWrite(first, LOW); delay(10);
  const bool coupledLow = digitalRead(second) == LOW;
  pinMode(first, INPUT); pinMode(second, INPUT);
  const bool pass = !coupledHigh && coupledLow;
  char line[180];
  snprintf(line, sizeof(line), "FTDIAG|%lu|GPIO_CROSS_SHORT|%s_TO_%s|%s|OTHER_HIGH=%d|OTHER_LOW=%d",
           millis(), firstName, secondName, pass ? "PASS" : "FAIL", coupledHigh, coupledLow);
  appendReport(line);
  return pass;
}

bool saveReport() {
  const esp_partition_t *partition = esp_partition_find_first(
      ESP_PARTITION_TYPE_DATA, ESP_PARTITION_SUBTYPE_DATA_COREDUMP, nullptr);
  if (!partition || report.length() + 1 > partition->size) return false;
  if (esp_partition_erase_range(partition, 0, partition->size) != ESP_OK) return false;
  return esp_partition_write(partition, 0, report.c_str(), report.length() + 1) == ESP_OK;
}

void testMemory(uint32_t caps, const char *name, size_t requested) {
  size_t size = min(requested, heap_caps_get_largest_free_block(caps) / 2);
  if (size < 1024) { checkpoint(name, caps & MALLOC_CAP_SPIRAM ? "SKIP|PSRAM disabled in this build or absent; presence cannot be inferred" : "FAIL|Insufficient internal RAM for test"); return; }
  auto *memory = static_cast<volatile uint32_t *>(heap_caps_malloc(size, caps));
  if (!memory) { checkpoint(name, "FAIL|Allocation failed"); return; }
  bool ok = true;
  const size_t count = size / sizeof(uint32_t);
  for (uint32_t pattern : {0u, 0xffffffffu, 0xaaaaaaaau, 0x55555555u}) {
    for (size_t i = 0; i < count; ++i) memory[i] = pattern ^ static_cast<uint32_t>(i);
    for (size_t i = 0; i < count; ++i) if (memory[i] != (pattern ^ static_cast<uint32_t>(i))) { ok = false; break; }
    delay(1);
  }
  heap_caps_free(const_cast<uint32_t *>(memory));
  checkpoint(name, (String(ok ? "PASS" : "FAIL") + "|BYTES=" + String(size) + "|Four patterns plus address pattern; allocated region only").c_str());
}

void testSystem() {
  esp_chip_info_t chip; esp_chip_info(&chip);
  checkpoint("CHIP", (String(chip.model == CHIP_ESP32S3 && chip.cores == 2 ? "PASS" : "FAIL") + "|CORES=" + String(chip.cores) + "|REVISION=" + String(chip.revision)).c_str());
  checkpoint("HEAP_BEFORE", heap_caps_check_integrity_all(true) ? "PASS" : "FAIL");
  testMemory(MALLOC_CAP_INTERNAL | MALLOC_CAP_8BIT, "INTERNAL_RAM", 65536);
  testMemory(MALLOC_CAP_SPIRAM | MALLOC_CAP_8BIT, "PSRAM", 1048576);
  int64_t started = esp_timer_get_time(); delay(100);
  int64_t elapsed = esp_timer_get_time() - started;
  checkpoint("TIMER", elapsed >= 90000 && elapsed < 500000 ? "PASS|Relative scheduling and timer progression" : "FAIL|Unexpected timer progression");
  uint32_t first = esp_random(); bool varied = false;
  for (int i = 0; i < 32; ++i) varied |= esp_random() != first;
  checkpoint("RNG", varied ? "PASS|Liveness only; not an entropy certification" : "FAIL|Constant output");
  temperature_sensor_handle_t sensor = nullptr;
  temperature_sensor_config_t config = TEMPERATURE_SENSOR_CONFIG_DEFAULT(10, 80);
  bool temperatureOk = temperature_sensor_install(&config, &sensor) == ESP_OK;
  float temperature = 0;
  if (temperatureOk) temperatureOk = temperature_sensor_enable(sensor) == ESP_OK && temperature_sensor_get_celsius(sensor, &temperature) == ESP_OK;
  checkpoint("DIE_TEMPERATURE", (String(temperatureOk ? "PASS" : "FAIL") + "|CELSIUS=" + String(temperature) + "|Internal sensor only; not ambient or rail measurement").c_str());
  if (sensor) { temperature_sensor_disable(sensor); temperature_sensor_uninstall(sensor); }
  checkpoint("POWER_RAILS", "UNVERIFIED|No calibrated rail/current sensing on this board; reset reason only");
  checkpoint("ANTENNA", "UNVERIFIED|RSSI and received networks provide indirect evidence only");
  checkpoint("USB_STORAGE_HOST", "UNVERIFIED|Recovery transport works; printer/Windows enumeration requires host-side test");
  checkpoint("UNCONNECTED_GPIO", "SKIP|Unknown wiring; USB, flash, PSRAM and strap pins must not be blindly driven");
  checkpoint("LCD", "SKIP|Screenless hardware profile");
  checkpoint("BUTTON_ACTUATION", "UNVERIFIED|Physical press required to prove switch and trace continuity");
}

struct CoreTest { volatile bool done = false; volatile bool passed = false; int expected; };
void coreWorker(void *argument) {
  auto *test = static_cast<CoreTest *>(argument);
  volatile uint32_t value = 0;
  for (uint32_t i = 1; i <= 10000; ++i) value += i;
  test->passed = value == 50005000 && xPortGetCoreID() == test->expected;
  test->done = true;
  vTaskDelete(nullptr);
}

void testCoresAndCrypto() {
  static CoreTest cores[2];
  for (int core = 0; core < 2; ++core) {
    cores[core].expected = core;
    const bool created = xTaskCreatePinnedToCore(coreWorker, "diagnostic-cpu", 2048, &cores[core], 1, nullptr, core) == pdPASS;
    uint32_t start = millis();
    while (created && !cores[core].done && millis() - start < 3000) delay(10);
    checkpoint(core == 0 ? "CPU_CORE0" : "CPU_CORE1", created && cores[core].done && cores[core].passed ? "PASS|Pinned task arithmetic and scheduling" : "FAIL|Task did not produce expected result");
  }
  const uint8_t expected[32] = {0xba,0x78,0x16,0xbf,0x8f,0x01,0xcf,0xea,0x41,0x41,0x40,0xde,0x5d,0xae,0x22,0x23,0xb0,0x03,0x61,0xa3,0x96,0x17,0x7a,0x9c,0xb4,0x10,0xff,0x61,0xf2,0x00,0x15,0xad};
  uint8_t digest[32];
  bool ok = mbedtls_sha256(reinterpret_cast<const uint8_t *>("abc"), 3, digest, 0) == 0 && memcmp(digest, expected, 32) == 0;
  checkpoint("SHA256_KNOWN_VECTOR", ok ? "PASS|abc known digest" : "FAIL");
}

void testSettingsAndFlash() {
  Preferences prefs;
  if (!prefs.begin("ft-diagnostics")) { checkpoint("NVS", "FAIL|Namespace could not open"); return; }
  if (prefs.isKey("probe")) { checkpoint("NVS", "SKIP|Existing diagnostic key preserved"); prefs.end(); return; }
  const uint32_t pattern[4] = {0,0xffffffff,0x55555555,0xaaaaaaaa}; uint32_t readback[4] = {};
  bool ok = prefs.putBytes("probe", pattern, sizeof(pattern)) == sizeof(pattern);
  prefs.end(); ok &= prefs.begin("ft-diagnostics");
  ok &= prefs.getBytes("probe", readback, sizeof(readback)) == sizeof(readback) && memcmp(pattern, readback, sizeof(pattern)) == 0;
  checkpoint("NVS_PERSISTENCE", ok ? "PASS|Write close reopen read" : "FAIL");
  checkpoint("NVS_CLEANUP", prefs.remove("probe") ? "PASS" : "FAIL"); prefs.end();
  const esp_partition_t *scratch = resultPartition;
  if (!scratch || scratch->size < 65536) { checkpoint("FLASH_SCRATCH", "SKIP|No validated reserved region"); return; }
  uint8_t data[256], verify[256]; for (size_t i = 0; i < sizeof(data); ++i) data[i] = i ^ 0xa5;
  const size_t offset = scratch->size - 4096;
  bool flashOk = esp_partition_erase_range(scratch, offset, 4096) == ESP_OK;
  flashOk &= esp_partition_write(scratch, offset, data, sizeof(data)) == ESP_OK;
  flashOk &= esp_partition_read(scratch, offset, verify, sizeof(verify)) == ESP_OK && memcmp(data, verify, sizeof(data)) == 0;
  checkpoint("FLASH_SCRATCH", flashOk ? "PASS|Reserved sector erase program readback" : "FAIL");
  esp_partition_erase_range(scratch, offset, 4096);
}

void testCardFiles() {
  const String directory = "/.FlyingThumb-diagnostic-" + String(esp_random(), HEX);
  if (SD_MMC.exists(directory)) { checkpoint("SD_FILE_TEST", "SKIP|Temporary path already exists"); return; }
  bool ok = SD_MMC.mkdir(directory);
  const String path = directory + "/pattern.bin";
  uint8_t pattern[512]; for (size_t i = 0; i < sizeof(pattern); ++i) pattern[i] = (i * 37) ^ (i >> 3);
  File output = SD_MMC.open(path, FILE_WRITE);
  ok &= static_cast<bool>(output);
  uint32_t began = millis();
  for (int i = 0; i < 128 && ok; ++i) ok &= output.write(pattern, sizeof(pattern)) == sizeof(pattern);
  output.flush(); output.close();
  const uint32_t writeMs = millis() - began;
  File input = SD_MMC.open(path, FILE_READ); ok &= static_cast<bool>(input) && input.size() == 65536;
  uint8_t readback[512]; began = millis();
  for (int i = 0; i < 128 && ok; ++i) ok &= input.read(readback, sizeof(readback)) == sizeof(readback) && memcmp(pattern, readback, sizeof(pattern)) == 0;
  input.close();
  checkpoint("SD_FILE_ROUNDTRIP", (String(ok ? "PASS" : "FAIL") + "|BYTES=65536|WRITE_MS=" + String(writeMs) + "|READ_MS=" + String(millis() - began)).c_str());
  bool renamed = ok && SD_MMC.rename(path, directory + "/renamed.bin");
  checkpoint("SD_RENAME", renamed ? "PASS" : "FAIL");
  bool cleaned = true;
  if (SD_MMC.exists(path)) cleaned &= SD_MMC.remove(path);
  if (SD_MMC.exists(directory + "/renamed.bin")) cleaned &= SD_MMC.remove(directory + "/renamed.bin");
  cleaned &= SD_MMC.rmdir(directory);
  checkpoint("SD_CLEANUP", cleaned ? "PASS" : "FAIL|Temporary test files remain");
}

uint16_t le16(const uint8_t *p) { return p[0] | uint16_t(p[1]) << 8; }
uint32_t le32(const uint8_t *p) { return le16(p) | uint32_t(le16(p + 2)) << 16; }

void testCardGeometry() {
  uint8_t sector[512];
  if (SD_MMC.sectorSize() != 512 || !SD_MMC.readRAW(sector, 0)) { checkpoint("SD_GEOMETRY", "FAIL|Cannot read sector zero"); return; }
  const uint32_t capacity = SD_MMC.numSectors();
  uint32_t start = 0, available = capacity;
  const bool looksLikeBoot = (sector[0] == 0xeb || sector[0] == 0xe9) && le16(sector + 11) == 512;
  if (!looksLikeBoot) {
    bool found = false;
    for (int i = 0; i < 4; ++i) {
      const uint8_t *entry = sector + 446 + 16 * i;
      uint32_t offset = le32(entry + 8), length = le32(entry + 12);
      if (!entry[4] || !length) continue;
      if (offset >= capacity || length > capacity - offset) { checkpoint("SD_PARTITION_BOUNDS", "FAIL|Partition extends outside card capacity"); return; }
      if (!found) { start = offset; available = length; found = true; }
    }
    if (!found || !SD_MMC.readRAW(sector, start)) { checkpoint("SD_GEOMETRY", "FAIL|No readable primary partition or superfloppy BPB"); return; }
  }
  uint32_t total = le16(sector + 19); if (!total) total = le32(sector + 32);
  uint32_t fatSize = le16(sector + 22); if (!fatSize) fatSize = le32(sector + 36);
  uint32_t reserved = le16(sector + 14), fats = sector[16], clusterSize = sector[13];
  uint32_t rootSectors = (uint32_t(le16(sector + 17)) * 32 + 511) / 512;
  uint64_t overhead = uint64_t(reserved) + uint64_t(fats) * fatSize + rootSectors;
  bool valid = sector[510] == 0x55 && sector[511] == 0xaa && le16(sector + 11) == 512 && clusterSize && !(clusterSize & (clusterSize - 1)) && clusterSize <= 128 && reserved && fats >= 1 && fats <= 2 && fatSize && total && total <= available && overhead < total;
  checkpoint("SD_FAT_GEOMETRY", (String(valid ? "PASS" : "FAIL") + "|PARTITION_START=" + String(start) + "|VOLUME_SECTORS=" + String(total) + "|CARD_SECTORS=" + String(capacity) + "|SECTORS_PER_CLUSTER=" + String(clusterSize)).c_str());
  if (valid) {
    uint64_t clusters = (total - overhead) / clusterSize;
    checkpoint("SD_FAT_TYPE", (String("INFO|TYPE=") + (clusters < 4085 ? "FAT12" : clusters < 65525 ? "FAT16" : "FAT32") + "|CLUSTERS=" + String(static_cast<uint32_t>(clusters))).c_str());
  }
}

void testSavedNetwork() {
  Preferences network;
  if (!network.begin("network", true)) { checkpoint("WIFI_SAVED_CONNECTION", "SKIP|No saved network namespace"); return; }
  String ssid = network.getString("ssid", ""), password = network.getString("password", "");
  bool wps = network.getBool("wps", false); network.end();
  if (!ssid.length() && !wps) { checkpoint("WIFI_SAVED_CONNECTION", "SKIP|No saved credentials"); return; }
  WiFi.persistent(false);
  if (wps) WiFi.begin(); else WiFi.begin(ssid.c_str(), password.c_str());
  uint32_t start = millis();
  while (WiFi.status() != WL_CONNECTED && millis() - start < 15000) delay(100);
  bool connected = WiFi.status() == WL_CONNECTED;
  checkpoint("WIFI_SAVED_CONNECTION", (String(connected ? "PASS" : "FAIL") + "|STATUS=" + String(static_cast<int>(WiFi.status())) + "|Credentials hidden; router reachability also affects result").c_str());
  if (connected) {
    checkpoint("WIFI_IP_ASSIGNMENT", WiFi.localIP() != IPAddress(0,0,0,0) ? "PASS|IP address assigned" : "FAIL|No IPv4 address");
    checkpoint("WIFI_CONNECTED_RSSI", (String("INFO|DBM=") + String(WiFi.RSSI())).c_str());
  }
  WiFi.disconnect(false, false);
}
}

void setup() {
  Serial.begin(115200);
  const uint32_t consoleWaitStarted = millis();
  while (!Serial && millis() - consoleWaitStarted < 2000) delay(25);
  delay(250);
  resultPartition = esp_partition_find_first(ESP_PARTITION_TYPE_DATA, ESP_PARTITION_SUBTYPE_DATA_COREDUMP, nullptr);
  if (resultPartition && esp_partition_erase_range(resultPartition, 0, resultPartition->size) != ESP_OK) resultPartition = nullptr;
  xTaskCreate(recoveryDeadline, "diagnostic-deadline", 4096, nullptr, 2, nullptr);
  checkpoint("BOOT", "Flying Thumb active hardware diagnostic v2");
  checkpoint("SCHEMA", "2");
  appendReport("FTDIAG|" + String(millis()) + "|RESET_REASON|" + String(static_cast<int>(esp_reset_reason())));
  testSystem();
  testCoresAndCrypto();
  testSettingsAndFlash();
  checkpoint("BLE_CONTROLLER", btStartMode(BT_MODE_BLE) && btStarted() ? "PASS|Controller initialized" : "FAIL|Controller initialization failed");
  btStop();
  checkpoint("BLE_RADIO", "UNVERIFIED|Over-the-air receive/transmit requires a BLE peer");

  pinMode(PIN_BUTTON, INPUT_PULLUP);
  checkpoint("BUTTON", digitalRead(PIN_BUTTON) == LOW ? "PRESSED" : "RELEASED");

  checkpoint("GPIO_TEST_BEGIN", "LED data and clock pins");
  testPinLevels(PIN_LED_DATA, "LED_DATA_GPIO40");
  testPinLevels(PIN_LED_CLOCK, "LED_CLOCK_GPIO39");
  testCrossShort(PIN_LED_DATA, PIN_LED_CLOCK, "DATA40", "CLOCK39");
  testCrossShort(PIN_LED_CLOCK, PIN_LED_DATA, "CLOCK39", "DATA40");
  checkpoint("GPIO_TEST_COMPLETE");

  checkpoint("LED_INIT_BEGIN", "APA102 data=40 clock=39");
  FastLED.addLeds<APA102, PIN_LED_DATA, PIN_LED_CLOCK, BGR>(&diagnosticLed, 1);
  FastLED.setBrightness(32);
  checkpoint("LED_INIT_COMPLETE", "PASS");
  showColor("RED", CRGB::Red);
  showColor("GREEN", CRGB::Green);
  showColor("BLUE", CRGB::Blue);
  showColor("WHITE", CRGB::White);
  showColor("OFF", CRGB::Black);
  checkpoint("LED_VISIBLE_OUTPUT", "UNVERIFIED_NO_LIGHT_SENSOR");

  checkpoint("WIFI_INIT_BEGIN");
  WiFi.mode(WIFI_STA);
  const int networks = WiFi.scanNetworks(false, true);
  if (networks < 0) checkpoint("WIFI_SCAN", "FAIL");
  else {
    appendReport("FTDIAG|" + String(millis()) + "|WIFI_SCAN|PASS|NETWORKS=" + String(networks));
    for (int i = 0; i < min(networks, 5); ++i)
      appendReport("FTDIAG|" + String(millis()) + "|WIFI_NETWORK|RSSI=" + String(WiFi.RSSI(i)) + "|CHANNEL=" + String(WiFi.channel(i)));
  }
  WiFi.scanDelete();
  testSavedNetwork();
  WiFi.mode(WIFI_AP);
  const bool ap = WiFi.softAP("FlyingThumb-Hardware-Test", "flyingthumb");
  checkpoint("WIFI_AP", ap ? "PASS" : "FAIL");
  if (ap) appendReport("FTDIAG|" + String(millis()) + "|WIFI_AP_IP|" + WiFi.softAPIP().toString());

  checkpoint("SD_INIT_BEGIN", "clk=12 cmd=16 d0=14 d1=17 d2=21 d3=18");
  SD_MMC.setPins(PIN_SD_CLK, PIN_SD_CMD, PIN_SD_D0, PIN_SD_D1, PIN_SD_D2, PIN_SD_D3);
  const bool card = SD_MMC.begin("/sdcard", false);
  if (!card) checkpoint("SD_INIT", "FAIL");
  else {
    char line[150];
    snprintf(line, sizeof(line), "FTDIAG|%lu|SD_INIT|PASS|TYPE=%u|SIZE=%llu", millis(), static_cast<unsigned>(SD_MMC.cardType()), SD_MMC.cardSize());
    appendReport(line);
    testCardGeometry();
    uint8_t sector[512];
    const bool raw = SD_MMC.sectorSize() == 512 && SD_MMC.readRAW(sector, 0);
    checkpoint("SD_SECTOR0", raw && sector[510] == 0x55 && sector[511] == 0xaa ? "PASS|MBR or FAT boot signature readable" : "FAIL|Sector unreadable or boot signature absent");
    const uint32_t sectors = SD_MMC.numSectors();
    bool sampleOk = sectors > 0;
    const uint32_t positions[] = {0, sectors / 4, sectors / 2, sectors ? sectors - 1 : 0};
    for (uint32_t position : positions) {
      sampleOk &= SD_MMC.readRAW(sector, position);
    }
    checkpoint("SD_RAW_SAMPLES", sampleOk ? "PASS|First quarter middle last sectors readable" : "FAIL");
    testCardFiles();
  }
  SD_MMC.end();
  const bool oneBit = SD_MMC.begin("/sdcard", true, false);
  checkpoint("SD_ONE_BIT", oneBit ? "PASS|CLK CMD D0 transport" : "FAIL|One-bit initialization failed");
  SD_MMC.end();
  checkpoint("HEAP_AFTER", heap_caps_check_integrity_all(true) ? "PASS" : "FAIL");
  checkpoint("DIAGNOSTIC_COMPLETE", "Results saved; returning to USB recovery");
  const bool saved = resultPartition && storedBytes == report.length();
  if (!saved) checkpoint("RESULT_SAVE", "FAIL");
  delay(500);
  diagnosticFinished = true;
  returnToRecovery();
  lastButton = digitalRead(PIN_BUTTON) == LOW;
}

void loop() {
  const bool button = digitalRead(PIN_BUTTON) == LOW;
  if (button != lastButton) {
    lastButton = button;
    checkpoint("BUTTON_CHANGE", button ? "PRESSED" : "RELEASED");
  }
  if (millis() - lastHeartbeat >= 2000) {
    lastHeartbeat = millis();
    checkpoint("HEARTBEAT", "RUNNING");
  }
  delay(10);
}
