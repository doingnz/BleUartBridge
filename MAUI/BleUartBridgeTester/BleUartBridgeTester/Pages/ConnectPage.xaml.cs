using BleUartBridgeTester.ViewModels;

namespace BleUartBridgeTester.Pages;

public partial class ConnectPage : ContentPage
{
    private readonly MainViewModel _vm;

    public ConnectPage(MainViewModel vm)
    {
        InitializeComponent();
        BindingContext = _vm = vm;
    }

    private void OnRefreshPortsClicked(object? sender, EventArgs e)
        => _vm.RefreshPortsCommand.Execute(null);
}
