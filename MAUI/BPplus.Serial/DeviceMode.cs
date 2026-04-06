namespace BPplus.Serial;

/// <summary>
/// Device operating modes as reported by the BP+ via 'M nn' messages.
/// Values match the numeric codes transmitted over the serial interface.
/// </summary>
public enum DeviceMode
{
    Initial                  = 0,   // Boot / self-test starting
    Offline                  = 1,   // Device offline
    Ready                    = 2,   // Ready / idle
    MeasuringBp              = 3,   // Measuring BP (NIBP phase)
    DeflatingCuff            = 4,   // Deflating cuff
    InflatingToSs            = 5,   // Inflating to suprasystolic
    AcquireData              = 6,   // Acquiring waveform data
    ProcessData              = 7,   // Processing measurement data
    SelectStorage            = 8,   // Store / recall selection menu
    StoreRecall              = 9,   // Store / recall active
    ExtraInfo                = 10,  // Extra information screen
    SafetyLock               = 11,  // Safety lock active
    ServiceMenu              = 12,  // Service menu
    ServiceMenuManometer     = 13,  // Service menu — manometer
    ServiceCharacterisingSys = 14,  // Service menu — characterising systolic
    SetTarget                = 15,  // Set target pressure menu
    DownloadApp              = 16,  // Firmware download / app update
    Settings                 = 17,  // Settings menu
    SelectLanguage           = 18,  // Language selection menu
    SetDatetime              = 19,  // Date / time setting menu
    CentralPress             = 20,  // Central pressure mode
    MeasurePressureTest      = 21,  // Pressure test mode (entered via 't'; exit with 'c')
    CountDownAobp            = 22,  // AOBP countdown
    SelectAobpMode           = 23,  // AOBP mode selection
}
