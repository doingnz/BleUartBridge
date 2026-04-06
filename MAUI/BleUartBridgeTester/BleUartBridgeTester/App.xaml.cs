using BleUartBridgeTester.ViewModels;

namespace BleUartBridgeTester;

public partial class App : Application
{
    public App() => InitializeComponent();

    protected override Window CreateWindow(IActivationState? activationState)
    {
        var window = new Window(new AppShell());
        window.Destroying += OnWindowDestroying;
        return window;
    }

    private async void OnWindowDestroying(object? sender, EventArgs e)
    {
        var vm = Handler?.MauiContext?.Services.GetService<MainViewModel>();
        if (vm is not null)
            await vm.DisconnectCommand.ExecuteAsync(null);
    }
}
