using System.Data;
using System.Data.SqlClient;
using System.Globalization;
using sp2rdlGenExtension.Model;

namespace sp2rdlGenExtension.Services;

#pragma warning disable CS0618 // Project decision: use System.Data.SqlClient for VSIX compatibility.
internal sealed class SqlIntrospector
{
    public async Task<IReadOnlyList<StoredProcedureSummary>> ListStoredProceduresAsync(
        string connectionString,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        const string sql = """
            SELECT
                s.name AS schema_name,
                o.name AS procedure_name
            FROM sys.objects o
            INNER JOIN sys.schemas s ON o.schema_id = s.schema_id
            WHERE o.type IN ('P', 'PC')
            ORDER BY s.name, o.name;
            """;

        using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        using var command = new SqlCommand(sql, connection);
        command.CommandTimeout = 15;
        using var reader = await command.ExecuteReaderAsync(cancellationToken);

        var procedures = new List<StoredProcedureSummary>();
        while (await reader.ReadAsync(cancellationToken))
        {
            procedures.Add(new StoredProcedureSummary(
                reader.GetString(0),
                reader.GetString(1)));
        }

        return procedures;
    }

    public async Task<StoredProcedureMetadata> ReadStoredProcedureAsync(
        string connectionString,
        string storedProcedureName,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentException.ThrowIfNullOrWhiteSpace(storedProcedureName);

        var procedure = StoredProcedureIdentifier.Parse(storedProcedureName);

        using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        var parameters = await ReadParametersAsync(connection, procedure, cancellationToken);
        IReadOnlyList<DatasetField> fields;
        string? resultSetWarning = null;

        try
        {
            fields = await ReadResultFieldsAsync(connection, procedure, cancellationToken);
        }
        catch (Exception ex) when (ex is SqlException or InvalidOperationException)
        {
            fields = [];
            resultSetWarning = $"Result columns could not be detected automatically. Add them manually. Details: {ex.Message}";
        }

        return new StoredProcedureMetadata(
            procedure.SchemaName,
            procedure.ProcedureName,
            parameters,
            fields,
            resultSetWarning);
    }

    public async Task<IReadOnlyList<DatasetField>> SuggestFieldsFromProcedureTextAsync(
        string connectionString,
        string storedProcedureName,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentException.ThrowIfNullOrWhiteSpace(storedProcedureName);

        var procedure = StoredProcedureIdentifier.Parse(storedProcedureName);

        const string sql = """
            SELECT OBJECT_DEFINITION(OBJECT_ID(@qualifiedName));
            """;

        using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        using var command = new SqlCommand(sql, connection);
        command.CommandTimeout = 15;
        command.Parameters.Add("@qualifiedName", SqlDbType.NVarChar, 300).Value = procedure.DisplayName;

        var definition = await command.ExecuteScalarAsync(cancellationToken) as string;
        return SqlProcedureColumnSuggester.SuggestFields(definition ?? string.Empty);
    }

    /// <summary>
    /// Reads parameter and result-column metadata for a raw SQL dataset without executing the SQL text.
    /// </summary>
    public async Task<SqlTextDatasetMetadata> ReadSqlTextDatasetAsync(
        string connectionString,
        string sqlText,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentException.ThrowIfNullOrWhiteSpace(sqlText);

        using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        var parameters = await ReadSqlTextParametersAsync(connection, sqlText, cancellationToken);
        IReadOnlyList<DatasetField> fields;
        string? resultSetWarning = null;

        try
        {
            fields = await ReadSqlTextResultFieldsAsync(connection, sqlText, BuildSqlTextParameterDefinition(parameters), cancellationToken);
        }
        catch (Exception ex) when (ex is SqlException or InvalidOperationException or IndexOutOfRangeException)
        {
            var fallback = SqlTextAnalyzer.SuggestFieldsFromFinalResultSelect(sqlText);
            fields = fallback.Fields;
            resultSetWarning = fallback.Warning
                ?? $"SQL Server metadata could not describe result columns; parser fallback suggested {fields.Count} column(s). Details: {ex.Message}";
        }

        return new SqlTextDatasetMetadata(parameters, fields, resultSetWarning);
    }

    /// <summary>
    /// Suggests fields from raw SQL text using the final top-level SELECT parser.
    /// </summary>
    public SqlTextAnalysisResult SuggestFieldsFromSqlText(string sqlText)
        => SqlTextAnalyzer.SuggestFieldsFromFinalResultSelect(sqlText);

    /// <summary>
    /// Builds conservative parameter metadata directly from SQL text parser output.
    /// </summary>
    public IReadOnlyList<SpParameter> SuggestParametersFromSqlText(string sqlText)
        => BuildFallbackSqlTextParameters(SqlTextAnalyzer.FindUndeclaredParameterNames(sqlText));

    public async Task<LookupSqlSuggestion?> SuggestLookupSqlAsync(
        string connectionString,
        string parameterName,
        string? dependsOnParameterName,
        bool dependsOnIsMultiValue,
        bool parameterIsNullable,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentException.ThrowIfNullOrWhiteSpace(parameterName);

        var normalizedParameterName = parameterName.Trim().TrimStart('@');
        if (string.IsNullOrWhiteSpace(normalizedParameterName))
        {
            return null;
        }

        const string sql = """
            ;WITH PrimaryKeyColumns AS
            (
                SELECT
                    s.name AS schema_name,
                    t.name AS table_name,
                    pk.name AS pk_name,
                    c.name AS pk_column_name,
                    COUNT(*) OVER (PARTITION BY pk.object_id) AS pk_column_count
                FROM sys.key_constraints pk
                INNER JOIN sys.tables t ON pk.parent_object_id = t.object_id
                INNER JOIN sys.schemas s ON t.schema_id = s.schema_id
                INNER JOIN sys.index_columns ic
                    ON pk.parent_object_id = ic.object_id
                    AND pk.unique_index_id = ic.index_id
                INNER JOIN sys.columns c
                    ON ic.object_id = c.object_id
                    AND ic.column_id = c.column_id
                WHERE pk.type = 'PK'
            ),
            CandidateTables AS
            (
                SELECT
                    pk.schema_name,
                    pk.table_name,
                    pk.pk_column_name,
                    ROW_NUMBER() OVER
                    (
                        ORDER BY
                            CASE WHEN EXISTS
                            (
                                SELECT 1
                                FROM sys.columns label_column
                                WHERE label_column.object_id = t.object_id
                                  AND label_column.name LIKE '%Name%'
                            ) THEN 0 ELSE 1 END,
                            s.name,
                            t.name
                    ) AS table_rank
                FROM PrimaryKeyColumns pk
                INNER JOIN sys.tables t ON t.name = pk.table_name
                INNER JOIN sys.schemas s ON s.schema_id = t.schema_id AND s.name = pk.schema_name
                WHERE pk.pk_column_count = 1
                  AND pk.pk_column_name = @parameterName
            )
            SELECT TOP (1)
                schema_name,
                table_name,
                pk_column_name
            FROM CandidateTables
            WHERE table_rank = 1;
            """;

        using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        using var command = new SqlCommand(sql, connection);
        command.CommandTimeout = 15;
        command.Parameters.Add("@parameterName", SqlDbType.NVarChar, 128).Value = normalizedParameterName;

        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var schemaName = reader.GetString(0);
        var tableName = reader.GetString(1);
        var valueField = reader.GetString(2);
        await reader.CloseAsync();

        var nameFields = await ReadNameColumnsAsync(connection, schemaName, tableName, cancellationToken);
        var dependencyFilterColumn = string.IsNullOrWhiteSpace(dependsOnParameterName)
            ? null
            : await FindColumnAsync(connection, schemaName, tableName, dependsOnParameterName.Trim().TrimStart('@'), cancellationToken);
        var labelField = nameFields.FirstOrDefault() ?? valueField;
        var labelExpression = string.Equals(labelField, valueField, StringComparison.OrdinalIgnoreCase)
            ? $"CAST({QuoteIdentifier(labelField)} AS nvarchar(4000))"
            : $"CONCAT(CAST({QuoteIdentifier(labelField)} AS nvarchar(4000)), N' (', CAST({QuoteIdentifier(valueField)} AS nvarchar(100)), N')')";

        var dependencyOperator = dependsOnIsMultiValue ? "IN" : "=";
        var dependencyPredicateValue = dependsOnIsMultiValue
            ? $"(@{dependsOnParameterName?.Trim().TrimStart('@')})"
            : $"@{dependsOnParameterName?.Trim().TrimStart('@')}";
        var whereClause = string.IsNullOrWhiteSpace(dependencyFilterColumn) || string.IsNullOrWhiteSpace(dependsOnParameterName)
            ? string.Empty
            : $"""
            WHERE @{dependsOnParameterName.Trim().TrimStart('@')} IS NOT NULL
              AND {QuoteIdentifier(dependencyFilterColumn)} {dependencyOperator} {dependencyPredicateValue}

            """;
        var lookupSql = parameterIsNullable
            ? $"""
            SELECT [Value], [Label]
            FROM
            (
                SELECT
                    0 AS [SortOrder],
                    NULL AS [Value],
                    N'(All)' AS [Label]
                UNION ALL
                SELECT TOP (1000)
                    1 AS [SortOrder],
                    {QuoteIdentifier(valueField)} AS [Value],
                    {labelExpression} AS [Label]
                FROM {QuoteIdentifier(schemaName)}.{QuoteIdentifier(tableName)}
            {whereClause}) AS [sp2rdlLookup]
            ORDER BY [SortOrder], [Label];
            """
            : $"""
            SELECT TOP (1000)
                {QuoteIdentifier(valueField)} AS [Value],
                {labelExpression} AS [Label]
            FROM {QuoteIdentifier(schemaName)}.{QuoteIdentifier(tableName)}
            {whereClause}ORDER BY {QuoteIdentifier(labelField)};
            """;

        return new LookupSqlSuggestion(
            lookupSql,
            BuildLookupDatasetName(normalizedParameterName),
            "Value",
            "Label",
            $"{schemaName}.{tableName}");
    }

    public async Task<DataTable> PreviewSqlAsync(
        string connectionString,
        string sql,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentException.ThrowIfNullOrWhiteSpace(sql);

        using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        using var command = new SqlCommand($"SET FMTONLY OFF; SET ROWCOUNT 100;{Environment.NewLine}{sql}{Environment.NewLine}SET ROWCOUNT 0;", connection);
        command.CommandTimeout = 15;

        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var table = new DataTable();
        table.Load(reader);
        return table;
    }

    private static async Task<IReadOnlyList<string>> ReadNameColumnsAsync(
        SqlConnection connection,
        string schemaName,
        string tableName,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT c.name
            FROM sys.tables t
            INNER JOIN sys.schemas s ON t.schema_id = s.schema_id
            INNER JOIN sys.columns c ON c.object_id = t.object_id
            WHERE s.name = @schemaName
              AND t.name = @tableName
              AND c.name LIKE '%Name%'
            ORDER BY
                CASE WHEN c.name = 'Name' THEN 0 ELSE 1 END,
                c.column_id;
            """;

        using var command = new SqlCommand(sql, connection);
        command.CommandTimeout = 15;
        command.Parameters.Add("@schemaName", SqlDbType.NVarChar, 128).Value = schemaName;
        command.Parameters.Add("@tableName", SqlDbType.NVarChar, 128).Value = tableName;

        var fields = new List<string>();
        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            fields.Add(reader.GetString(0));
        }

        return fields;
    }

    private static async Task<string?> FindColumnAsync(
        SqlConnection connection,
        string schemaName,
        string tableName,
        string columnName,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT TOP (1) c.name
            FROM sys.tables t
            INNER JOIN sys.schemas s ON t.schema_id = s.schema_id
            INNER JOIN sys.columns c ON c.object_id = t.object_id
            WHERE s.name = @schemaName
              AND t.name = @tableName
              AND LOWER(c.name) = LOWER(@columnName);
            """;

        using var command = new SqlCommand(sql, connection);
        command.CommandTimeout = 15;
        command.Parameters.Add("@schemaName", SqlDbType.NVarChar, 128).Value = schemaName;
        command.Parameters.Add("@tableName", SqlDbType.NVarChar, 128).Value = tableName;
        command.Parameters.Add("@columnName", SqlDbType.NVarChar, 128).Value = columnName;

        return await command.ExecuteScalarAsync(cancellationToken) as string;
    }

    private static async Task<IReadOnlyList<SpParameter>> ReadParametersAsync(
        SqlConnection connection,
        StoredProcedureIdentifier procedure,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT
                p.name,
                t.name AS type_name,
                p.is_nullable,
                p.has_default_value,
                p.is_output,
                p.parameter_id
            FROM sys.parameters p
            INNER JOIN sys.objects o ON p.object_id = o.object_id
            INNER JOIN sys.schemas s ON o.schema_id = s.schema_id
            INNER JOIN sys.types t ON p.user_type_id = t.user_type_id
            WHERE s.name = @schemaName
              AND o.name = @procedureName
              AND o.type IN ('P', 'PC')
            ORDER BY p.parameter_id;
            """;

        using var command = new SqlCommand(sql, connection);
        command.CommandTimeout = 15;
        command.Parameters.Add("@schemaName", SqlDbType.NVarChar, 128).Value = procedure.SchemaName;
        command.Parameters.Add("@procedureName", SqlDbType.NVarChar, 128).Value = procedure.ProcedureName;

        var parameters = new List<SpParameter>();
        using var reader = await command.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            parameters.Add(new SpParameter(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetBoolean(2),
                reader.GetBoolean(3),
                reader.GetBoolean(4),
                reader.GetInt32(5)));
        }

        return parameters;
    }

    /// <summary>
    /// Reads undeclared SQL text parameters through SQL Server metadata with parser fallback.
    /// </summary>
    private static async Task<IReadOnlyList<SpParameter>> ReadSqlTextParametersAsync(
        SqlConnection connection,
        string sqlText,
        CancellationToken cancellationToken)
    {
        var parserParameterNames = SqlTextAnalyzer.FindUndeclaredParameterNames(sqlText);
        try
        {
            var describedParameters = await DescribeUndeclaredParametersAsync(connection, sqlText, cancellationToken);
            return MergeSqlTextParameterMetadata(describedParameters, parserParameterNames);
        }
        catch (Exception ex) when (ex is SqlException or InvalidOperationException or IndexOutOfRangeException)
        {
            return BuildFallbackSqlTextParameters(parserParameterNames);
        }
    }

    /// <summary>
    /// Adds parser-discovered parameters missing from SQL Server metadata.
    /// </summary>
    private static IReadOnlyList<SpParameter> MergeSqlTextParameterMetadata(
        IReadOnlyList<SpParameter> describedParameters,
        IReadOnlyList<string> parserParameterNames)
    {
        var merged = describedParameters.ToList();
        var knownNames = merged
            .Select(parameter => parameter.Name.TrimStart('@'))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var parserName in parserParameterNames)
        {
            var normalizedName = parserName.TrimStart('@');
            if (string.IsNullOrWhiteSpace(normalizedName) || knownNames.Contains(normalizedName))
            {
                continue;
            }

            merged.Add(new SpParameter(
                "@" + normalizedName,
                "nvarchar",
                true,
                false,
                false,
                merged.Count + 1));
            knownNames.Add(normalizedName);
        }

        return merged;
    }

    /// <summary>
    /// Builds conservative parameter metadata from parser-discovered SQL text parameters.
    /// </summary>
    private static IReadOnlyList<SpParameter> BuildFallbackSqlTextParameters(IReadOnlyList<string> parameterNames)
        => parameterNames
            .Select((name, index) => new SpParameter(
                "@" + name.TrimStart('@'),
                "nvarchar",
                true,
                false,
                false,
                index + 1))
            .ToList();

    /// <summary>
    /// Calls sys.sp_describe_undeclared_parameters for raw SQL text.
    /// </summary>
    private static async Task<IReadOnlyList<SpParameter>> DescribeUndeclaredParametersAsync(
        SqlConnection connection,
        string sqlText,
        CancellationToken cancellationToken)
    {
        using var command = new SqlCommand("sys.sp_describe_undeclared_parameters", connection)
        {
            CommandType = CommandType.StoredProcedure,
            CommandTimeout = 15
        };
        command.Parameters.Add("@tsql", SqlDbType.NVarChar, -1).Value = sqlText;

        var parameters = new List<SpParameter>();
        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var name = ReadNullableString(reader, "name");
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            var sqlTypeName = ReadNullableString(reader, "suggested_system_type_name");
            parameters.Add(new SpParameter(
                name.StartsWith('@') ? name : "@" + name,
                string.IsNullOrWhiteSpace(sqlTypeName) ? "nvarchar" : sqlTypeName,
                true,
                false,
                false,
                parameters.Count + 1));
        }

        return parameters;
    }

    /// <summary>
    /// Reads result columns for raw SQL text through sys.sp_describe_first_result_set.
    /// </summary>
    private static async Task<IReadOnlyList<DatasetField>> ReadSqlTextResultFieldsAsync(
        SqlConnection connection,
        string sqlText,
        string parameterDefinition,
        CancellationToken cancellationToken)
    {
        using var command = new SqlCommand("sys.sp_describe_first_result_set", connection)
        {
            CommandType = CommandType.StoredProcedure,
            CommandTimeout = 15
        };
        command.Parameters.Add("@tsql", SqlDbType.NVarChar, -1).Value = sqlText;
        command.Parameters.Add("@params", SqlDbType.NVarChar, -1).Value = parameterDefinition;
        command.Parameters.Add("@browse_information_mode", SqlDbType.TinyInt).Value = 0;

        var fields = new List<DatasetField>();
        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (HasColumn(reader, "error_number") && reader["error_number"] is not DBNull)
            {
                throw new InvalidOperationException($"Could not describe SQL text result set: {ReadNullableString(reader, "error_message")}");
            }

            if (HasColumn(reader, "is_hidden") && reader["is_hidden"] is bool isHidden && isHidden)
            {
                continue;
            }

            var name = ReadNullableString(reader, "name");
            var systemTypeName = ReadNullableString(reader, "system_type_name");
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(systemTypeName))
            {
                continue;
            }

            fields.Add(new DatasetField(
                name,
                systemTypeName,
                HasColumn(reader, "is_nullable") && reader["is_nullable"] is bool isNullable && isNullable,
                ReadInt32(reader, "column_ordinal", fields.Count + 1),
                DatasetFieldDraft.GetDefaultFormat(systemTypeName)));
        }

        return fields;
    }

    /// <summary>
    /// Builds the @params definition string required by sys.sp_describe_first_result_set.
    /// </summary>
    private static string BuildSqlTextParameterDefinition(IEnumerable<SpParameter> parameters)
        => string.Join(
            ", ",
            parameters
                .Where(parameter => !parameter.IsOutput)
                .Select(parameter => $"{(parameter.Name.StartsWith('@') ? parameter.Name : "@" + parameter.Name)} {parameter.SqlTypeName}"));

    /// <summary>
    /// Checks whether a data reader result contains a metadata column.
    /// </summary>
    private static bool HasColumn(SqlDataReader reader, string columnName)
    {
        for (var index = 0; index < reader.FieldCount; index++)
        {
            if (string.Equals(reader.GetName(index), columnName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Reads an optional string metadata column without assuming it is always present.
    /// </summary>
    private static string? ReadNullableString(SqlDataReader reader, string columnName)
    {
        if (!HasColumn(reader, columnName))
        {
            return null;
        }

        var value = reader[columnName];
        return value is DBNull ? null : value as string ?? value.ToString();
    }

    /// <summary>
    /// Reads an optional integer metadata column with a fallback value.
    /// </summary>
    private static int ReadInt32(SqlDataReader reader, string columnName, int fallback)
    {
        if (!HasColumn(reader, columnName) || reader[columnName] is DBNull)
        {
            return fallback;
        }

        return Convert.ToInt32(reader[columnName], CultureInfo.InvariantCulture);
    }

    private static async Task<IReadOnlyList<DatasetField>> ReadResultFieldsAsync(
        SqlConnection connection,
        StoredProcedureIdentifier procedure,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT
                column_ordinal,
                name,
                system_type_name,
                is_nullable,
                is_hidden,
                error_number,
                error_message
            FROM sys.dm_exec_describe_first_result_set_for_object(OBJECT_ID(@qualifiedName), 0)
            ORDER BY column_ordinal;
            """;

        using var command = new SqlCommand(sql, connection);
        command.CommandTimeout = 15;
        command.Parameters.Add("@qualifiedName", SqlDbType.NVarChar, 300).Value = procedure.DisplayName;

        var fields = new List<DatasetField>();
        using var reader = await command.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            if (reader["error_number"] is not DBNull)
            {
                throw new InvalidOperationException(
                    $"Could not describe result set for {procedure.DisplayName}: {reader["error_message"]}");
            }

            if (reader["is_hidden"] is bool isHidden && isHidden)
            {
                continue;
            }

            var name = reader["name"] as string;
            var systemTypeName = reader["system_type_name"] as string;

            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(systemTypeName))
            {
                continue;
            }

            fields.Add(new DatasetField(
                name,
                systemTypeName,
                reader["is_nullable"] is bool isNullable && isNullable,
                (int)reader["column_ordinal"],
                DatasetFieldDraft.GetDefaultFormat(systemTypeName)));
        }

        return fields;
    }

    private sealed record StoredProcedureIdentifier(string SchemaName, string ProcedureName)
    {
        public string DisplayName => $"{SchemaName}.{ProcedureName}";

        public static StoredProcedureIdentifier Parse(string storedProcedureName)
        {
            var parts = storedProcedureName
                .Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            return parts.Length switch
            {
                1 => new StoredProcedureIdentifier("dbo", parts[0]),
                2 => new StoredProcedureIdentifier(parts[0], parts[1]),
                _ => throw new ArgumentException(
                    "Stored procedure name must be in 'ProcedureName' or 'SchemaName.ProcedureName' format.",
                    nameof(storedProcedureName))
            };
        }
    }

    private static string QuoteIdentifier(string identifier)
        => "[" + identifier.Replace("]", "]]") + "]";

    private static string BuildLookupDatasetName(string parameterName)
        => "ds" + (string.IsNullOrWhiteSpace(parameterName)
            ? "Lookup"
            : char.ToUpperInvariant(parameterName[0]) + parameterName[1..]);
}
#pragma warning restore CS0618
