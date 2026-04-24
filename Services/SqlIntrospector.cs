using System.Data;
using System.Data.SqlClient;
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
}
#pragma warning restore CS0618
