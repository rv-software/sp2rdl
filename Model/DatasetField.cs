namespace sp2rdlGenExtension.Model;

internal sealed record DatasetField(
    string Name,
    string SqlTypeName,
    bool IsNullable,
    int OrdinalPosition,
    string? Format = null);
