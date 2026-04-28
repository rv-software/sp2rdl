namespace sp2rdlGenExtension.Model;

internal sealed class ReportDynamicVariableSourceConfig
{
    public bool Enabled { get; set; }

    public string DatasetName { get; set; } = "dsReportVariables";

    public string SqlExpression { get; set; } = string.Empty;
}
