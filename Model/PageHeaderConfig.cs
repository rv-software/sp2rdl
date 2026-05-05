namespace sp2rdlGenExtension.Model;

internal sealed class PageHeaderConfig
{
    public bool Enabled { get; set; } = true;

    public string LeftText { get; set; } = string.Empty;

    public string RightText { get; set; } = string.Empty;

    public double HeightInCentimeters { get; set; } = 1.2d;

    public bool PrintOnFirstPage { get; set; } = true;

    public bool PrintOnLastPage { get; set; } = true;

    public double FontSizeInPoints { get; set; } = 9d;
}
