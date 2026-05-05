namespace sp2rdlGenExtension.Model;

internal sealed class DatasetFieldDraft
{
    public string Name { get; set; } = string.Empty;

    public string SqlTypeName { get; set; } = "nvarchar";

    public bool IsNullable { get; set; }

    public int OrdinalPosition { get; set; }

    public bool IncludeInReport { get; set; } = true;

    public string? Format { get; set; }

    public int GroupLevel { get; set; }

    public string? AggregateFunction { get; set; }

    public string? TextAlign { get; set; }

    public string? DefaultLabel { get; set; }

    public IReadOnlyList<string> AllowedAggregates
        => [string.Empty, .. GetAllowedAggregates(SqlTypeName)];

    public static DatasetFieldDraft FromDatasetField(DatasetField field)
        => new()
        {
            Name = field.Name,
            SqlTypeName = field.SqlTypeName,
            IsNullable = field.IsNullable,
            OrdinalPosition = field.OrdinalPosition,
            IncludeInReport = field.IncludeInReport,
            Format = field.Format ?? GetDefaultFormat(field.SqlTypeName),
            GroupLevel = field.GroupLevel,
            AggregateFunction = field.AggregateFunction,
            TextAlign = field.TextAlign,
            DefaultLabel = string.IsNullOrWhiteSpace(field.DefaultLabel) ? field.Name : field.DefaultLabel
        };

    public DatasetField ToDatasetField()
    {
        var groupLevel = GroupLevel is >= 1 and <= 4 ? GroupLevel : 0;
        var aggregateFunction = groupLevel > 0 ? null : NormalizeAggregateFunction(AggregateFunction, SqlTypeName);
        return new(Name, SqlTypeName, IsNullable, OrdinalPosition, NormalizeFormat(Format), groupLevel, aggregateFunction, IncludeInReport, NormalizeTextAlign(TextAlign), NormalizeDefaultLabel(DefaultLabel, Name));
    }

    public static string? GetDefaultFormat(string sqlTypeName)
    {
        var normalized = sqlTypeName.Split('(', 2)[0].Trim().ToLowerInvariant();

        return normalized switch
        {
            "date" or "datetime" or "datetime2" or "smalldatetime" or "datetimeoffset" => "dd.MM.yyyy",
            "time" => "HH:mm",
            "decimal" or "numeric" or "money" or "smallmoney" or "float" or "real" => "#,##0.00",
            "tinyint" or "smallint" or "int" or "bigint" => "#,##0",
            _ => null
        };
    }

    private static string? NormalizeFormat(string? format)
        => string.IsNullOrWhiteSpace(format) ? null : format.Trim();

    private static string? NormalizeDefaultLabel(string? defaultLabel, string fieldName)
    {
        var normalized = string.IsNullOrWhiteSpace(defaultLabel) ? fieldName : defaultLabel.Trim();
        return string.Equals(normalized, fieldName, StringComparison.Ordinal) ? null : normalized;
    }

    private static string? NormalizeTextAlign(string? textAlign)
    {
        if (string.IsNullOrWhiteSpace(textAlign))
        {
            return null;
        }

        var normalized = textAlign.Trim();
        return string.Equals(normalized, "Left", StringComparison.OrdinalIgnoreCase)
            || string.Equals(normalized, "Center", StringComparison.OrdinalIgnoreCase)
            || string.Equals(normalized, "Right", StringComparison.OrdinalIgnoreCase)
            ? normalized
            : null;
    }

    private static string? NormalizeAggregateFunction(string? aggregateFunction, string sqlTypeName)
    {
        if (string.IsNullOrWhiteSpace(aggregateFunction))
        {
            return null;
        }

        var normalized = aggregateFunction.Trim();
        return GetAllowedAggregates(sqlTypeName).Contains(normalized, StringComparer.OrdinalIgnoreCase)
            ? GetAllowedAggregates(sqlTypeName).First(value => string.Equals(value, normalized, StringComparison.OrdinalIgnoreCase))
            : null;
    }

    public static IReadOnlyList<string> GetAllowedAggregates(string sqlTypeName)
    {
        var normalized = sqlTypeName.Split('(', 2)[0].Trim().ToLowerInvariant();
        return normalized switch
        {
            "tinyint" or "smallint" or "int" or "bigint" or "decimal" or "numeric" or "money" or "smallmoney" or "float" or "real" => ["Sum", "Count", "CountDistinct", "Min", "Max", "Avg"],
            "date" or "datetime" or "datetime2" or "smalldatetime" or "datetimeoffset" or "time" => ["Count", "CountDistinct", "Min", "Max"],
            _ => ["Count", "CountDistinct"]
        };
    }
}
