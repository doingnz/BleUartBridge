/**
 * BleNusClient — Web Bluetooth Nordic UART Service (NUS) peripheral client.
 *
 * Service : 6E400001-B5A3-F393-E0A9-E50E24DCCA9E
 * RX char : 6E400002  (write → ESP32 UART TX)
 * TX char : 6E400003  (notify ← ESP32 UART RX)
 */
class BleNusClient {
    static SERVICE = '6e400001-b5a3-f393-e0a9-e50e24dcca9e';
    static RX_CHAR = '6e400002-b5a3-f393-e0a9-e50e24dcca9e';  // we write to this
    static TX_CHAR = '6e400003-b5a3-f393-e0a9-e50e24dcca9e';  // we receive from this

    constructor() {
        this._device       = null;
        this._writeChar    = null;
        this._connected    = false;
        this._notifyQueue  = Promise.resolve();   // serialises onData callbacks

        /** @type {function(Uint8Array): Promise<void>|void} */
        this.onData       = null;
        /** @type {function(): void} */
        this.onConnect    = null;
        /** @type {function(): void} */
        this.onDisconnect = null;
    }

    get isConnected() { return this._connected; }

    get isAvailable() { return 'bluetooth' in navigator; }

    /**
     * Open the browser BLE picker, connect to GATT, subscribe to TX notifications.
     * Calls this.onConnect() on success.
     */
    async connect() {
        this._device = await navigator.bluetooth.requestDevice({
            filters: [
                { services: [BleNusClient.SERVICE] },
                { namePrefix: 'BP+' },
                { name: 'BP+ Bridge' },
                { name: 'NUS Bridge' },
            ],
            optionalServices: [BleNusClient.SERVICE],
        });

        this._device.addEventListener('gattserverdisconnected', () => {
            this._connected = false;
            this._writeChar = null;
            if (this.onDisconnect) this.onDisconnect();
        });

        const server  = await this._device.gatt.connect();
        const service = await server.getPrimaryService(BleNusClient.SERVICE);

        this._writeChar = await service.getCharacteristic(BleNusClient.RX_CHAR);

        const notifyChar = await service.getCharacteristic(BleNusClient.TX_CHAR);
        await notifyChar.startNotifications();
        // Notifications are delivered as browser events — multiple can queue up
        // while an async onData callback is sleeping (simulated delay).  Chain
        // each notification onto the previous one so onData is never re-entered
        // concurrently, matching the sequential semantics of the WebSerial loop.
        notifyChar.addEventListener('characteristicvaluechanged', (e) => {
            const data = new Uint8Array(e.target.value.buffer);
            this._notifyQueue = this._notifyQueue.then(
                () => this.onData ? this.onData(data) : undefined
            );
        });

        this._connected = true;
        if (this.onConnect) this.onConnect();
    }

    async disconnect() {
        this._notifyQueue = Promise.resolve();   // drop any queued callbacks
        if (this._device && this._device.gatt.connected) {
            this._device.gatt.disconnect();
        }
        this._connected = false;
        this._writeChar = null;
    }

    /**
     * Write data to the NUS RX characteristic (ESP32 receives, forwards to UART).
     *
     * Uses writeValueWithResponse (ATT Write with acknowledgment) so that each
     * send() call awaits the ESP32's ATT response before returning.  This provides
     * backpressure at the BLE layer: the send loop can only submit one write per
     * round-trip, keeping the Windows BLE driver queue empty.
     *
     * writeValueWithoutResponse was previously used here.  With that approach the
     * driver accepts writes far faster than the ESP32 can consume them (~8 KB/s at
     * a 15 ms connection interval), building up a multi-megabyte OS-level queue.
     * Disconnecting then flushes that queue over the air before the GAP disconnect
     * event fires, causing the ESP32 to continue dropping bytes for minutes.
     *
     * @param {Uint8Array} data
     */
    async send(data) {
        if (!this._writeChar) throw new Error('BLE not connected');
        if (this._writeChar.writeValueWithResponse) {
            await this._writeChar.writeValueWithResponse(data);
        } else {
            await this._writeChar.writeValue(data);   // older browsers
        }
    }
}
