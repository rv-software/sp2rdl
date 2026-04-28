namespace sp2rdlGenExtension.Model;

internal sealed class PageFooterConfig
{
    public bool Enabled { get; set; } = true;

    public string LeftText { get; set; } = string.Empty;

    public string RightText { get; set; } = string.Empty;

    public string? LogoImagePath { get; set; }

    public bool ShowPageNumber { get; set; } = true;

    public bool ShowTopLine { get; set; }

    public PageFooterDisplayMode DisplayMode { get; set; } = PageFooterDisplayMode.AllPages;

    public double HeightInCentimeters { get; set; } = 1.0d;

    public bool PrintOnFirstPage { get; set; } = true;

    public bool PrintOnLastPage { get; set; } = true;
}
