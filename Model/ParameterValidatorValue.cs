namespace sp2rdlGenExtension.Model;

internal sealed class ParameterValidatorValue
{
    public string Code { get; set; } = string.Empty;

    public string Value { get; set; } = string.Empty;

    public string ValueType { get; set; } = "string";

    public string Kind { get; set; } = "validation";

    public int SortOrder { get; set; }

    public bool IsEnabled { get; set; } = true;
}
