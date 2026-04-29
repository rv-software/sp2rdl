namespace sp2rdlGenExtension.Model;

internal sealed class ReportSummaryConfig
{
    public bool Enabled { get; set; }

    public ReportBandLayoutMode LayoutMode { get; set; } = ReportBandLayoutMode.Inline;

    public string? SubreportPath { get; set; }

    public string? SubreportName { get; set; }

    public string? SubreportServerPath { get; set; }

    public List<SubreportParameterMapping> SubreportParameterMappings { get; set; } = new();

    public bool FallbackToInline { get; set; } = true;

    public string TextTemplate { get; set; } = string.Empty;

    public bool ShowTopLine { get; set; } = true;

    public double HeightInCentimeters { get; set; } = 2.0d;
}
