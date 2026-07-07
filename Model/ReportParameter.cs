namespace sp2rdlGenExtension.Model;

internal sealed class ReportParameter
{
    public string Name { get; set; } = string.Empty;

    public string? DefinitionName { get; set; }

    public string SqlTypeName { get; set; } = string.Empty;

    public string Prompt { get; set; } = string.Empty;

    public ControlType ControlType { get; set; } = ControlType.Text;

    public bool Nullable { get; set; }

    public bool AllowBlank { get; set; }

    public bool MultiValue { get; set; }

    public bool Hidden { get; set; }

    public bool IsVisible { get; set; } = true;

    public string? EntityKey { get; set; }

    public string? ValueFieldTemplate { get; set; }

    public string? DisplayFieldTemplate { get; set; }

    public string? DefaultValueExpression { get; set; }

    public string? DefaultValueSql { get; set; }

    public string? DefaultValueDatasetName { get; set; }

    public string? DefaultValueField { get; set; }

    public string? DisplayFormat { get; set; }

    public string? LookupSql { get; set; }

    public string? DependsOnParameterName { get; set; }

    public string? DependencyFilterPath { get; set; }

    public string? BindToDatasetParameterName { get; set; }

    public string? CompareToParameterName { get; set; }

    public string? CompareOperator { get; set; }

    public string? ComparisonValueTemplate { get; set; }

    public LookupConfig? Lookup { get; set; }

    public List<StaticValidValue> StaticValidValues { get; set; } = new();

    public List<ParameterValidatorValue> RuntimeSettings { get; set; } = new();

    public List<ParameterDependency> Dependencies { get; set; } = new();

    public int OrdinalNumber { get; set; }

    public int LayoutRow { get; set; }

    public int LayoutColumn { get; set; }
}
