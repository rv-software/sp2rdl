namespace sp2rdlGenExtension.Model;

internal sealed class PageSetupConfig
{
    public string PageSizeName { get; set; } = "A4";

    public PageOrientation Orientation { get; set; } = PageOrientation.Portrait;

    public double WidthInCentimeters { get; set; } = 21.0d;

    public double HeightInCentimeters { get; set; } = 29.7d;

    public double LeftMarginInCentimeters { get; set; } = 1.0d;

    public double RightMarginInCentimeters { get; set; } = 1.0d;

    public double TopMarginInCentimeters { get; set; } = 1.0d;

    public double BottomMarginInCentimeters { get; set; } = 1.0d;
}
