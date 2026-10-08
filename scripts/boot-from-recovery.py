"""ESP32-S3 watchdog boot reset using the installed esptool transport.

Register sequence matches Espressif esptool ESP32S3ROM.watchdog_reset.
Does not write flash or eFuses.
"""
import argparse
import time
from esptool.targets.esp32s3 import ESP32S3ROM

parser = argparse.ArgumentParser()
parser.add_argument("--port", required=True)
parser.add_argument("--mac", required=True)
args = parser.parse_args()
rom = ESP32S3ROM(args.port)
rom.connect("no_reset")
chip = rom.run_stub()
actual = ":".join(f"{value:02x}" for value in chip.read_mac())
if actual != args.mac.lower():
    raise RuntimeError("Device identity mismatch; reset not performed")
chip.write_reg(0x6000812C, 0, 1)
chip.write_reg(0x600080B0, 0x50D83AA1)
chip.write_reg(0x6000809C, 2000)
chip.write_reg(0x60008098, (1 << 31) | (5 << 28) | (1 << 8) | 2)
chip.write_reg(0x600080B0, 0)
time.sleep(0.5)
chip._port.close()
print("Watchdog boot reset requested for", actual)
