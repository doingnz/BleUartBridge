# BPplus Serial API Design Reference

> **Purpose:** This document distils the Uscom BP+ Serial Command Specification (BPPLUS-RS-SerialCommand Rev C, 2024-03-08) into a concise engineering reference for implementing `BPplus.Serial.dll` and its MAUI test application. It covers the wire protocol, the proposed C# assembly design, and integration patterns.

---

## 1. Wire Protocol

### 1.1 Physical Layer

| Parameter    | Value                                                               |
|---|---|
| Connector    | DTE RS-232 female (null-modem cable or USB-to-Serial adapter)       |
| Default baud | **115 200** (older devices default to 9 600)                        |
| Encoding     | ASCII                                                               |
| Flow control | Hardware (null-modem with flow control) or none over USB-to-Serial  |

> Recommended baud: **115 200** — the XML result payload can be several kilobytes, so a low baud rate causes noticeable delay.

### 1.2 Frame Format

All commands and responses share the same envelope:

```
<typ> <param(s)>\r\n
```

- `<typ>` — single ASCII character. **Lower-case = command** sent by the host. **Upper-case = response** sent by the device.
- A single space separates `<typ>` from parameters.
- Frame is terminated by `\r\n` (CR LF, ASCII 13 10).
- Commands with no parameters are sent as just `<typ>\r\n`.

---

### 1.3 Commands

| Cmd | Parameters | Description |
|---|---|---|
| `?` | — | Query Terminal API version. |
| `!` | — | Check whether a measurement is in progress. |
| `m` | — | Request current device mode (v1.2+). |
| `f` | — | Request feature list as XML. |
| `y` | — | Get device date/time. |
| `y` | `YYYYMMDDHHmmSS ` | Set device date/time (trailing space required). |
| `d` | `<level>` | Set reporting detail level. **Use `d 4`** before every measurement for XML results. Level resets to 0 after each measurement on older firmware (CardioScope ≤ 037). |
| `s` | — or `<target>,<patientID>,<i\|d\|4>` | Start Adult Mode measurement. `target` is NIBP inflation pressure (mmHg). Valid values for TM2917: 80, 100, 120, 140, 160, 180, 200, 220, 240, 280. `0` or omitted = Auto. `patientID` is an optional string, saved as `<PatientID>` in the XML. Third param: `i` = inflate mode, `d` = deflate @ 6 mmHg/s, `4` = slow deflate @ 4 mmHg/s. |
| `c` | — | Cancel a measurement in progress. |
| `r` | — or `<index>` | Retrieve a stored measurement by index. |
| `i` | — or `<startIndex>` | Retrieve up to 100 stored measurement index numbers. `0` or omitted = highest 100. `1` = lowest 100. Results are sorted ascending. |
| `o` | `<sys>,<map>,<dia>,<pr>` | Suprasystolic-only measurement using supplied NIBP values (integers, all 4 required). |
| `t` | — | Enter pressure test mode. Device closes valves and streams cuff pressure continuously. Exit with `c`. |
| `b` | `<baud>` | Set baud rate. Valid: 9600 or 115200. Takes effect immediately — host must reconnect at new baud. |
| `q` | — | Reboot device. No response. Device emits `M 00` on restart. |
| `h` | — | Pressure hold (reserved, not implemented). |
| `v` | — | BP-only measurement (reserved, not implemented). |
| `l` | — | Dump Log (reserved, not implemented). |
| `x` | — | Test Math (reserved, not implemented). |
| `u` | — | Leak test (reserved, not implemented). |
| `a` | — | Analogue test (reserved, not implemented). |

---

### 1.4 Responses

Most responses follow the `<TYP> <params>\r\n` envelope, but **several responses do not conform** — they have no leading type character and must be matched by prefix instead. These are marked ⚠ in the table below.

| Response | Parameters | Description |
|---|---|---|
| ⚠ `ver`_N_`.`_M_ | *(none — the token is the entire line)* | Response to `?`. The device sends the bare string `ver2.3\r\n` with **no leading type character**. Match by `line.StartsWith("ver", OrdinalIgnoreCase)`. |
| `M` | `<code:nn>` | Response to `m` or **Unsolicited.** Device mode notification. Sent whenever the device changes state — during boot, on menu navigation, at measurement start/end. See Mode Codes below. |
| `P` / `p` | `<cuffp:nnn>` | **Unsolicited.** Cuff pressure update in mmHg during BP measurement (modes M 03, M 04) and inflating for suprasystolic (M 05). Also streams continuously in pressure test mode. The original spec lists this as lower-case `p`; the device has been observed to send upper-case `P`. Parse both. |
| ⚠ `D` | `<level>` | Acknowledgement echo for the `d` command. The device echoes `D 4\r\n` after accepting a detail-level change. **Not documented in the original spec.** Match by `line[0] == 'D' && line[1] == ' '`. |
| `S` | (multi-field) | Measurement success. Format depends on detail level (see §1.5). |
| `F` | `<code:nn>` | Measurement failure or command error. See Failure Codes below. |
| `E` | `<msg:string>` | Textual error. Always followed by `F nn`. Applications should log `E` messages for diagnostics but use the subsequent `F nn` for programmatic error handling. |
| `T` | `<datetime>` | Response to `y` (get time). Format mirrors the set-time command. |
| ⚠ `<Feature` | `version="1.0\|2.0">…</Feature>` | Response to `f`. The entire line is XML starting with `<Feature` — **no leading type character**. Match by `line.StartsWith("<Feature", OrdinalIgnoreCase)`. |
| ⚠ `IDs_H` | `<len> <crc8>` followed by `IDs_Content <id> <id> …` | Response to `i`. Two-line response; header starts with the multi-character token `IDs_H` — **no single-character prefix**. See §1.6. |

> **Parsing note:** The spec states "upper case letters are used for responses" but this rule has three confirmed exceptions — `ver…`, `<Feature…`, and `IDs_H…`. Parsers must check these full-string prefixes before falling back to the single-character type dispatch.

#### Mode Codes (`M nn`)

| Code | `DeviceMode` enum | Meaning |
|---|---|---|
| 0 | `Initial` | Boot / self-test starting |
| 1 | `Offline` | Device offline |
| 2 | `Ready` | Ready / idle |
| 3 | `MeasuringBp` | Measuring BP (NIBP phase) |
| 4 | `DeflatingCuff` | Deflating cuff |
| 5 | `InflatingToSs` | Inflating to suprasystolic |
| 6 | `AcquireData` | Acquiring waveform data |
| 7 | `ProcessData` | Processing measurement data |
| 8 | `SelectStorage` | Store / recall selection menu |
| 9 | `StoreRecall` | Store / recall active |
| 10 | `ExtraInfo` | Extra information screen |
| 11 | `SafetyLock` | Safety lock active |
| 12 | `ServiceMenu` | Service menu |
| 13 | `ServiceMenuManometer` | Service menu — manometer |
| 14 | `ServiceCharacterisingSys` | Service menu — characterising systolic |
| 15 | `SetTarget` | Set target pressure menu |
| 16 | `DownloadApp` | Firmware download / app update |
| 17 | `Settings` | Settings menu |
| 18 | `SelectLanguage` | Language selection menu |
| 19 | `SetDatetime` | Date / time setting menu |
| 20 | `CentralPress` | Central pressure mode |
| 21 | `MeasurePressureTest` | Pressure test mode (entered via `t` command; exit with `c` → returns to `Ready`) |
| 22 | `CountDownAobp` | AOBP countdown |
| 23 | `SelectAobpMode` | AOBP mode selection |

#### Failure Codes (`F nn`)

| Code | Meaning |
|---|---|
| 0 | Failed to start measurement |
| 1 | Timeout |
| 2 | Measurement was cancelled |
| 3 | Pneumatic error |
| 4 | Data processing error |
| 5 | Failed to process suprasystolic data |
| 6 | Error finding feature points |
| 7 | Failed to calculate central BP parameters |
| 8 | Failed to finish measurement |
| 9 | No measurement found |
| 10 | Measurement did not complete in permitted time |
| 11 | NIBP device error (and retry limit reached) |
| 12 | Measurement data invalid |
| 13 | BP out of range |
| 14 | Measurement serial error |
| 15 | Failed self-test |
| 17 | Device is busy |
| 18 | Timeout or connection error *(host-side simulation)* |
| 19 | Data receiving error *(host-side simulation)* |
| 20 | Data receiving timeout *(host-side simulation)* |
| 21 | Invalid port name *(host-side simulation)* |
| 22 | No measurement in progress |
| 23 | Failed safety test |
| 24 | Invalid date/time |
| 25 | Invalid patient mode |
| 26 | Invalid baud rate |

---

### 1.5 Measurement Success Response (`S`)

**Detail level 0 (default):**

```
S <ID> <SNR> <Sys> <Map> <Dia> <Pr> <cSys> <cMap> <cDia> <sPR> <sPRV>
  <sAI> <sPPV> <sSEP> <RWTTpeak> <RWTTfoot> <sDpDtMax>\r\n
```

**Detail level 4 (recommended — always set `d 4` before measurement):**

The XML result is preceded by a header line:

```
|_XML_Size<NNNNN> <CCC>_|\r\n
<?xml version="1.0" encoding="utf-8" ?>
<bpplus version="1.0">
  …
</bpplus>\r\n
```

- `NNNNN` = byte count of the XML string, **excluding** the final `\r\n`.
- `CCC` = CRC-8 (decimal) calculated over all ASCII bytes of the XML string.
- Older devices may use `<CardioScope>` as the root element instead of `<BPplus>`.
- The `IDs_H` response for `i` uses the same CRC-8.

**CRC-8 implementation** (from spec, unchanged):

```csharp
public static class Crc8
{
    private const byte Init = 0xFF;
    private static readonly byte[] Lut = {
        0x00,0x91,0x61,0xF0,0xC2,0x53,0xA3,0x32,0xC7,0x56,0xA6,0x37,0x05,0x94,0x64,0xF5,
        0xCD,0x5C,0xAC,0x3D,0x0F,0x9E,0x6E,0xFF,0x0A,0x9B,0x6B,0xFA,0xC8,0x59,0xA9,0x38,
        0xD9,0x48,0xB8,0x29,0x1B,0x8A,0x7A,0xEB,0x1E,0x8F,0x7F,0xEE,0xDC,0x4D,0xBD,0x2C,
        0x14,0x85,0x75,0xE4,0xD6,0x47,0xB7,0x26,0xD3,0x42,0xB2,0x23,0x11,0x80,0x70,0xE1,
        0xF1,0x60,0x90,0x01,0x33,0xA2,0x52,0xC3,0x36,0xA7,0x57,0xC6,0xF4,0x65,0x95,0x04,
        0x3C,0xAD,0x5D,0xCC,0xFE,0x6F,0x9F,0x0E,0xFB,0x6A,0x9A,0x0B,0x39,0xA8,0x58,0xC9,
        0x28,0xB9,0x49,0xD8,0xEA,0x7B,0x8B,0x1A,0xEF,0x7E,0x8E,0x1F,0x2D,0xBC,0x4C,0xDD,
        0xE5,0x74,0x84,0x15,0x27,0xB6,0x46,0xD7,0x22,0xB3,0x43,0xD2,0xE0,0x71,0x81,0x10,
        0xA1,0x30,0xC0,0x51,0x63,0xF2,0x02,0x93,0x66,0xF7,0x07,0x96,0xA4,0x35,0xC5,0x54,
        0x6C,0xFD,0x0D,0x9C,0xAE,0x3F,0xCF,0x5E,0xAB,0x3A,0xCA,0x5B,0x69,0xF8,0x08,0x99,
        0x78,0xE9,0x19,0x88,0xBA,0x2B,0xDB,0x4A,0xBF,0x2E,0xDE,0x4F,0x7D,0xEC,0x1C,0x8D,
        0xB5,0x24,0xD4,0x45,0x77,0xE6,0x16,0x87,0x72,0xE3,0x13,0x82,0xB0,0x21,0xD1,0x40,
        0x50,0xC1,0x31,0xA0,0x92,0x03,0xF3,0x62,0x97,0x06,0xF6,0x67,0x55,0xC4,0x34,0xA5,
        0x9D,0x0C,0xFC,0x6D,0x5F,0xCE,0x3E,0xAF,0x5A,0xCB,0x3B,0xAA,0x98,0x09,0xF9,0x68,
        0x89,0x18,0xE8,0x79,0x4B,0xDA,0x2A,0xBB,0x4E,0xDF,0x2F,0xBE,0x8C,0x1D,0xED,0x7C,
        0x44,0xD5,0x25,0xB4,0x86,0x17,0xE7,0x76,0x83,0x12,0xE2,0x73,0x41,0xD0,0x20,0xB1
    };

    public static byte Compute(ReadOnlySpan<byte> data)
    {
        byte val = Init;
        foreach (var b in data)
            val = Lut[b ^ val];
        return val;
    }
}
```

---

### 1.6 Stored Measurement Index Response (`IDs_H`)

```
IDs_H <len> <crc8>\r\n
IDs_Content <id0> <id1> … <idN>\r\n
```

- `len` = byte count of `IDs_Content <ids>\r\n` (the second line, inclusive).
- `crc8` = CRC-8 over those same bytes.
- IDs are listed **highest first** (most recent measurement first).
- Max 100 IDs per call. Use `i <startIndex>` for pagination: `i 0` = highest 100, `i 1` = lowest 100.

---

## 2. `BPplus.Serial` Assembly Design

### 2.1 Target and Dependencies

| Item | Value |
|---|---|
| Target framework | `net8.0` (or `netstandard2.1`) — consumed by MAUI (net10.0) and any other host |
| Serial port | `System.IO.Ports.SerialPort` (NuGet `System.IO.Ports`) |
| Threading | `System.Threading.Channels`, `System.Threading`, `System.Threading.Tasks` |
| No UI dependency | The assembly must not reference MAUI or WinUI packages |

---

### 2.2 Message Model

All incoming lines are parsed into a discriminated union. Using a base record with derived types keeps the switch pattern clean:

```csharp
namespace BPplus.Serial;

// Base type — every parsed line from the device
public abstract record BpPlusMessage(string RawLine);

// Unsolicited
public record ModeMessage(int Code, string RawLine) : BpPlusMessage(RawLine);
public record PressureMessage(int CuffPressureMmHg, string RawLine) : BpPlusMessage(RawLine);

// Command responses
public record VersionMessage(string Version, string RawLine) : BpPlusMessage(RawLine);
public record FeatureMessage(string FeatureXml, string RawLine) : BpPlusMessage(RawLine);
public record DeviceTimeMessage(DateTime DeviceTime, string RawLine) : BpPlusMessage(RawLine);
public record MeasurementSuccessMessage(string Xml, bool XmlCrcValid, string RawLine) : BpPlusMessage(RawLine);
public record MeasurementFailureMessage(int Code, string Description, string RawLine) : BpPlusMessage(RawLine);
public record ErrorMessage(string Text, string RawLine) : BpPlusMessage(RawLine);
public record MeasurementIdListMessage(IReadOnlyList<int> Ids, bool CrcValid, string RawLine) : BpPlusMessage(RawLine);
public record AcknowledgeMessage(string RawLine) : BpPlusMessage(RawLine);   // generic OK / echo
public record UnknownMessage(string RawLine) : BpPlusMessage(RawLine);
```

---

### 2.3 Core Interface: `IBpPlusSerialClient`

```csharp
namespace BPplus.Serial;

/// <summary>
/// Async serial interface to a Uscom BP+ device.
/// All methods are thread-safe. Only one command may be in-flight at a time;
/// concurrent callers wait their turn via an internal semaphore.
/// </summary>
public interface IBpPlusSerialClient : IAsyncDisposable
{
    // ── Connection ────────────────────────────────────────────────────────────

    Task ConnectAsync(string portName, int baudRate = 115200,
                      CancellationToken ct = default);

    Task DisconnectAsync(CancellationToken ct = default);

    bool IsConnected { get; }

    // ── Unsolicited notification streams ──────────────────────────────────────

    /// Fires whenever the device sends M nn (mode change, boot, menu navigation).
    event EventHandler<ModeChangedEventArgs>       ModeChanged;

    /// Fires whenever the device sends a cuff pressure update (P nnn / p nnn).
    /// Raised during active measurements AND in pressure-test mode.
    event EventHandler<PressureUpdatedEventArgs>   PressureUpdated;

    /// Fires for any device message that is not consumed as a command response,
    /// including E (error strings) and unknown/unexpected frames.
    event EventHandler<UnsolicitedMessageEventArgs> UnsolicitedMessage;

    // ── Device info commands ──────────────────────────────────────────────────

    Task<string>      GetVersionAsync      (CancellationToken ct = default);
    Task<FeatureInfo> GetFeaturesAsync     (CancellationToken ct = default);
    Task<DateTime>    GetDeviceTimeAsync   (CancellationToken ct = default);
    Task              SetDeviceTimeAsync   (DateTime time, CancellationToken ct = default);
    Task<int>         GetCurrentModeAsync  (CancellationToken ct = default);
    Task<bool>        IsMeasurementInProgressAsync(CancellationToken ct = default);

    // ── Measurement configuration ─────────────────────────────────────────────

    /// Must be called with level 4 before every measurement to receive XML results.
    Task SetDetailLevelAsync(int level, CancellationToken ct = default);

    // ── Measurement commands ──────────────────────────────────────────────────

    /// Starts an Adult Mode measurement.
    /// <param name="request">Measurement parameters (target pressure, patient ID, etc.).</param>
    /// <param name="cuffPressureProgress">
    ///   Optional. Receives cuff pressure updates (mmHg) while the measurement runs.
    ///   Invoked on the calling synchronisation context.
    /// </param>
    /// <param name="ct">
    ///   Cancellation sends 'c' to the device. The task resolves with
    ///   <see cref="MeasurementResult.Cancelled"/> or throws <see cref="OperationCanceledException"/>.
    /// </param>
    Task<MeasurementResult> StartMeasurementAsync(
        MeasurementRequest request,
        IProgress<int>?    cuffPressureProgress = null,
        CancellationToken  ct = default);

    /// Cancels any in-progress measurement immediately.
    Task CancelMeasurementAsync(CancellationToken ct = default);

    /// Retrieves a stored measurement by index.
    Task<MeasurementResult> RetrieveMeasurementAsync(int index,
                                                     CancellationToken ct = default);

    /// Returns available stored measurement IDs (paged, up to 100 per call).
    Task<IReadOnlyList<int>> GetStoredMeasurementIdsAsync(int startIndex = 0,
                                                          CancellationToken ct = default);

    /// Suprasystolic-only measurement using caller-supplied NIBP values.
    Task<MeasurementResult> StartSuprasystolicOnlyAsync(
        int sys, int map, int dia, int pr,
        CancellationToken ct = default);

    // ── Streaming modes ───────────────────────────────────────────────────────

    /// Enters pressure-test mode and streams live cuff pressure values.
    /// Cancel the token to send 'c' and exit pressure-test mode.
    IAsyncEnumerable<int> StreamPressureTestAsync(CancellationToken ct = default);

    // ── Device control ────────────────────────────────────────────────────────

    /// Sets baud rate. Existing connection is closed; caller must reconnect.
    Task SetBaudRateAsync(int baudRate, CancellationToken ct = default);

    Task RebootAsync(CancellationToken ct = default);

    // ── Escape hatch (test app) ───────────────────────────────────────────────

    /// Sends a raw ASCII command and returns the first response line (or all
    /// lines up to a known terminator). Use for exploratory testing only.
    Task<string> SendRawCommandAsync(string command,
                                     TimeSpan    timeout,
                                     CancellationToken ct = default);
}
```

#### Supporting Types

```csharp
public sealed record MeasurementRequest(
    int     TargetPressureMmHg = 0,          // 0 = Auto
    string? PatientId          = null,
    MeasureMode Mode           = MeasureMode.Auto
);

public enum MeasureMode { Auto, Inflate, Deflate, SlowDeflate }

public sealed record MeasurementResult(
    bool   IsSuccess,
    int    FailureCode,                      // 0 if success
    string FailureDescription,
    string? Xml,                             // non-null when IsSuccess && detail level 4
    bool   XmlCrcValid
)
{
    public bool IsCancelled  => FailureCode == 2;
    public static MeasurementResult Cancelled =>
        new(false, 2, "Measurement was cancelled", null, false);
}

public sealed record FeatureInfo(
    string Version,
    string ProtocolXmlVersion,
    string? FirmwareVersion,
    string? SoftwareVersion,
    string? HardwareId,
    string? DeviceId,
    string? NibpType,
    string? NibpVersion,
    string RawXml
);

public sealed class ModeChangedEventArgs(int code) : EventArgs
{
    public int Code { get; } = code;
    public string Description => code switch {
        0  => "Boot/self-test",
        2  => "Ready",
        3  => "Measuring BP",
        4  => "Deflating cuff",
        5  => "Inflating to suprasystolic",
        _  => $"Mode {code}"
    };
}

public sealed class PressureUpdatedEventArgs(int pressureMmHg) : EventArgs
{
    public int PressureMmHg { get; } = pressureMmHg;
}

public sealed class UnsolicitedMessageEventArgs(BpPlusMessage message) : EventArgs
{
    public BpPlusMessage Message { get; } = message;
}
```

---

### 2.4 Timeout and Cancellation

**Policy:** every public method accepts a `CancellationToken`. Callers control timeout via `CancellationTokenSource`:

```csharp
// Short command — 5-second timeout
using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
var version = await client.GetVersionAsync(cts.Token);

// Long-running measurement — explicit generous timeout + user-cancel
using var userCts   = new CancellationTokenSource();           // UI "Stop" button
using var timerCts  = new CancellationTokenSource(TimeSpan.FromMinutes(10));
using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
                          userCts.Token, timerCts.Token);
var result = await client.StartMeasurementAsync(request, progress, linkedCts.Token);
```

**Internal behaviour when the token is cancelled during a measurement:**
1. The assembly sends `c\r\n` to the device.
2. Awaits `F 02` (cancelled) or a short acknowledgment timeout.
3. If the `F 02` arrives, the `Task<MeasurementResult>` resolves normally with `result.IsCancelled == true`.
4. If the device does not respond within ~3 s, the task throws `OperationCanceledException`.

This two-stage cancel (soft → hard) preserves device state more cleanly than just closing the port.

---

### 2.5 Unsolicited Notification Pattern

The device sends `M nn` and `P nnn` at any time — before, during, and after commands. The recommended pattern is:

- **Events** — chosen over `IObservable<T>` to avoid a Reactive Extensions dependency in the core assembly. MAUI ViewModels simply subscribe on the `MainThread` dispatcher.
- Events are always raised on a **background thread** (the internal read loop thread). MAUI consumers must marshal to the UI thread themselves (see §4).
- An application that needs richer stream composition can wrap the events in `Observable.FromEventPattern` with Rx.NET independently.

**Why not `Channel<T>` exposed directly?**  A `Channel` is an excellent internal primitive, but exposing it as the public API forces consumers to run their own consumer loops. Events are simpler for the expected consumer (a MAUI ViewModel).

---

### 2.6 Internal Architecture

```
┌────────────────────────────────────────────────────────┐
│  BpPlusSerialClient (internal)                         │
│                                                        │
│  ┌──────────────┐   raw lines   ┌──────────────────┐   │
│  │ SerialPort   │ ─────────────►│ LineParser       │   │
│  │ ReadLine loop│               │ (parses frames   │   │
│  └──────────────┘               │  → BpPlusMessage)│   │
│                                 └────────┬─────────┘   │
│                                          │             │
│                              ┌───────────▼──────────┐  │
│                              │ MessageRouter        │  │
│                              │                      │  │
│                              │ pending TCS? ──►TCS  │  │
│                              │ M / P ────────►Events│  │
│                              │ other ────────►Events│  │
│                              └──────────────────────┘  │
│                                                        │
│  SemaphoreSlim(1,1) serialises all SendCommandAsync    │
│  calls so one command is in-flight at a time.          │
└────────────────────────────────────────────────────────┘
```

**Key implementation notes:**

- The `LineParser` must buffer incomplete lines between `DataReceived` events.
- The XML response arrives over multiple lines. The parser accumulates lines after detecting `|_XML_Size…` until the byte count is satisfied, then emits a single `MeasurementSuccessMessage`.
- The `IDs_H` response similarly spans two lines (`IDs_H …` header + `IDs_Content …` body).
- The `MessageRouter` holds an `Optional<(TaskCompletionSource<BpPlusMessage>, Predicate<BpPlusMessage>)>`. When a response message satisfies the predicate, the TCS is completed; otherwise the message is broadcast as an unsolicited event.
- `M nn` and `P nnn` messages are **always** broadcast as events even if a command is pending — they are not command responses.
- The internal read loop must not throw on parse errors; malformed lines emit `UnknownMessage` and continue.

---

### 2.7 Error Handling Strategy

| Scenario | Behaviour |
|---|---|
| Device sends `E` before `F nn` | Log via `UnsolicitedMessage` event; wait for the following `F nn` to resolve the command task. |
| `F nn` arrives with no pending command | Broadcast as `UnsolicitedMessage`. |
| `CancellationToken` fires | Send `c`, await `F 02`; see §2.4. |
| Serial port disconnected mid-operation | Pending TCS faults with `IOException`; `IsConnected` becomes `false`; `UnsolicitedMessage` event fires with the exception detail. |
| CRC mismatch on XML | `MeasurementSuccessMessage.XmlCrcValid == false`. Caller decides whether to accept or reject. |
| Response timeout (no reply in allotted time) | `SendCommandAsync` throws `TimeoutException` (or `OperationCanceledException` if using the token pattern). |

---

## 3. Application Integration

### 3.1 Recommended Startup Sequence

```csharp
// 1. Connect
await client.ConnectAsync("COM3", 115200, ct);

// 2. Subscribe to unsolicited notifications BEFORE any command
client.ModeChanged     += OnModeChanged;
client.PressureUpdated += OnPressureUpdated;
client.UnsolicitedMessage += OnUnsolicitedMessage;

// 3. Query device capabilities
var features = await client.GetFeaturesAsync(ct);

// 4. Set detail level 4 once (persists until device reboots or firmware resets it)
await client.SetDetailLevelAsync(4, ct);

// 5. Ready to measure
```

> **Important:** `d 4` must be re-sent after a device reboot (`q`) and may need to be re-sent after each measurement on older CardioScope firmware (≤ 037) that resets the detail level. A safe practice is to always send `d 4` immediately before each `s` command.

### 3.2 Measurement Flow

```csharp
var progress = new Progress<int>(p => cuffPressureLabel.Text = $"{p} mmHg");

using var cts = CancellationTokenSource.CreateLinkedTokenSource(
                    _userCancelCts.Token,
                    new CancellationTokenSource(TimeSpan.FromMinutes(10)).Token);

await client.SetDetailLevelAsync(4, cts.Token);

var result = await client.StartMeasurementAsync(
    new MeasurementRequest(TargetPressureMmHg: 160, PatientId: "P001"),
    cuffPressureProgress: progress,
    ct: cts.Token);

if (result.IsSuccess && result.XmlCrcValid)
    ProcessXml(result.Xml!);
else if (result.IsCancelled)
    ShowMessage("Measurement cancelled.");
else
    ShowMessage($"Measurement failed: {result.FailureDescription} (code {result.FailureCode})");
```

### 3.3 Handling Unsolicited Messages in MAUI

Events fire on the **background read thread**. Always marshal to the UI thread:

```csharp
client.ModeChanged += (_, e) =>
    MainThread.BeginInvokeOnMainThread(() =>
        StatusLabel.Text = $"Device: {e.Description}");

client.PressureUpdated += (_, e) =>
    MainThread.BeginInvokeOnMainThread(() =>
        PressureLabel.Text = $"Cuff: {e.PressureMmHg} mmHg");

client.UnsolicitedMessage += (_, e) =>
    MainThread.BeginInvokeOnMainThread(() =>
        LogAppend(e.Message.RawLine));
```

In a MVVM pattern, the ViewModel subscribes in its constructor and publishes `ObservableProperty` values via `MainThread.BeginInvokeOnMainThread` (or via `WeakReferenceMessenger` if the ViewModel has no reference to the View).

---

## 4. MAUI Test Application Design

See [`../MauiTestApplication/MauiTestApplicationDesign.md`](../MauiTestApplication/MauiTestApplicationDesign.md).

---

## 5. Open Questions / Implementation Notes

| # | Item |
|---|---|
| 2 | **Detail level persistence.** Older CardioScope (≤ 037) firmware resets detail level to 0 after each measurement. Always send `d 4` immediately before `s`. |
| 3 | **`!` response format.** F 14 if not busy or F 17 if busy. . |
| 4 | **Mode codes.** All 24 codes (0–23) now documented in §1.4 from the existing `DeviceMode` enum in the Uscom codebase. The `ModeChangedEventArgs.Description` should still fall back to the raw numeric string for any future unknown codes. |
| 5 | **Baud rate change.** After `b <baud>`, the device changes baud immediately and sends no acknowledgement at the new baud. The assembly must close the port and notify the caller to reconnect. |
| 6 | **`q` (reboot).** No response is sent. Wait for the device to emit `M 00` on the serial port (may take several seconds) to confirm successful reboot. |
| 7 | **Thread safety.** All public methods must be safe to call from any thread. The internal `SemaphoreSlim` serialises serial writes; events are raised on the reader thread without locking. |
| 8 | **XML multi-line buffering.** After the `|_XML_Size…` header, subsequent lines must be accumulated until the declared byte count is reached. A 30-second timeout on this accumulation step guards against a truncated transmission. |
