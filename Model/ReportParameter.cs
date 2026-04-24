namespace sp2rdlGenExtension.Model;

internal sealed class ReportParameter
{
    public string Name { get; set; } = string.Empty;

    public string SqlTypeName { get; set; } = string.Empty;

    public string Prompt { get; set; } = string.Empty;

    public ControlType ControlType { get; set; } = ControlType.Text;

    public bool Nullable { get; set; }

    public bool AllowBlank { get; set; }

    public bool MultiValue { get; set; }

    public bool Hidden { get; set; }

    public string? DefaultValueExpression { get; set; }

    public LookupConfig? Lookup { get; set; }

    public List<string> StaticValidValues { get; set; } = new();

    public List<ParameterDependency> Dependencies { get; set; } = new();

    public int LayoutRow { get; set; }

    public int LayoutColumn { get; set; }
}
