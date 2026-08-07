#pragma once
#include <Arduino.h>
constexpr int PIN_BUTTON=0, PIN_TFT_RST=1, PIN_TFT_DC=2, PIN_TFT_MOSI=3, PIN_TFT_CS=4;
constexpr int PIN_TFT_SCLK=5, PIN_TFT_BL=38;
constexpr int PIN_SD_CLK=12, PIN_SD_CMD=16, PIN_SD_D0=14, PIN_SD_D1=17, PIN_SD_D2=21, PIN_SD_D3=18;
constexpr int PIN_LED_DATA=39, PIN_LED_CLOCK=40;
constexpr uint32_t RESET_HOLD_MS=5000, DEBOUNCE_MS=50, STA_CONNECT_TIMEOUT_MS=15000;
constexpr char SETUP_PASSWORD[]="flyingthumb";
