using BleUartBridgeTester.ViewModels;

namespace BleUartBridgeTester.Pages;

public partial class TestPage : ContentPage
{
    public TestPage(MainViewModel vm)
    {
        InitializeComponent();
        BindingContext = vm;
    }
}
