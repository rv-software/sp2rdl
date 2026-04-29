namespace sp2rdlGenExtension.Model;

internal sealed class SubreportParameterMapping
{
    public string SubreportParameterName { get; set; } = string.Empty;

    public SubreportParameterSourceKind SourceKind { get; set; } = SubreportParameterSourceKind.ReportParameter;

    public string? SourceName { get; set; }

    public string? StaticValue { get; set; }

    public string? Expression { get; set; }
}
