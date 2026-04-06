# BleUartBridgeTester — .NET MAUI

A .NET MAUI test app for the BleUartBridge firmware. Connects to the ESP32/ESP32-S3 bridge via BLE and provides a terminal-style interface for sending and receiving data over the bridged UART.

## Platforms

- Windows (WinUI 3)
- Android

## Requirements

- Visual Studio 2022 with the .NET MAUI workload
- .NET 10

## Build

Open `BleUartBridgeTester.sln` in Visual Studio and select the target platform (Windows Machine or an Android device/emulator).

## Usage

1. Power on the ESP32 bridge. It will advertise as `"BP+ Bridge XXXX"`.
2. Launch the app and tap **Connect** to scan and connect.
3. Type commands or data in the send field; received UART data appears in the log.
