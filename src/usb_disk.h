#pragma once

bool beginUsbFileUpdate();
bool finishUsbFileUpdate();
bool usbFileUpdateActive();
bool usbManagedModeActive();
bool releaseUsbManagedMode();
bool prepareUsbRecovery();
void enterUsbRecovery();
