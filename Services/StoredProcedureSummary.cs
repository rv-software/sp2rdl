namespace sp2rdlGenExtension.Services;

internal sealed record StoredProcedureSummary(
    string SchemaName,
    string ProcedureName)
{
    public string DisplayName => $"{SchemaName}.{ProcedureName}";
}
