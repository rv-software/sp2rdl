namespace sp2rdlGenExtension.Model;

internal sealed class ReportVariableConfig
{
    public bool Enabled { get; set; } = true;

    public string Name { get; set; } = string.Empty;

    public string? StaticValue { get; set; }

    public string? SourceColumnName { get; set; }

    public string? FallbackValue { get; set; }
}
