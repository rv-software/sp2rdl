namespace sp2rdlGenExtension.Model;

internal sealed class ReportSummaryConfig
{
    public bool Enabled { get; set; }

    public string TextTemplate { get; set; } = string.Empty;

    public bool ShowTopLine { get; set; } = true;

    public double HeightInCentimeters { get; set; } = 2.0d;
}
