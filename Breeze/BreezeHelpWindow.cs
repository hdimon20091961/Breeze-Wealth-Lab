using System.Diagnostics;
using System.Net;
using System.Reflection;
using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace BreezeVL;

internal sealed class BreezeHelpWindow : Window
{
    public const string ExternalHelpFileName = "BreezeHelp.html";

    public BreezeHelpWindow(string fallbackReason)
    {
        Title = "Zweig Breeze — резервная справка";
        Width = 980;
        Height = 760;
        MinWidth = 700;
        MinHeight = 500;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var browser = new WebBrowser();
        Content = browser;

        Assembly assembly = typeof(BreezeHelpWindow).Assembly;
        string? resourceName = assembly.GetManifestResourceNames()
            .FirstOrDefault(name => name.EndsWith("BreezeHelp.html", StringComparison.OrdinalIgnoreCase));
        if (resourceName is null)
        {
            browser.NavigateToString("<html><body><h1>Zweig Breeze</h1><p>Embedded help is unavailable.</p></body></html>");
            return;
        }

        using Stream stream = assembly.GetManifestResourceStream(resourceName)!;
        using var reader = new StreamReader(stream);
        string html = reader.ReadToEnd().Replace(
            "<!-- RESEARCH-MODEL-HELP -->",
            BreezeResearchHelp.BuildHtml(),
            StringComparison.Ordinal);
        string notice =
            "<div class=\"note\" style=\"border-left-color:#b42318;background:#fff1f0\">" +
            "<b>Открыта резервная копия из Breeze.dll.</b> " +
            WebUtility.HtmlEncode(fallbackReason) +
            "</div>";
        html = html.Replace("<body><main>", "<body><main>" + notice, StringComparison.Ordinal);
        browser.NavigateToString(html);
    }

    public static bool TryOpenExternal(out string fallbackReason)
    {
        string assemblyDirectory = Path.GetDirectoryName(typeof(BreezeHelpWindow).Assembly.Location)
            ?? AppContext.BaseDirectory;
        string helpPath = Path.Combine(assemblyDirectory, ExternalHelpFileName);
        if (!File.Exists(helpPath))
        {
            fallbackReason = $"Внешний файл {ExternalHelpFileName} не найден рядом с Breeze.dll.";
            return false;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = helpPath,
                UseShellExecute = true
            });
            fallbackReason = string.Empty;
            return true;
        }
        catch (Exception ex)
        {
            fallbackReason = $"Не удалось открыть внешний файл {ExternalHelpFileName}: {ex.Message}";
            return false;
        }
    }
}
