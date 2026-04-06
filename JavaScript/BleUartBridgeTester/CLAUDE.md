# BleUartBridgeTester — JavaScript / Web Bluetooth

## Purpose

Vanilla JS single-page app for testing the BleUartBridge ESP32 firmware from a browser. Uses the Web Bluetooth API to connect to the NUS service on the bridge.

## Architecture

No build step, no framework, no bundler — plain ES modules loaded via `<script type="module">`.

| File | Role |
|------|------|
| `js/ble-nus.js` | Web Bluetooth connection, GATT service/characteristic discovery, NUS RX write and TX notify subscribe |
| `js/packet.js` | Packet framing for the BP+ serial protocol |
| `js/crc8-smbus.js` | CRC-8/SMBUS checksum |
| `js/serial-port.js` | Web Serial fallback / abstraction |
| `js/app.js` | UI event wiring, connects the modules together |
| `index.html` | Single HTML page, imports `app.js` |

## Web Bluetooth constraints

- Requires HTTPS or `localhost` — will silently fail on plain HTTP.
- `requestDevice()` must be called from a user gesture (button click).
- NUS service UUID must be in the `filters` or `optionalServices` list passed to `requestDevice()`.
- Notifications arrive on the main thread; no special marshalling needed.
