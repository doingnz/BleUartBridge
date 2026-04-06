# BleUartBridgeTester — Web Bluetooth

A single-page Web Bluetooth test app for the BleUartBridge firmware. Runs directly in Chrome or Edge — no install required.

## Requirements

- Chrome or Edge (Web Bluetooth API required)
- Must be served over HTTPS or `localhost` (Web Bluetooth is blocked on plain HTTP)

## Usage

1. Open `index.html` via a local web server or `localhost`.
2. Click **Connect** to scan for a device advertising as `"BP+ Bridge"`.
3. Type commands or data in the send field; received UART data appears in the log.

## Quick local server

```bash
# Python 3
python -m http.server 8080
# then open http://localhost:8080
```

## File structure

```
BleUartBridgeTester/
├── index.html          Main UI
├── css/
│   └── app.css         Styles
└── js/
    ├── app.js          UI logic, event wiring
    ├── ble-nus.js      Web Bluetooth NUS connection wrapper
    ├── crc8-smbus.js   CRC-8 utility
    ├── packet.js       Packet framing
    └── serial-port.js  Serial port abstraction
```
