namespace sp2rdlGenExtension.Model;

internal sealed class PageFooterConfig
{
    public bool Enabled { get; set; } = true;

    public string? LogoImagePath { get; set; }

    public bool ShowPageNumber { get; set; } = true;

    public double HeightInCentimeters { get; set; } = 1.0d;

    public bool PrintOnFirstPage { get; set; } = true;

    public bool PrintOnLastPage { get; set; } = true;
}
