using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using WealthLab.WPF;

namespace BreezeVL;

public sealed class BreezeClientExtension : WL8ClientExtensionBase
{
    public override string Name => "Breeze Wealth-Lab";

    public override List<MenuItem> GetMenuItems() =>
    [
        CreateExtensionMenuItem("Breeze Wealth-Lab — Market Breadth", null!, OpenWindow)
    ];

    public override ChildWindow? GetChildWindow(string token) =>
        token == BreezeWindow.WindowToken ? CreateWindow() : null;

    private void OpenWindow(object sender, RoutedEventArgs e) => CreateWindow();

    private ChildWindow CreateWindow()
    {
        var window = new BreezeWindow();
        MyClientHost.ShowExtensionChildWindow(window, "Breeze Wealth-Lab — Market Breadth", null!);
        return window;
    }
}
