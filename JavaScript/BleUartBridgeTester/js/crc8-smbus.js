/**
 * CRC-8/SMBUS
 * Polynomial : 0x07  (x^8 + x^2 + x + 1)
 * Init       : 0x00
 * RefIn/Out  : false
 * XorOut     : 0x00
 *
 * Matches the C# Crc8.Compute() used in BleUartBridgeTester.
 */
function crc8Smbus(data) {
    let crc = 0x00;
    for (let i = 0; i < data.length; i++) {
        crc ^= data[i];
        for (let j = 0; j < 8; j++) {
            crc = (crc & 0x80) ? (((crc << 1) ^ 0x07) & 0xFF) : ((crc << 1) & 0xFF);
        }
    }
    return crc;
}
