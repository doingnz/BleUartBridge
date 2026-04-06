using BleUartBridgeTester.Services;
using BleUartBridgeTester.ViewModels;
using BleUartBridgeTester.Pages;
using BPplus.Ble;
using CommunityToolkit.Maui;
using Microsoft.Extensions.Logging;

namespace BleUartBridgeTester;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();

        builder
            .UseMauiApp<App>()
            .UseMauiCommunityToolkit()
            .ConfigureFonts(fonts =>
            {
                fonts.AddFont("OpenSans-Regular.ttf",    "OpenSansRegular");
                fonts.AddFont("OpenSans-Semibold.ttf",   "OpenSansSemibold");
            });

        // BLE services (from BPplus.Ble)
        builder.Services.AddSingleton<IBleScanner, BleScanner>();

        // Raw BLE client
        builder.Services.AddSingleton<IRawBleClient, RawBleClient>();

        // Serial port service — platform-specific
#if ANDROID
        builder.Services.AddSingleton<ISerialPortService, AndroidSerialPortService>();
#else
        builder.Services.AddSingleton<ISerialPortService, SerialPortService>();
#endif

        // ViewModel (singleton — shared by all pages)
        builder.Services.AddSingleton<MainViewModel>();

        // Pages (singleton so ViewModel is not re-created on tab switch)
        builder.Services.AddSingleton<ConnectPage>();
        builder.Services.AddSingleton<ManualPage>();
        builder.Services.AddSingleton<TestPage>();

#if DEBUG
        builder.Logging.AddDebug();
#endif

        return builder.Build();
    }
}
