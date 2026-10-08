"""Bundled recovery helper with a bounded, identity-checked watchdog boot reset."""
import sys
import argparse
import time
import esptool
from esptool.targets.esp32s3 import ESP32S3ROM

def main():
    if "--flyingthumb-boot-reset" not in sys.argv:
        esptool.main()
        return
    parser = argparse.ArgumentParser()
    parser.add_argument("--port", required=True)
    parser.add_argument("--mac", required=True)
    parser.add_argument("--flyingthumb-boot-reset", action="store_true")
    args, _ = parser.parse_known_args()
    chip = ESP32S3ROM(args.port)
    try:
        chip.connect("no_reset")
        chip = chip.run_stub()
        actual = ":".join(f"{value:02x}" for value in chip.read_mac())
        if actual != args.mac.lower():
            raise RuntimeError("Device identity mismatch; reset not performed")
        chip.write_reg(0x6000812C, 0, 1)
        chip.write_reg(0x600080B0, 0x50D83AA1)
        chip.write_reg(0x6000809C, 2000)
        chip.write_reg(0x60008098, (1 << 31) | (5 << 28) | (1 << 8) | 2)
        chip.write_reg(0x600080B0, 0)
        time.sleep(0.5)
        print("Watchdog boot reset requested for", actual)
    finally:
        chip._port.close()

if __name__ == "__main__":
    main()
