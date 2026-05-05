using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace sp2rdlGenExtension.Services;

internal static class DialogThemeService
{
    private const int DwmwaCaptionColor = 35;

    public static void Apply(Window window, IntPtr ownerHwnd)
    {
        var palette = IsDarkTheme(ownerHwnd) ? DialogPalette.Dark : DialogPalette.Light;
        ApplyPalette(window, palette);
    }

    public static void ApplyFromOwner(Window window, Window owner)
    {
        foreach (var key in ThemeResourceKeys)
        {
            if (owner.TryFindResource(key) is Brush brush)
            {
                window.Resources[key] = brush.CloneCurrentValue();
            }
        }
    }

    private static bool IsDarkTheme(IntPtr ownerHwnd)
    {
        if (TryGetWindowCaptionColor(ownerHwnd, out var color))
        {
            return GetBrightness(color) < 128;
        }

        return IsWindowsDarkAppTheme();
    }

    private static bool TryGetWindowCaptionColor(IntPtr hwnd, out Color color)
    {
        color = default;
        if (hwnd == IntPtr.Zero)
        {
            return false;
        }

        var hr = DwmGetWindowAttribute(hwnd, DwmwaCaptionColor, out var colorRef, Marshal.SizeOf<int>());
        if (hr != 0 || colorRef is 0 or -1)
        {
            return false;
        }

        color = Color.FromRgb(
            (byte)(colorRef & 0xFF),
            (byte)((colorRef >> 8) & 0xFF),
            (byte)((colorRef >> 16) & 0xFF));
        return true;
    }

    private static bool IsWindowsDarkAppTheme()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return key?.GetValue("AppsUseLightTheme") is int value && value == 0;
    }

    private static double GetBrightness(Color color)
        => (color.R * 299 + color.G * 587 + color.B * 114) / 1000d;

    private static void SetBrush(Window window, string resourceKey, string hexColor)
        => window.Resources[resourceKey] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hexColor));

    private static void ApplyPalette(Window window, DialogPalette palette)
    {
        SetBrush(window, "DialogBackgroundBrush", palette.DialogBackground);
        SetBrush(window, "DialogPanelBrush", palette.PanelBackground);
        SetBrush(window, "DialogPrimaryTextBrush", palette.PrimaryText);
        SetBrush(window, "DialogSecondaryTextBrush", palette.SecondaryText);
        SetBrush(window, "DialogFieldBackgroundBrush", palette.FieldBackground);
        SetBrush(window, "DialogFieldTextBrush", palette.FieldText);
        SetBrush(window, "DialogBorderBrush", palette.Border);
        SetBrush(window, "DialogButtonBackgroundBrush", palette.ButtonBackground);
        SetBrush(window, "DialogButtonTextBrush", palette.ButtonText);
        SetBrush(window, "DialogCancelButtonBackgroundBrush", palette.CancelButtonBackground);
    }

    private static readonly string[] ThemeResourceKeys =
    [
        "DialogBackgroundBrush",
        "DialogPanelBrush",
        "DialogPrimaryTextBrush",
        "DialogSecondaryTextBrush",
        "DialogFieldBackgroundBrush",
        "DialogFieldTextBrush",
        "DialogBorderBrush",
        "DialogButtonBackgroundBrush",
        "DialogButtonTextBrush",
        "DialogCancelButtonBackgroundBrush"
    ];

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out int pvAttribute, int cbAttribute);

    private sealed record DialogPalette(
        string DialogBackground,
        string PanelBackground,
        string PrimaryText,
        string SecondaryText,
        string FieldBackground,
        string FieldText,
        string Border,
        string ButtonBackground,
        string ButtonText,
        string CancelButtonBackground)
    {
        public static DialogPalette Dark { get; } = new(
            "#1E1E1E",
            "#252526",
            "#D4D4D4",
            "#9AA0A6",
            "#3C3C3C",
            "#F2F2F2",
            "#3F3F46",
            "#3E3E42",
            "#FFFFFF",
            "#4A4A4F");

        public static DialogPalette Light { get; } = new(
            "#F5F5F5",
            "#FFFFFF",
            "#1F1F1F",
            "#555555",
            "#FFFFFF",
            "#1F1F1F",
            "#C8C8C8",
            "#E5E5E5",
            "#1F1F1F",
            "#D8D8D8");
    }
}
