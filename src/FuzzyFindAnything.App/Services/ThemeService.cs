using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace FuzzyFindAnything.App.Services;

public static class ThemeService
{
    public static void Apply(string theme, Window? window = null)
    {
        if (theme is not ("Light" or "Dark"))
            throw new ArgumentException("Choose the Light or Dark theme.", nameof(theme));
        Application.Current.Resources.MergedDictionaries[0] = new ResourceDictionary
        {
            Source = new Uri($"Themes/{theme}.xaml", UriKind.Relative)
        };
        if (window is not null)
            ApplyTitleBar(window, theme == "Dark");
    }

    public static void ApplyTitleBar(Window window, bool dark)
    {
        int enabled = dark ? 1 : 0;
        int result = DwmSetWindowAttribute(new WindowInteropHelper(window).Handle, 20, ref enabled, sizeof(int));
        if (result < 0)
            Trace.TraceWarning($"Could not update the native title bar theme: 0x{result:X8}.");
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
}
