#!/usr/bin/env python3
"""
USB Sensor terminal test script.
Kullanim: python test_sensor.py [PORT] [BAUDRATE] [KOMUT]
Ornek:    python test_sensor.py COM3 115200 OKU_1
"""
import sys
import serial
import time

PORT    = sys.argv[1] if len(sys.argv) > 1 else "COM3"
BAUD    = int(sys.argv[2]) if len(sys.argv) > 2 else 115200
COMMAND = sys.argv[3] if len(sys.argv) > 3 else "OKU_1"

print(f"Port   : {PORT}")
print(f"Baud   : {BAUD}")
print(f"Komut  : {COMMAND!r}")
print("-" * 40)

try:
    ser = serial.Serial(PORT, BAUD, timeout=5)
    print(f"Port acildi: {ser.name}")
    time.sleep(2)          # Arduino reset sonrasi hazirlanma suresi

    print(f"Gonderiliyor -> {COMMAND!r}")
    ser.write((COMMAND + "\n").encode("utf-8"))

    print("Yanit bekleniyor (5 sn)...")
    response = ser.readline()
    print(f"Ham yanit  : {response}")
    print(f"Metin      : {response.decode('utf-8', errors='replace').strip()!r}")
    ser.close()

except serial.SerialException as e:
    print(f"HATA: {e}")
except Exception as e:
    print(f"BEKLENMEYEN HATA: {e}")
