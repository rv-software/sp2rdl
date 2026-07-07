using System.Data.SqlClient;
using sp2rdlGenExtension.Model;

namespace sp2rdlGenExtension.Services;

#pragma warning disable CS0618 // Project decision: use System.Data.SqlClient for VSIX compatibility.
internal sealed class ReportingMetadataWriter
{
    /// <summary>
    /// Upserts one reusable Reporting.ParameterDefinition from the current report parameter row.
    /// </summary>
    public async Task SaveParameterDefinitionAsync(
        string connectionString,
        ReportParameter parameter,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentNullException.ThrowIfNull(parameter);

        var definitionName = GetDefinitionName(parameter);
        if (string.IsNullOrWhiteSpace(definitionName))
        {
            throw new InvalidOperationException("Definition name is required.");
        }

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandTimeout = 30;
        command.CommandText = """
            DECLARE @ComponentTypeId int;

            SELECT @ComponentTypeId = Id
            FROM [Reporting].[ComponentType]
            WHERE [Name] = @ComponentTypeName;

            IF @ComponentTypeId IS NULL
                THROW 51001, 'Reporting.ComponentType row was not found.', 1;

            MERGE [Reporting].[ParameterDefinition] AS T
            USING
            (
                SELECT
                    @Name AS [Name],
                    @Label AS Label,
                    @ComponentTypeId AS ComponentTypeId,
                    @EntityKey AS EntityKey,
                    @ValueFieldTemplate AS ValueFieldTemplate,
                    @DisplayFieldTemplate AS DisplayFieldTemplate,
                    @InitialValue AS InitialValue,
                    @DefaultSortOrder AS DefaultSortOrder
            ) AS S
               ON T.[Name] = S.[Name]
            WHEN MATCHED THEN
                UPDATE SET
                    Label = S.Label,
                    ComponentTypeId = S.ComponentTypeId,
                    EntityKey = S.EntityKey,
                    ValueFieldTemplate = S.ValueFieldTemplate,
                    DisplayFieldTemplate = S.DisplayFieldTemplate,
                    InitialValue = S.InitialValue,
                    DefaultSortOrder = S.DefaultSortOrder,
                    LastModifiedBy = @AuditUser,
                    LastModifiedAt = @Now
            WHEN NOT MATCHED THEN
                INSERT ([Name], Label, ComponentTypeId, EntityKey, ValueFieldTemplate, DisplayFieldTemplate, InitialValue, DefaultSortOrder, CreatedBy, CreatedAt)
                VALUES (S.[Name], S.Label, S.ComponentTypeId, S.EntityKey, S.ValueFieldTemplate, S.DisplayFieldTemplate, S.InitialValue, S.DefaultSortOrder, @AuditUser, @Now);
            """;

        command.Parameters.AddWithValue("@Name", definitionName);
        command.Parameters.AddWithValue("@Label", string.IsNullOrWhiteSpace(parameter.Prompt) ? DBNull.Value : parameter.Prompt.Trim());
        command.Parameters.AddWithValue("@ComponentTypeName", MapComponentTypeName(parameter.ControlType));
        command.Parameters.AddWithValue("@EntityKey", parameter.EntityKey?.Trim() ?? string.Empty);
        command.Parameters.AddWithValue("@ValueFieldTemplate", parameter.ValueFieldTemplate?.Trim() ?? string.Empty);
        command.Parameters.AddWithValue("@DisplayFieldTemplate", parameter.DisplayFieldTemplate?.Trim() ?? string.Empty);
        command.Parameters.AddWithValue("@InitialValue", string.IsNullOrWhiteSpace(parameter.DefaultValueExpression) ? DBNull.Value : parameter.DefaultValueExpression.Trim());
        command.Parameters.AddWithValue("@DefaultSortOrder", 0);
        command.Parameters.AddWithValue("@AuditUser", 0);
        command.Parameters.AddWithValue("@Now", DateTime.Now);

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>
    /// Resolves the definition name used to persist reusable runtime parameter metadata.
    /// </summary>
    private static string GetDefinitionName(ReportParameter parameter)
    {
        var definitionName = parameter.DefinitionName?.Trim().TrimStart('@');
        return string.IsNullOrWhiteSpace(definitionName)
            ? parameter.Name.Trim().TrimStart('@')
            : definitionName;
    }

    /// <summary>
    /// Maps generator control types to Reporting.ComponentType names.
    /// </summary>
    private static string MapComponentTypeName(ControlType controlType)
        => controlType == ControlType.Boolean ? "Checkbox" : controlType.ToString();
}
#pragma warning restore CS0618
