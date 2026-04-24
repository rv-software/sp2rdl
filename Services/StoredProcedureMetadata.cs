using sp2rdlGenExtension.Model;

namespace sp2rdlGenExtension.Services;

internal sealed record StoredProcedureMetadata(
    string SchemaName,
    string ProcedureName,
    IReadOnlyList<SpParameter> Parameters,
    IReadOnlyList<DatasetField> Fields,
    string? ResultSetWarning = null);
