"""Bundled recovery helper with a bounded, identity-checked watchdog boot reset."""
import sys
import argparse
import time
import subprocess
import os
import ctypes
import uuid
import shutil
import esptool
import serial
from serial.tools import list_ports
from esptool.targets.esp32s3 import ESP32S3ROM

def volume_roots(mac):
    serial_number = mac.replace(":", "").upper()
    if len(serial_number) != 12 or any(c not in "0123456789ABCDEF" for c in serial_number):
        raise RuntimeError("Invalid device identity")
    script = "$ErrorActionPreference='Stop'; Get-Disk | Where-Object {$_.BusType -eq 'USB' -and $_.SerialNumber.Trim() -eq '" + serial_number + "'} | Get-Partition | Where-Object DriveLetter | ForEach-Object { $_.DriveLetter }"
    result = subprocess.run(["powershell.exe", "-NoProfile", "-NonInteractive", "-Command", script], capture_output=True, text=True, timeout=20, creationflags=subprocess.CREATE_NO_WINDOW)
    if result.returncode:
        raise RuntimeError("Cannot identify USB storage volume: " + result.stderr.strip())
    return [line.strip() + ":\\" for line in result.stdout.splitlines() if len(line.strip()) == 1 and line.strip().isalpha()]

def lock_volumes(mac):
    kernel = ctypes.WinDLL("kernel32", use_last_error=True)
    kernel.CreateFileW.restype = ctypes.c_void_p
    kernel.CreateFileW.argtypes = [ctypes.c_wchar_p,ctypes.c_uint32,ctypes.c_uint32,ctypes.c_void_p,ctypes.c_uint32,ctypes.c_uint32,ctypes.c_void_p]
    kernel.DeviceIoControl.argtypes = [ctypes.c_void_p,ctypes.c_uint32,ctypes.c_void_p,ctypes.c_uint32,ctypes.c_void_p,ctypes.c_uint32,ctypes.c_void_p,ctypes.c_void_p]
    kernel.CloseHandle.argtypes = [ctypes.c_void_p]
    handles = []
    try:
        for root in volume_roots(mac):
            handle = kernel.CreateFileW("\\\\.\\" + root[:2], 0xC0000000, 3, None, 3, 0, None)
            if handle == ctypes.c_void_p(-1).value:
                raise RuntimeError("Cannot open USB volume for safe diagnostic transition")
            handles.append(handle)
            returned = ctypes.c_uint32()
            for code in (0x90018, 0x90020): # Lock (flushes cached data), then dismount.
                if not kernel.DeviceIoControl(handle,code,None,0,None,0,ctypes.byref(returned),None):
                    raise RuntimeError("USB volume is in use; close files and finish Windows copies before diagnostics")
        return kernel, handles
    except:
        for handle in handles: kernel.CloseHandle(handle)
        raise

def main():
    if "--flyingthumb-helper-version" in sys.argv:
        print("FLYINGTHUMB_HELPER_PROTOCOL=3")
        return
    if "--flyingthumb-volume-test" in sys.argv:
        parser = argparse.ArgumentParser()
        parser.add_argument("--mac", required=True)
        parser.add_argument("--flyingthumb-volume-test", action="store_true")
        args = parser.parse_args()
        roots = volume_roots(args.mac)
        if not roots:
            print("FTDIAG|0|USB_STORAGE_HOST|SKIP|No mounted volume for this device identity")
            return
        for root in roots:
            folder = os.path.join(root,".FlyingThumb-diagnostic-" + uuid.uuid4().hex)
            try:
                os.makedirs(os.path.join(folder,"Nested folder"))
                path = os.path.join(folder,"Nested folder","Test file.bin")
                payload = os.urandom(65536)
                with open(path,"xb") as stream:
                    stream.write(payload)
                    stream.flush()
                    os.fsync(stream.fileno())
                with open(path,"rb") as stream:
                    if stream.read() != payload: raise RuntimeError("USB file readback mismatch")
                os.rename(path,path+".renamed")
                os.remove(path+".renamed")
                print("FTDIAG|0|USB_STORAGE_HOST|PASS|64 KiB write/read/rename/delete and nested folder")
            except Exception as error:
                print("FTDIAG|0|USB_STORAGE_HOST|FAIL|" + str(error))
            finally:
                try:
                    if os.path.isdir(folder): shutil.rmtree(folder)
                except Exception as error:
                    print("FTDIAG|0|USB_STORAGE_CLEANUP|FAIL|" + folder + ": " + str(error))
        return
    if "--flyingthumb-find-usb" in sys.argv:
        # Query only Espressif USB interfaces, without DTR/RTS boot toggles.
        matches = []
        for port in list_ports.comports():
            if port.vid != 0x303A:
                continue
            try:
                with serial.Serial(port=None, baudrate=115200, timeout=0.2, write_timeout=1) as console:
                    console.dtr = True
                    console.rts = False
                    console.port = port.device
                    console.open()
                    console.write(b"\nFTUSB ID\n")
                    deadline = time.monotonic() + 2
                    while time.monotonic() < deadline:
                        line = console.readline().decode("ascii", errors="ignore").strip()
                        if line.startswith("FTUSB|ID|"):
                            matches.append((port.device, line.split("|")[2].lower()))
                            status = console.readline().decode("ascii", errors="ignore").strip()
                            if status.startswith("FTUSB|STORAGE|"): print(port.device + " " + status)
                            break
            except (serial.SerialException, OSError):
                pass
        for port, mac in matches:
            print("FTUSB|FOUND|" + port + "|" + mac)
        return
    if "--flyingthumb-usb-recovery" in sys.argv:
        parser = argparse.ArgumentParser()
        parser.add_argument("--port", required=True)
        parser.add_argument("--mac", required=True)
        parser.add_argument("--flyingthumb-usb-recovery", action="store_true")
        args = parser.parse_args()
        with serial.Serial(port=None, baudrate=115200, timeout=0.2, write_timeout=1) as console:
            console.dtr = True
            console.rts = False
            console.port = args.port
            console.open()
            console.write(b"\nFTUSB ID\n")
            deadline = time.monotonic() + 3
            identified = False
            while time.monotonic() < deadline:
                if console.readline().decode("ascii", errors="ignore").strip().lower() == "ftusb|id|" + args.mac.lower():
                    identified = True
                    break
            if not identified:
                raise RuntimeError("Flying Thumb identity not confirmed; no reset requested")
            kernel, handles = lock_volumes(args.mac)
            try:
                time.sleep(2.1) # Allow firmware's last-write safety interval to elapse.
                console.write(b"FTUSB RECOVERY\n")
                deadline = time.monotonic() + 3
                while time.monotonic() < deadline:
                    line = console.readline().decode("ascii", errors="ignore").strip()
                    if line == "FTUSB|RECOVERY|OK":
                        print(line)
                        return
                    if line == "FTUSB|RECOVERY|BUSY":
                        raise RuntimeError("USB transfer is active; finish copying files before diagnostics")
                raise RuntimeError("Drive did not acknowledge recovery request")
            finally:
                for handle in handles: kernel.CloseHandle(handle)
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
