using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;

namespace sp2rdlGenExtension.Dialogs;

public partial class ColorPickerDialog : Window
{
    public string SelectedHexColor { get; private set; }

    public ColorPickerDialog(string initialHexColor)
    {
        InitializeComponent();
        SelectedHexColor = NormalizeHexColor(initialHexColor, "#000000");
        var color = (Color)ColorConverter.ConvertFromString(SelectedHexColor);
        SliderRed.Value = color.R;
        SliderGreen.Value = color.G;
        SliderBlue.Value = color.B;
        UpdatePreview();
    }

    private void Slider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        => UpdatePreview();

    private void UpdatePreview()
    {
        if (PreviewBorder is null)
        {
            return;
        }

        var red = (byte)Math.Round(SliderRed.Value);
        var green = (byte)Math.Round(SliderGreen.Value);
        var blue = (byte)Math.Round(SliderBlue.Value);
        SelectedHexColor = $"#{red:X2}{green:X2}{blue:X2}";
        PreviewBorder.Background = new SolidColorBrush(Color.FromRgb(red, green, blue));
        TxtRed.Text = red.ToString(CultureInfo.InvariantCulture);
        TxtGreen.Text = green.ToString(CultureInfo.InvariantCulture);
        TxtBlue.Text = blue.ToString(CultureInfo.InvariantCulture);
        TxtHex.Text = SelectedHexColor;
    }

    private void OkButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }

    private static string NormalizeHexColor(string? value, string fallback)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        var trimmed = value.Trim();
        if (!trimmed.StartsWith("#", StringComparison.Ordinal))
        {
            trimmed = "#" + trimmed;
        }

        return Regex.IsMatch(trimmed, "^#[0-9A-Fa-f]{6}$") ? trimmed.ToUpperInvariant() : fallback;
    }
}
