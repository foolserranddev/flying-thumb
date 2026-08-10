#include "display.h"
#include <Arduino.h>
#include <FastLED.h>
#include "board_config.h"
#include "bsp_lcd/esp_lcd_st7735.h"
#include "esp_lcd_panel_io.h"
#include "esp_lcd_panel_ops.h"
#include "esp_lcd_panel_vendor.h"
#include "esp_heap_caps.h"

namespace {
constexpr int LCD_W = 160;
constexpr int LCD_H = 80;
constexpr spi_host_device_t LCD_HOST = SPI2_HOST;
esp_lcd_panel_handle_t panel = nullptr;
esp_lcd_panel_io_handle_t panelIo = nullptr;
uint16_t *frame = nullptr;
CRGB led;
bool displayAwake = false;
uint32_t displayTouchedAt = 0;

constexpr char glyphKeys[] = " 0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ.-:/_?";
constexpr uint8_t glyphs[][5] = {
  {0x00,0x00,0x00,0x00,0x00},
  {0x3E,0x51,0x49,0x45,0x3E},{0x00,0x42,0x7F,0x40,0x00},{0x42,0x61,0x51,0x49,0x46},
  {0x21,0x41,0x45,0x4B,0x31},{0x18,0x14,0x12,0x7F,0x10},{0x27,0x45,0x45,0x45,0x39},
  {0x3C,0x4A,0x49,0x49,0x30},{0x01,0x71,0x09,0x05,0x03},{0x36,0x49,0x49,0x49,0x36},
  {0x06,0x49,0x49,0x29,0x1E},
  {0x7E,0x11,0x11,0x11,0x7E},{0x7F,0x49,0x49,0x49,0x36},{0x3E,0x41,0x41,0x41,0x22},
  {0x7F,0x41,0x41,0x22,0x1C},{0x7F,0x49,0x49,0x49,0x41},{0x7F,0x09,0x09,0x09,0x01},
  {0x3E,0x41,0x49,0x49,0x7A},{0x7F,0x08,0x08,0x08,0x7F},{0x00,0x41,0x7F,0x41,0x00},
  {0x20,0x40,0x41,0x3F,0x01},{0x7F,0x08,0x14,0x22,0x41},{0x7F,0x40,0x40,0x40,0x40},
  {0x7F,0x02,0x0C,0x02,0x7F},{0x7F,0x04,0x08,0x10,0x7F},{0x3E,0x41,0x41,0x41,0x3E},
  {0x7F,0x09,0x09,0x09,0x06},{0x3E,0x41,0x51,0x21,0x5E},{0x7F,0x09,0x19,0x29,0x46},
  {0x46,0x49,0x49,0x49,0x31},{0x01,0x01,0x7F,0x01,0x01},{0x3F,0x40,0x40,0x40,0x3F},
  {0x1F,0x20,0x40,0x20,0x1F},{0x3F,0x40,0x38,0x40,0x3F},{0x63,0x14,0x08,0x14,0x63},
  {0x07,0x08,0x70,0x08,0x07},{0x61,0x51,0x49,0x45,0x43},
  {0x00,0x60,0x60,0x00,0x00},{0x08,0x08,0x08,0x08,0x08},{0x00,0x36,0x36,0x00,0x00},
  {0x20,0x10,0x08,0x04,0x02},{0x40,0x40,0x40,0x40,0x40},{0x02,0x01,0x51,0x09,0x06}
};

uint16_t panelColor(uint16_t rgb565) { return static_cast<uint16_t>((rgb565 << 8) | (rgb565 >> 8)); }
void clear(uint16_t color) { if (frame) std::fill_n(frame, LCD_W * LCD_H, panelColor(color)); }
void pixel(int x, int y, uint16_t color) {
  if (frame && x >= 0 && x < LCD_W && y >= 0 && y < LCD_H) frame[y * LCD_W + x] = panelColor(color);
}
const uint8_t *glyphFor(char c) {
  if (c >= 'a' && c <= 'z') c -= 32;
  const char *p = strchr(glyphKeys, c);
  return p ? glyphs[p - glyphKeys] : glyphs[sizeof(glyphs) / sizeof(glyphs[0]) - 1];
}
void drawChar(int x, int y, char c, int scale, uint16_t color) {
  const uint8_t *g = glyphFor(c);
  for (int col = 0; col < 5; ++col) for (int row = 0; row < 7; ++row)
    if (g[col] & (1 << row)) for (int dx = 0; dx < scale; ++dx) for (int dy = 0; dy < scale; ++dy)
      pixel(x + col * scale + dx, y + row * scale + dy, color);
}
void centered(const char *text, int y, int scale, uint16_t color) {
  if (!text) return;
  const int maxChars = LCD_W / (6 * scale);
  String shown(text); if (shown.length() > maxChars) shown = shown.substring(0, maxChars);
  int x = (LCD_W - static_cast<int>(shown.length()) * 6 * scale + scale) / 2;
  for (char c : shown) { drawChar(x, y, c, scale, color); x += 6 * scale; }
}
void present() { if (panel && frame) { esp_lcd_panel_draw_bitmap(panel, 0, 0, LCD_W, LCD_H, frame); delay(20); } }
}

void initDisplay() {
  frame = static_cast<uint16_t *>(heap_caps_malloc(LCD_W * LCD_H * sizeof(uint16_t), MALLOC_CAP_INTERNAL | MALLOC_CAP_8BIT));
  if (!frame) abort();
  pinMode(PIN_TFT_BL, OUTPUT); digitalWrite(PIN_TFT_BL, HIGH);
  spi_bus_config_t bus = ST7735_PANEL_BUS_SPI_CONFIG(PIN_TFT_SCLK, PIN_TFT_MOSI, LCD_W * LCD_H * sizeof(uint16_t));
  ESP_ERROR_CHECK(spi_bus_initialize(LCD_HOST, &bus, SPI_DMA_CH_AUTO));
  esp_lcd_panel_io_spi_config_t io = ST7735_PANEL_IO_SPI_CONFIG(PIN_TFT_CS, PIN_TFT_DC, nullptr, nullptr);
  ESP_ERROR_CHECK(esp_lcd_new_panel_io_spi((esp_lcd_spi_bus_handle_t)LCD_HOST, &io, &panelIo));
  esp_lcd_panel_dev_config_t config = {};
  config.reset_gpio_num = PIN_TFT_RST;
  config.color_space = static_cast<decltype(config.color_space)>(ESP_LCD_COLOR_SPACE_BGR);
  config.bits_per_pixel = 16;
  ESP_ERROR_CHECK(esp_lcd_new_panel_st7735(panelIo, &config, &panel));
  ESP_ERROR_CHECK(esp_lcd_panel_reset(panel));
  ESP_ERROR_CHECK(esp_lcd_panel_init(panel));
  ESP_ERROR_CHECK(esp_lcd_panel_invert_color(panel, true));
  ESP_ERROR_CHECK(esp_lcd_panel_set_gap(panel, 1, 26));
  ESP_ERROR_CHECK(esp_lcd_panel_swap_xy(panel, true));
  ESP_ERROR_CHECK(esp_lcd_panel_mirror(panel, false, true));
  ESP_ERROR_CHECK(esp_lcd_panel_disp_on_off(panel, true));
  digitalWrite(PIN_TFT_BL, LOW);
  clear(0x07FF); present(); delay(350);
  displayAwake = true; displayTouchedAt = millis();
  FastLED.addLeds<APA102, PIN_LED_DATA, PIN_LED_CLOCK, BGR>(&led, 1); FastLED.setBrightness(24);
}


void displayMessage(const char *title, const char *line1, const char *line2) {
  wakeDisplay();
  clear(0x0000);
  int titleScale = title && strlen(title) <= 13 ? 2 : 1;
  centered(title, 8, titleScale, 0x07FF);
  centered(line1, 44, 1, 0xFFFF);
  centered(line2, 62, 1, 0xFFFF);
  present();
}

bool wakeDisplay() {
  const bool wasOff = !displayAwake;
  displayTouchedAt = millis();
  if (wasOff) {
    if (panel) esp_lcd_panel_disp_on_off(panel, true);
    digitalWrite(PIN_TFT_BL, LOW);
    present();
    displayAwake = true;
  }
  return wasOff;
}

void handleDisplayPower() {
  if (!displayAwake || millis() - displayTouchedAt < DISPLAY_IDLE_MS) return;
  digitalWrite(PIN_TFT_BL, HIGH);
  if (panel) esp_lcd_panel_disp_on_off(panel, false);
  displayAwake = false;
}
void setActivityLed(bool reading, bool writing) {
  static uint8_t old = 0xff; uint8_t state = (reading ? 1 : 0) | (writing ? 2 : 0); if (state == old) return; old = state;
  led = writing ? (reading ? CRGB::Yellow : CRGB::Red) : (reading ? CRGB::Green : CRGB::Blue); FastLED.show();
}