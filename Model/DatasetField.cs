namespace sp2rdlGenExtension.Model;

internal enum MatrixFieldRole
{
    None,
    RowGroup,
    ColumnGroup,
    Measure
}

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
    string? DefaultLabel = null,
    MatrixFieldRole MatrixRole = MatrixFieldRole.None,
    int MatrixLevel = 0,
    double WidthPercent = 0);
