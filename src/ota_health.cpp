#include "ota_health.h"
#include <Arduino.h>
#include <Preferences.h>
#include <esp_ota_ops.h>

// Override Arduino's immediate OTA acceptance. Flying Thumb validates after its
// network, display, and storage initialization has completed instead.
extern "C" bool verifyRollbackLater() { return true; }

void rememberOtaRequirements(bool storageWasReady)
{
    Preferences prefs;
    prefs.begin("ota-health", false);
    prefs.putBool("need-card", storageWasReady);
    prefs.end();
}

void finishOtaHealthCheck(bool storageReady)
{
    const esp_partition_t *running = esp_ota_get_running_partition();
    esp_ota_img_states_t state;
    if (!running || esp_ota_get_state_partition(running, &state) != ESP_OK || state != ESP_OTA_IMG_PENDING_VERIFY) return;

    Preferences prefs;
    prefs.begin("ota-health", false);
    const bool cardRequired = prefs.getBool("need-card", false);
    prefs.clear();
    prefs.end();

    if (cardRequired && !storageReady)
    {
        delay(500);
        esp_ota_mark_app_invalid_rollback_and_reboot();
    }
    esp_ota_mark_app_valid_cancel_rollback();
}