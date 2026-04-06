/**
 * SerialPortClient — unified serial port adapter.
 *
 * Desktop Chrome (Windows / Mac): uses the WebSerial API.
 * Android Chrome: WebSerial is not yet available; falls back to WebUSB
 *   with a PL2303 USB-to-serial adapter (vendorId 0x067B).
 *
 * Both implementations expose the same interface:
 *   connect(options)  — opens the browser picker and connects
 *   disconnect()
 *   send(Uint8Array)  — returns Promise, awaitable for backpressure
 *   onData            — async callback(Uint8Array), awaited by read loop
 *   onConnect / onDisconnect
 */

// ─── WebSerial implementation (desktop) ──────────────────────────────────────

class WebSerialImpl {
    constructor(options) {
        this._options = options;
        this._port    = null;
        this._reader  = null;
        this._writer  = null;
        this.onData       = null;
        this.onDisconnect = null;
    }

    async connect() {
        this._port = await navigator.serial.requestPort();
        await this._port.open({
            baudRate:    this._options.baudRate    || 115200,
            dataBits:    this._options.dataBits    || 8,
            parity:      this._options.parity      || 'none',
            stopBits:    this._options.stopBits    || 1,
            flowControl: this._options.flowControl ? 'hardware' : 'none',
        });
        this._writer = this._port.writable.getWriter();
        this._startReading();   // fire-and-forget read loop
    }

    async disconnect() {
        try { if (this._reader) await this._reader.cancel(); } catch {}
        try { if (this._writer) this._writer.releaseLock(); } catch {}
        try { if (this._port)   await this._port.close();   } catch {}
        this._reader = this._writer = this._port = null;
    }

    async send(data) {
        // writer.write() awaits until OS serial buffer accepts the bytes.
        // When CTS is deasserted (flow control), the write blocks — natural backpressure.
        await this._writer.write(data);
    }

    async _startReading() {
        const reader = this._port.readable.getReader();
        this._reader = reader;
        let errorDisconnect = false;
        try {
            while (true) {
                const { value, done } = await reader.read();
                if (done) break;
                // Await onData so that a simulated delay blocks this loop,
                // preventing further reads — fills OS buffer → RTS asserts.
                if (this.onData) await this.onData(value);
            }
        } catch {
            errorDisconnect = true;
        } finally {
            reader.releaseLock();
            this._reader = null;
            if (errorDisconnect) {
                // Close the port so it can be reopened on reconnect.
                // Writer lock must be released before close() is called.
                try { if (this._writer) { this._writer.releaseLock(); this._writer = null; } } catch {}
                try { if (this._port)   { await this._port.close();   this._port   = null; } } catch {}
                if (this.onDisconnect) this.onDisconnect();
            }
        }
    }
}

// ─── WebUSB PL2303 implementation (Android) ──────────────────────────────────
// Adapted from D:\BPplus\JavaScript\bpconnect\js\bpplus-webserial.js.
// Supports PL2303/PL2303HX (productId 0x2303) and PL2303GT (0x23A3).
// The read loop awaits onData so simulated delays create true backpressure
// by holding the bulk transferIn, letting the PL2303 FIFO fill.

class WebUsbPl2303Impl {
    constructor(options) {
        this._options     = options;
        this._device      = null;
        this._endpointIn  = null;
        this._endpointOut = null;
        this._chipType    = 'legacy';
        this._running     = false;
        this.onData       = null;
        this.onDisconnect = null;
    }

    async connect() {
        this._device = await navigator.usb.requestDevice({
            filters: [
                { vendorId: 0x067B, productId: 0x2303  },  // PL2303 / PL2303HX
                { vendorId: 0x067B, productId: 0x23A3  },  // PL2303GT
            ],
        });

        await this._device.open();
        await this._device.selectConfiguration(1);
        await this._device.claimInterface(0);

        // Discover bulk IN/OUT endpoints from the vendor-class interface
        for (const iface of this._device.configuration.interfaces) {
            for (const alt of iface.alternates) {
                if (alt.interfaceClass !== 0xff) continue;
                for (const ep of alt.endpoints) {
                    if (ep.direction === 'out' && ep.type === 'bulk') this._endpointOut = ep.endpointNumber;
                    if (ep.direction === 'in'  && ep.type === 'bulk') this._endpointIn  = ep.endpointNumber;
                }
            }
        }

        this._chipType = (this._device.productId === 0x23A3) ? 'GT' : 'legacy';
        await this._pl2303Init();
        await this._setLineCoding(
            this._options.baudRate  || 115200,
            this._options.dataBits  || 8,
            this._options.parity    || 'none',
            this._options.stopBits  || 1,
        );
        await this._setControlLineState(true, true);  // assert DTR + RTS

        this._running = true;
        this._readLoop();  // fire-and-forget
    }

    async disconnect() {
        this._running = false;
        try { await this._setControlLineState(false, false); } catch {}
        try { await this._device.releaseInterface(0); }       catch {}
        try { await this._device.close(); }                   catch {}
        this._device = null;
    }

    async send(data) {
        await this._device.transferOut(this._endpointOut, data);
    }

    async _readLoop() {
        while (this._running) {
            try {
                // 256-byte buffer — fits our max packet (115 B) plus some margin
                const result = await this._device.transferIn(this._endpointIn, 256);
                if (result.data && result.data.byteLength > 0) {
                    const data = new Uint8Array(result.data.buffer);
                    // Await onData: a simulated delay holds this loop, letting
                    // the PL2303 FIFO fill and hardware flow control assert.
                    if (this.onData) await this.onData(data);
                }
            } catch (err) {
                if (this._running) {
                    console.error('PL2303 read error:', err);
                    this._running = false;
                    try { await this._device.releaseInterface(0); } catch {}
                    try { await this._device.close(); this._device = null; } catch {}
                    if (this.onDisconnect) this.onDisconnect();
                }
                break;
            }
        }
    }

    // ── PL2303 chip initialisation ─────────────────────────────────────────

    async _pl2303Init() {
        if (this._chipType === 'GT') {
            // PL2303GT: two register clears before standard CDC commands
            await this._vendorWrite(0x08, 0);
            await this._vendorWrite(0x09, 0);
        } else {
            // PL2303 / PL2303HX (TYPE_01) — mirrors Linux pl2303 kernel driver
            await this._vendorRead(0x8484, 0);
            await this._vendorWrite(0x0404, 0);
            await this._vendorRead(0x8484, 0);
            await this._vendorRead(0x8383, 0);
            await this._vendorRead(0x8484, 0);
            await this._vendorWrite(0x0404, 1);
            await this._vendorRead(0x8484, 0);
            await this._vendorRead(0x8383, 0);
            await this._vendorWrite(0, 1);
            await this._vendorWrite(1, 0);
            await this._vendorWrite(2, 0x44);  // enable RTS/CTS flow control bits
        }
    }

    /** CDC SET_LINE_CODING — baud rate, stop bits, parity, data bits. */
    async _setLineCoding(baudRate, dataBits, parity, stopBits) {
        const stopMap   = { 1: 0, 1.5: 1, 2: 2 };
        const parityMap = { none: 0, odd: 1, even: 2, mark: 3, space: 4 };
        const buf  = new ArrayBuffer(7);
        const view = new DataView(buf);
        view.setUint32(0, baudRate, true);
        view.setUint8(4, stopMap[stopBits]   ?? 0);
        view.setUint8(5, parityMap[parity]   ?? 0);
        view.setUint8(6, dataBits            ?? 8);
        await this._device.controlTransferOut(
            { requestType: 'class', recipient: 'interface', request: 0x20, value: 0, index: 0 },
            buf);
    }

    /** CDC SET_CONTROL_LINE_STATE — assert/deassert DTR and RTS. */
    async _setControlLineState(dtr, rts) {
        const value = (dtr ? 0x01 : 0) | (rts ? 0x02 : 0);
        await this._device.controlTransferOut(
            { requestType: 'class', recipient: 'interface', request: 0x22, value, index: 0 });
    }

    async _vendorRead(value, index) {
        return this._device.controlTransferIn(
            { requestType: 'vendor', recipient: 'device', request: 0x01, value, index }, 1);
    }

    async _vendorWrite(value, index) {
        return this._device.controlTransferOut(
            { requestType: 'vendor', recipient: 'device', request: 0x01, value, index });
    }
}

// ─── Public adapter ───────────────────────────────────────────────────────────

class SerialPortClient {
    constructor() {
        this._impl    = null;
        this._connected = false;

        /** @type {function(Uint8Array): Promise<void>|void} */
        this.onData       = null;
        /** @type {function(): void} */
        this.onConnect    = null;
        /** @type {function(): void} */
        this.onDisconnect = null;
    }

    get isConnected() { return this._connected; }

    /** True if any serial API is available in this browser. */
    get isAvailable() { return ('serial' in navigator) || ('usb' in navigator); }

    /** True when running in a desktop Chrome with WebSerial. */
    get isWebSerial() { return 'serial' in navigator; }

    /**
     * @param {object} options
     * @param {number}  options.baudRate
     * @param {number}  options.dataBits
     * @param {string}  options.parity      'none'|'even'|'odd'
     * @param {number}  options.stopBits    1|1.5|2
     * @param {boolean} options.flowControl
     */
    async connect(options = {}) {
        // Tear down any stale impl (e.g. read loop errored without explicit disconnect).
        // This ensures the port is closed before we try to open it again.
        if (this._impl) {
            await this._impl.disconnect();
            this._impl = null;
        }
        this._impl = this.isWebSerial
            ? new WebSerialImpl(options)
            : new WebUsbPl2303Impl(options);

        this._impl.onData = async (data) => {
            if (this.onData) await this.onData(data);
        };
        this._impl.onDisconnect = () => {
            this._connected = false;
            if (this.onDisconnect) this.onDisconnect();
        };

        await this._impl.connect();
        this._connected = true;
        if (this.onConnect) this.onConnect();
    }

    async disconnect() {
        if (this._impl) {
            await this._impl.disconnect();
            this._impl = null;
        }
        this._connected = false;
    }

    async send(data) {
        if (!this._impl) throw new Error('Serial not connected');
        await this._impl.send(data);
    }
}
