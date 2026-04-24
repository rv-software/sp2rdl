namespace sp2rdlGenExtension.Model;

internal sealed class DatasetConfig
{
    public string Name { get; set; } = string.Empty;

    public string Command { get; set; } = string.Empty;

    public CommandKind CommandKind { get; set; } = CommandKind.StoredProcedure;

    public List<DatasetField> Fields { get; set; } = new();

    public List<DatasetParameterBinding> ParameterBindings { get; set; } = new();
}
