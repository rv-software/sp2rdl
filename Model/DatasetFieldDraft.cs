namespace sp2rdlGenExtension.Model;

internal sealed class DatasetFieldDraft
{
    public string Name { get; set; } = string.Empty;

    public string SqlTypeName { get; set; } = "nvarchar";

    public bool IsNullable { get; set; }

    public int OrdinalPosition { get; set; }

    public string? Format { get; set; }

    public static DatasetFieldDraft FromDatasetField(DatasetField field)
        => new()
        {
            Name = field.Name,
            SqlTypeName = field.SqlTypeName,
            IsNullable = field.IsNullable,
            OrdinalPosition = field.OrdinalPosition,
            Format = field.Format ?? GetDefaultFormat(field.SqlTypeName)
        };

    public DatasetField ToDatasetField()
        => new(Name, SqlTypeName, IsNullable, OrdinalPosition, NormalizeFormat(Format));

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
}
