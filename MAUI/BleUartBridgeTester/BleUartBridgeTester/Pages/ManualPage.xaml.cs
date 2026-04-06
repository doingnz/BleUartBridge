using BleUartBridgeTester.ViewModels;

namespace BleUartBridgeTester.Pages;

public partial class ManualPage : ContentPage
{
    public ManualPage(MainViewModel vm)
    {
        InitializeComponent();
        BindingContext = vm;
    }
}
