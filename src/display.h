#pragma once

enum class DeviceStatus {
  Starting,
  SetupAccessPoint,
  Connecting,
  WpsSearching,
  Connected,
  WifiOffline,
  Fault
};

void initDisplay();
void displayMessage(const char *title, const char *line1, const char *line2);
void setDeviceStatus(DeviceStatus status);
void setActivityLed(bool reading, bool writing);
bool wakeDisplay();
void handleDisplayPower();
void handleStatusLed();
void showDiagnosticLed(unsigned char red, unsigned char green, unsigned char blue);
