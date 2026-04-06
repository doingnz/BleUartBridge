/**
 * Packet format (matches BleUartBridgeTester C# and ESP32 firmware):
 *   [seq : 4 bytes LE uint32]
 *   [len : 2 bytes LE uint16]   (seq % 100) + 8  → 8–107 bytes
 *   [payload : len bytes]       payload[i] = (seq + i) & 0xFF
 *   [crc : 1 byte]              CRC-8/SMBUS over seq+len+payload
 *   [pad : 1 byte = 0x00]
 */

const PKT_HEADER   = 6;    // seq(4) + len(2)
const PKT_TRAILER  = 2;    // crc(1) + pad(1)
const PKT_MAX_PLD  = 240;  // parser rejects len > this

// ── Build ─────────────────────────────────────────────────────────────────────

function buildPacket(seq) {
    seq = seq >>> 0;  // ensure uint32
    const payloadLen = (seq % 100) + 8;
    const total = PKT_HEADER + payloadLen + PKT_TRAILER;
    const buf = new Uint8Array(total);
    const view = new DataView(buf.buffer);

    view.setUint32(0, seq, true);
    view.setUint16(4, payloadLen, true);
    for (let i = 0; i < payloadLen; i++) buf[PKT_HEADER + i] = (seq + i) & 0xFF;
    buf[PKT_HEADER + payloadLen]     = crc8Smbus(buf.subarray(0, PKT_HEADER + payloadLen));
    buf[PKT_HEADER + payloadLen + 1] = 0x00;
    return buf;
}

function buildExpectedPayload(seq) {
    seq = seq >>> 0;
    const len = (seq % 100) + 8;
    const p = new Uint8Array(len);
    for (let i = 0; i < len; i++) p[i] = (seq + i) & 0xFF;
    return p;
}

function validatePayload(seq, payload) {
    seq = seq >>> 0;
    const expected = (seq % 100) + 8;
    if (payload.length !== expected) return false;
    for (let i = 0; i < payload.length; i++) {
        if (payload[i] !== ((seq + i) & 0xFF)) return false;
    }
    return true;
}

// ── Hex formatter ─────────────────────────────────────────────────────────────

function _esc(s) { return s.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;'); }

function _hexRow(data, i, W, diffRef) {
    const RED = 'color:#DC2626;font-weight:700';
    let hex = '', asc = '';
    for (let j = 0; j < W; j++) {
        const idx = i + j;
        if (idx < data.length) {
            const b   = data[idx];
            const bad = diffRef && idx < diffRef.length && b !== diffRef[idx];
            const h   = b.toString(16).padStart(2, '0').toUpperCase();
            const a   = _esc((b >= 0x20 && b <= 0x7E) ? String.fromCharCode(b) : '.');
            if (bad) { hex += `<span style="${RED}">${h}</span> `; asc += `<span style="${RED}">${a}</span>`; }
            else     { hex += h + ' ';                              asc += a; }
            if (j === 7) hex += ' ';
        } else {
            hex += '   ';
            if (j === 7) hex += ' ';
        }
    }
    return i.toString(16).padStart(4, '0').toUpperCase() + '  ' + hex + ' ' + asc + '\n';
}

/** Plain hex dump — returns HTML-safe string (use as innerHTML). */
function formatHex(data) {
    if (!data || data.length === 0) return '(empty)';
    const W = 16;
    let out = '';
    for (let i = 0; i < data.length; i += W) out += _hexRow(data, i, W, null);
    return out;
}

/**
 * Hex dump with differing bytes coloured red.
 * @param {Uint8Array} data      — bytes to display
 * @param {Uint8Array} reference — reference bytes to compare against
 */
function formatHexDiff(data, reference) {
    if (!data || data.length === 0) return '(empty)';
    const W = 16;
    let out = '';
    for (let i = 0; i < data.length; i += W) out += _hexRow(data, i, W, reference);
    return out;
}

// ── StreamPacketParser ────────────────────────────────────────────────────────

class StreamPacketParser {
    constructor() {
        this._buf = [];
        /** @type {function(number, Uint8Array): void} */
        this.onPacket    = null;   // (seq, payload)
        /** @type {function(Uint8Array): void} */
        this.onSyncError = null;   // (snapshot up to 128 B)
    }

    /** Feed raw bytes into the parser. */
    feed(data) {
        for (const b of data) this._buf.push(b);
        this._parse();
    }

    reset() { this._buf = []; }

    get bufferSize() { return this._buf.length; }

    _parse() {
        while (this._buf.length >= PKT_HEADER) {
            // Read length field without allocating a full slice
            const len = this._buf[4] | (this._buf[5] << 8);

            if (len > PKT_MAX_PLD) {
                const snap = new Uint8Array(this._buf.slice(0, Math.min(this._buf.length, 128)));
                this._buf.shift();
                if (this.onSyncError) this.onSyncError(snap);
                continue;
            }

            const total = PKT_HEADER + len + PKT_TRAILER;
            if (this._buf.length < total) break;

            const raw = new Uint8Array(this._buf.slice(0, total));
            const expectedCrc = crc8Smbus(raw.subarray(0, PKT_HEADER + len));
            const receivedCrc = raw[PKT_HEADER + len];

            if (expectedCrc === receivedCrc) {
                const view = new DataView(raw.buffer);
                const seq     = view.getUint32(0, true);
                const payload = raw.slice(PKT_HEADER, PKT_HEADER + len);
                this._buf.splice(0, total);
                if (this.onPacket) this.onPacket(seq, payload);
            } else {
                const snap = new Uint8Array(this._buf.slice(0, Math.min(this._buf.length, 128)));
                this._buf.shift();
                if (this.onSyncError) this.onSyncError(snap);
            }
        }
    }
}
