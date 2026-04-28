namespace sp2rdlGenExtension.Model;

internal sealed class ReportVariablesConfig
{
    public ReportDynamicVariableSourceConfig DynamicSource { get; set; } = new();

    public List<ReportVariableConfig> Items { get; set; } = new();
}
