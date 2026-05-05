namespace sp2rdlGenExtension.Model;

internal sealed class ReportTitleConfig
{
    public bool Enabled { get; set; } = true;

    public string Text { get; set; } = string.Empty;

    public double HeightInCentimeters { get; set; } = 1.2d;

    public double FontSizeInPoints { get; set; } = 16.0d;

    public string TextAlign { get; set; } = "Center";

    public bool ShowInPageHeaderAfterFirstPage { get; set; } = true;
}
