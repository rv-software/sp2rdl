namespace sp2rdlGenExtension.Model;

internal sealed record SpParameter(
    string Name,
    string SqlTypeName,
    bool IsNullable,
    bool HasDefault,
    bool IsOutput,
    int OrdinalPosition);
