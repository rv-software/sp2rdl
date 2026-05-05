namespace sp2rdlGenExtension.Model;

internal sealed record DatasetField(
    string Name,
    string SqlTypeName,
    bool IsNullable,
    int OrdinalPosition,
    string? Format = null,
    int GroupLevel = 0,
    string? AggregateFunction = null,
    bool IncludeInReport = true,
    string? TextAlign = null,
    string? DefaultLabel = null);
