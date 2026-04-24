using sp2rdlGenExtension.Model;

namespace sp2rdlGenExtension.Services;

internal static class ReportModelFactory
{
    public static ReportModel FromStoredProcedure(StoredProcedureMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(metadata);

        var dataset = new DatasetConfig
        {
            Name = "DsMain",
            Command = $"{metadata.SchemaName}.{metadata.ProcedureName}",
            CommandKind = CommandKind.StoredProcedure,
            Fields = metadata.Fields.ToList(),
            ParameterBindings = metadata.Parameters
                .Where(parameter => !parameter.IsOutput)
                .Select(parameter => new DatasetParameterBinding
                {
                    DatasetParameterName = parameter.Name,
                    ReportParameterName = NormalizeParameterName(parameter.Name)
                })
                .ToList()
        };

        return new ReportModel
        {
            Name = metadata.ProcedureName,
            MainDatasetName = dataset.Name,
            Datasets = [dataset],
            Parameters = metadata.Parameters
                .Where(parameter => !parameter.IsOutput)
                .Select(parameter => new ReportParameter
                {
                    Name = NormalizeParameterName(parameter.Name),
                    SqlTypeName = parameter.SqlTypeName,
                    Prompt = BuildPrompt(parameter.Name),
                    ControlType = MapControlType(parameter.SqlTypeName),
                    Nullable = parameter.IsNullable,
                    AllowBlank = AllowsBlank(parameter.SqlTypeName),
                    LayoutRow = Math.Max(parameter.OrdinalPosition - 1, 0),
                    LayoutColumn = 0
                })
                .ToList()
        };
    }

    private static string NormalizeParameterName(string parameterName)
        => parameterName.TrimStart('@');

    private static string BuildPrompt(string parameterName)
    {
        var name = NormalizeParameterName(parameterName).Replace('_', ' ');
        return string.IsNullOrWhiteSpace(name) ? parameterName : name;
    }

    private static bool AllowsBlank(string sqlTypeName)
        => sqlTypeName.Contains("char", StringComparison.OrdinalIgnoreCase)
            || sqlTypeName.Contains("text", StringComparison.OrdinalIgnoreCase);

    private static ControlType MapControlType(string sqlTypeName)
    {
        var normalized = sqlTypeName.Split('(', 2)[0].Trim().ToLowerInvariant();

        return normalized switch
        {
            "bit" => ControlType.Boolean,
            "date" => ControlType.Date,
            "datetime" or "datetime2" or "smalldatetime" or "datetimeoffset" => ControlType.DateTime,
            "tinyint" or "smallint" or "int" or "bigint" => ControlType.Integer,
            "decimal" or "numeric" or "money" or "smallmoney" or "float" or "real" => ControlType.Decimal,
            _ => ControlType.Text
        };
    }
}
