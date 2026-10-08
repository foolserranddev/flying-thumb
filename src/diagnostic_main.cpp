#include <Arduino.h>
#include <FastLED.h>
#include <SD_MMC.h>
#include <WiFi.h>
#include <esp_partition.h>
#include <esp_system.h>
#include "esp32-hal-tinyusb.h"
#include "board_config.h"

namespace {
CRGB diagnosticLed;
uint32_t lastHeartbeat = 0;
bool lastButton = false;
String report;

void appendReport(const String &line) {
  report += line;
  report += '\n';
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
}

void setup() {
  Serial.begin(115200);
  const uint32_t consoleWaitStarted = millis();
  while (!Serial && millis() - consoleWaitStarted < 60000) delay(25);
  delay(250);
  checkpoint("BOOT", "Flying Thumb active hardware diagnostic v1");
  appendReport("FTDIAG|" + String(millis()) + "|RESET_REASON|" + String(static_cast<int>(esp_reset_reason())));

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
  }
  checkpoint("DIAGNOSTIC_COMPLETE", "Results saved; returning to USB recovery");
  const bool saved = saveReport();
  if (!saved) checkpoint("RESULT_SAVE", "FAIL");
  delay(500);
  usb_persist_restart(RESTART_BOOTLOADER);
  ESP.restart();
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
