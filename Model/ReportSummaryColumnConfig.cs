namespace sp2rdlGenExtension.Model;

internal sealed class ReportSummaryColumnConfig
{
    public string TextTemplate { get; set; } = string.Empty;

    public bool ShowTopLine { get; set; }

    public double WidthPercent { get; set; }

    public string VerticalAlign { get; set; } = "Top";

    public double PaddingInPoints { get; set; } = 3.0d;
}
