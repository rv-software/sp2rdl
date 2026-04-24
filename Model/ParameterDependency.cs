namespace sp2rdlGenExtension.Model;

internal sealed class ParameterDependency
{
    public string TargetParameterName { get; set; } = string.Empty;

    public DependencyOperator Operator { get; set; } = DependencyOperator.GreaterThanOrEqual;
}
