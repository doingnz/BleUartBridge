# BleUartBridge — Monorepo

## Overview

Three-component system for testing and using the BLE-to-UART bridge firmware.

| Project | Path | Language / Platform |
|---------|------|---------------------|
| ESP-IDF firmware | `Espressive/BleUartBridge/` | C, ESP-IDF v6.0, ESP32 / ESP32-S3 |
| MAUI tester | `MAUI/BleUartBridgeTester/` | C#, .NET MAUI 10, Windows / Android |
| Web tester | `JavaScript/BleUartBridgeTester/` | Vanilla JS, Web Bluetooth API |

Each subdirectory has its own `CLAUDE.md` with project-specific context.

## Repository layout

```
BleUartBridge/
├── Espressive/BleUartBridge/   ESP-IDF firmware
├── MAUI/BleUartBridgeTester/   .NET MAUI test app
└── JavaScript/BleUartBridgeTester/  Web Bluetooth test page
```

## BLE protocol (shared across all three)

The ESP32 firmware exposes a Nordic UART Service (NUS):

| Role | UUID |
|------|------|
| Service | `6E400001-B5A3-F393-E0A9-E50E24DCCA9E` |
| RX (client → ESP32) | `6E400002-B5A3-F393-E0A9-E50E24DCCA9E` |
| TX (ESP32 → client) | `6E400003-B5A3-F393-E0A9-E50E24DCCA9E` |

Device advertises as `"BP+ Bridge XXXX"` where `XXXX` is the last 4 hex digits of the BLE MAC address.
