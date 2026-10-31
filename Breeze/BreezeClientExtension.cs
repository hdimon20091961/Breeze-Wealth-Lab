using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using WealthLab.WPF;

namespace BreezeVL;

public sealed class BreezeClientExtension : WL8ClientExtensionBase
{
    public override string Name => "Market Breeze";

    public override List<MenuItem> GetMenuItems() =>
    [
        CreateExtensionMenuItem("Market Breeze", null!, OpenWindow)
    ];

    public override ChildWindow? GetChildWindow(string token) =>
        token == BreezeWindow.WindowToken ? CreateWindow() : null;

    private void OpenWindow(object sender, RoutedEventArgs e) => CreateWindow();

    private ChildWindow CreateWindow()
    {
        var window = new BreezeWindow();
        MyClientHost.ShowExtensionChildWindow(window, "Market Breeze", null!);
        return window;
    }
}
