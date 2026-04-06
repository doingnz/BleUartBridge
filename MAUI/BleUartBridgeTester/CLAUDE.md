# BleUartBridgeTester — MAUI

## Purpose

.NET MAUI test app for the BleUartBridge ESP32 firmware. Connects via BLE NUS and provides a UI for sending/receiving data over the bridged UART.

## Solution structure

```
BleUartBridgeTester.sln
└── BleUartBridgeTester/
    ├── MauiProgram.cs          DI setup, app entry point
    ├── App.xaml / App.xaml.cs  Application class
    ├── AppShell.xaml           Shell navigation
    ├── Models/                 Data models
    ├── Pages/                  ContentPage views (XAML + code-behind)
    ├── Services/               BLE service abstraction
    ├── ViewModels/             MVVM view models (CommunityToolkit.Mvvm)
    └── Platforms/              Platform-specific code
```

## Key patterns

- CommunityToolkit.Mvvm for MVVM (`[ObservableProperty]`, `[RelayCommand]`)
- BLE events fire on background thread — always marshal to UI with `MainThread.BeginInvokeOnMainThread`
- Services, main ViewModel, and AppShell registered as Singleton; detail views as Transient
- Resources/styles defined in the page's own `ContentPage.Resources`, not in `App.xaml`, to avoid DI startup ordering issues
