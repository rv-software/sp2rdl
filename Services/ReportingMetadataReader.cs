using System.Data.SqlClient;
using System.Globalization;
using System.Text.Json;
using sp2rdlGenExtension.Model;

namespace sp2rdlGenExtension.Services;

#pragma warning disable CS0618 // Project decision: use System.Data.SqlClient for VSIX compatibility.
internal sealed class ReportingMetadataReader
{
    /// <summary>
    /// Reads currently active report versions that can be used as a source for parameter import.
    /// </summary>
    public async Task<IReadOnlyList<ReportingReportVersionChoice>> ListReportVersionsAsync(
        string connectionString,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        const string sql = """
            SELECT
                RV.Id,
                R.InternalName,
                R.[Description],
                RV.Number,
                RV.Code,
                RV.Label,
                RV.ValidFrom
            FROM [Reporting].[ReportVersion] AS RV
            INNER JOIN [Reporting].[Report] AS R ON R.Id = RV.ReportId
            WHERE R.IsActive = 1
              AND CONVERT(date, SYSDATETIME()) BETWEEN CONVERT(date, RV.ValidFrom)
                  AND CONVERT(date, COALESCE(RV.ValidTo, CONVERT(datetime2(7), '99991231', 112)))
            ORDER BY R.InternalName, RV.Number DESC, RV.ValidFrom DESC;
            """;

        using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        using var command = new SqlCommand(sql, connection);
        command.CommandTimeout = 15;

        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var versions = new List<ReportingReportVersionChoice>();
        while (await reader.ReadAsync(cancellationToken))
        {
            var versionId = reader.GetInt32(0);
            var internalName = reader.GetString(1);
            var description = await ReadNullableStringAsync(reader, 2, cancellationToken);
            var number = reader.GetInt32(3);
            var code = reader.GetString(4);
            var label = await ReadNullableStringAsync(reader, 5, cancellationToken);
            var validFrom = reader.GetDateTime(6);
            var displayName = $"{internalName} - {code} ({validFrom:yyyy-MM-dd})";
            if (!string.IsNullOrWhiteSpace(label))
            {
                displayName += $" - {label}";
            }

            versions.Add(new ReportingReportVersionChoice(versionId, internalName, description, number, code, label, validFrom, displayName));
        }

        return versions;
    }

    /// <summary>
    /// Reads report parameters and dependencies for one report version so they can be imported into the grid.
    /// </summary>
    public async Task<IReadOnlyList<ReportingParameterImportCandidate>> ReadParametersForVersionAsync(
        string connectionString,
        int versionId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        var parameters = await ReadImportParametersAsync(connection, versionId, cancellationToken);
        var dependencies = await ReadImportDependenciesAsync(connection, versionId, cancellationToken);

        foreach (var parameter in parameters)
        {
            parameter.Dependencies.AddRange(dependencies.Where(dependency =>
                string.Equals(dependency.ParameterName, parameter.Name, StringComparison.OrdinalIgnoreCase)));
        }

        return parameters
            .OrderBy(parameter => parameter.CreationOrder <= 0 ? int.MaxValue : parameter.CreationOrder)
            .ThenBy(parameter => parameter.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Reads reusable parameter definitions from the Reporting schema and indexes them by normalized name.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, ReportingParameterDefinition>> ReadParameterDefinitionsAsync(
        string connectionString,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        const string sql = """
            SELECT
                PD.[Name],
                PD.Label,
                CT.[Name] AS ComponentTypeName,
                PD.EntityKey,
                PD.ValueFieldTemplate,
                PD.DisplayFieldTemplate,
                PD.InitialValue,
                PD.DefaultSortOrder
            FROM [Reporting].[ParameterDefinition] AS PD
            INNER JOIN [Reporting].[ComponentType] AS CT ON CT.Id = PD.ComponentTypeId
            ORDER BY PD.[Name];
            """;

        using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        using var command = new SqlCommand(sql, connection);
        command.CommandTimeout = 15;

        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var definitions = new Dictionary<string, ReportingParameterDefinition>(StringComparer.OrdinalIgnoreCase);
        while (await reader.ReadAsync(cancellationToken))
        {
            var definition = new ReportingParameterDefinition(
                reader.GetString(0),
                await ReadNullableStringAsync(reader, 1, cancellationToken),
                reader.GetString(2),
                await ReadNullableStringAsync(reader, 3, cancellationToken),
                await ReadNullableStringAsync(reader, 4, cancellationToken),
                await ReadNullableStringAsync(reader, 5, cancellationToken),
                await ReadNullableStringAsync(reader, 6, cancellationToken),
                await reader.IsDBNullAsync(7, cancellationToken) ? null : reader.GetInt32(7));

            var key = NormalizeParameterName(definition.Name);
            if (!string.IsNullOrWhiteSpace(key))
            {
                definitions[key] = definition;
            }
        }

        return definitions;
    }

    /// <summary>
    /// Reads parameter rows for one report version without dependency data.
    /// </summary>
    private static async Task<List<ReportingParameterImportCandidate>> ReadImportParametersAsync(
        SqlConnection connection,
        int versionId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT
                UP.Id,
                UP.CreationOrder,
                COALESCE(NULLIF(UP.NameOverride, ''), PD.[Name]) AS [Name],
                PD.[Name] AS DefinitionName,
                COALESCE(UP.LabelOverride, PD.Label) AS Label,
                CT.[Name] AS ComponentTypeName,
                PD.EntityKey,
                PD.ValueFieldTemplate,
                COALESCE(UP.DisplayFieldTemplateOverride, PD.DisplayFieldTemplate) AS DisplayFieldTemplate,
                COALESCE(UP.InitialValueOverride, PD.InitialValue) AS InitialValue,
                UP.IsRequired,
                UP.IsAdditional,
                UP.IsVisible,
                UP.StaticValues,
                UP.RuntimeSettings
            FROM [Reporting].[UiParameter] AS UP
            INNER JOIN [Reporting].[ParameterDefinition] AS PD ON PD.Id = UP.ParameterDefinitionId
            INNER JOIN [Reporting].[ComponentType] AS CT ON CT.Id = PD.ComponentTypeId
            WHERE UP.VersionId = @VersionId
            ORDER BY UP.CreationOrder, PD.[Name];
            """;

        using var command = new SqlCommand(sql, connection);
        command.CommandTimeout = 15;
        command.Parameters.AddWithValue("@VersionId", versionId);

        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var parameters = new List<ReportingParameterImportCandidate>();
        while (await reader.ReadAsync(cancellationToken))
        {
            var staticValuesJson = await ReadNullableStringAsync(reader, 13, cancellationToken);
            var runtimeSettingsJson = await ReadNullableStringAsync(reader, 14, cancellationToken);
            parameters.Add(new ReportingParameterImportCandidate
            {
                SourceUiParameterId = reader.GetInt32(0),
                CreationOrder = await reader.IsDBNullAsync(1, cancellationToken) ? 0 : Convert.ToInt32(reader.GetValue(1), CultureInfo.InvariantCulture),
                Name = reader.GetString(2),
                DefinitionName = reader.GetString(3),
                Label = await ReadNullableStringAsync(reader, 4, cancellationToken),
                ComponentTypeName = reader.GetString(5),
                EntityKey = await ReadNullableStringAsync(reader, 6, cancellationToken),
                ValueFieldTemplate = await ReadNullableStringAsync(reader, 7, cancellationToken),
                DisplayFieldTemplate = await ReadNullableStringAsync(reader, 8, cancellationToken),
                InitialValue = await ReadNullableStringAsync(reader, 9, cancellationToken),
                IsRequired = reader.GetBoolean(10),
                IsAdditional = reader.GetBoolean(11),
                IsVisible = reader.GetBoolean(12),
                StaticValidValues = ParseStaticValues(staticValuesJson),
                RuntimeSettings = ParseRuntimeSettings(runtimeSettingsJson)
            });
        }

        return parameters;
    }

    /// <summary>
    /// Reads dependency rows for all parameters in one report version.
    /// </summary>
    private static async Task<List<ReportingParameterImportDependency>> ReadImportDependenciesAsync(
        SqlConnection connection,
        int versionId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT
                COALESCE(NULLIF(UP.NameOverride, ''), PD.[Name]) AS ParameterName,
                COALESCE(NULLIF(DependsOnUP.NameOverride, ''), DependsOnPD.[Name]) AS DependsOnParameterName,
                UPD.CompareParams,
                CO.[Name] AS CompareOperator,
                UPD.DependencyFilterPath,
                UPD.ComparisonValueTemplate
            FROM [Reporting].[UiParameterDependency] AS UPD
            INNER JOIN [Reporting].[UiParameter] AS UP ON UP.Id = UPD.UiParameterId
            INNER JOIN [Reporting].[ParameterDefinition] AS PD ON PD.Id = UP.ParameterDefinitionId
            INNER JOIN [Reporting].[UiParameter] AS DependsOnUP ON DependsOnUP.Id = UPD.DependsOnUiParameterId
            INNER JOIN [Reporting].[ParameterDefinition] AS DependsOnPD ON DependsOnPD.Id = DependsOnUP.ParameterDefinitionId
            LEFT JOIN [Reporting].[CompareOperator] AS CO ON CO.Id = UPD.CompareOperatorId
            WHERE UP.VersionId = @VersionId
              AND DependsOnUP.VersionId = @VersionId
            ORDER BY PD.[Name], DependsOnPD.[Name];
            """;

        using var command = new SqlCommand(sql, connection);
        command.CommandTimeout = 15;
        command.Parameters.AddWithValue("@VersionId", versionId);

        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var dependencies = new List<ReportingParameterImportDependency>();
        while (await reader.ReadAsync(cancellationToken))
        {
            dependencies.Add(new ReportingParameterImportDependency(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetBoolean(2),
                await ReadNullableStringAsync(reader, 3, cancellationToken),
                await ReadNullableStringAsync(reader, 4, cancellationToken),
                await ReadNullableStringAsync(reader, 5, cancellationToken)));
        }

        return dependencies;
    }

    /// <summary>
    /// Deserializes UiParameter.StaticValues JSON into grid rows.
    /// </summary>
    private static List<StaticValidValue> ParseStaticValues(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.ValueKind == JsonValueKind.Array
                ? document.RootElement.EnumerateArray()
                    .Select(row => new StaticValidValue
                    {
                        Value = ReadJsonString(row, "valueKey") ?? string.Empty,
                        Label = ReadJsonString(row, "name") ?? ReadJsonString(row, "valueKey") ?? string.Empty
                    })
                    .Where(row => !string.IsNullOrWhiteSpace(row.Value))
                    .ToList()
                : [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>
    /// Deserializes UiParameter.RuntimeSettings JSON into runtime setting grid values.
    /// </summary>
    private static List<ParameterValidatorValue> ParseRuntimeSettings(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.ValueKind == JsonValueKind.Array
                ? document.RootElement.EnumerateArray()
                    .Select((row, index) => new ParameterValidatorValue
                    {
                        Code = ReadJsonString(row, "code") ?? string.Empty,
                        Kind = ReadJsonString(row, "kind") ?? "validation",
                        Value = ReadJsonValueText(row, "value"),
                        ValueType = InferValueType(row, "value"),
                        SortOrder = ReadJsonInt(row, "sortOrder") ?? index + 1,
                        IsEnabled = true
                    })
                    .Where(row => !string.IsNullOrWhiteSpace(row.Code))
                    .ToList()
                : [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>
    /// Reads a string property from a JSON object when present.
    /// </summary>
    private static string? ReadJsonString(JsonElement element, string propertyName)
        => element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(propertyName, out var property)
            && property.ValueKind != JsonValueKind.Null
            ? property.ToString()
            : null;

    /// <summary>
    /// Reads a JSON value as the text expected by runtime settings editors.
    /// </summary>
    private static string ReadJsonValueText(JsonElement element, string propertyName)
        => element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(propertyName, out var property)
            && property.ValueKind != JsonValueKind.Null
            ? property.ValueKind == JsonValueKind.String ? property.GetString() ?? string.Empty : property.ToString()
            : string.Empty;

    /// <summary>
    /// Infers the closest validator value type from serialized JSON.
    /// </summary>
    private static string InferValueType(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(propertyName, out var property))
        {
            return "string";
        }

        return property.ValueKind switch
        {
            JsonValueKind.True or JsonValueKind.False => "bool",
            JsonValueKind.Number => property.TryGetInt32(out _) ? "int" : "decimal",
            _ => "string"
        };
    }

    /// <summary>
    /// Reads an integer JSON property when present.
    /// </summary>
    private static int? ReadJsonInt(JsonElement element, string propertyName)
        => element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(propertyName, out var property)
            && property.TryGetInt32(out var value)
            ? value
            : null;

    /// <summary>
    /// Converts database NULL values to nullable strings without leaking provider-specific checks.
    /// </summary>
    private static async Task<string?> ReadNullableStringAsync(SqlDataReader reader, int ordinal, CancellationToken cancellationToken)
        => await reader.IsDBNullAsync(ordinal, cancellationToken) ? null : reader.GetString(ordinal);

    /// <summary>
    /// Normalizes report parameter names so database rows and grid rows can be matched consistently.
    /// </summary>
    private static string NormalizeParameterName(string? value)
        => string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim().TrimStart('@');
}
#pragma warning restore CS0618

internal sealed record ReportingParameterDefinition(
    string Name,
    string? Label,
    string ComponentTypeName,
    string? EntityKey,
    string? ValueFieldTemplate,
    string? DisplayFieldTemplate,
    string? InitialValue,
    int? DefaultSortOrder);

internal sealed record ReportingReportVersionChoice(
    int VersionId,
    string InternalName,
    string? Description,
    int Number,
    string Code,
    string? Label,
    DateTime ValidFrom,
    string DisplayName);

internal sealed class ReportingParameterImportCandidate
{
    public bool IsSelected { get; set; } = true;

    public int SourceUiParameterId { get; set; }

    public int CreationOrder { get; set; }

    public string Name { get; set; } = string.Empty;

    public string DefinitionName { get; set; } = string.Empty;

    public string? Label { get; set; }

    public string ComponentTypeName { get; set; } = string.Empty;

    public string? EntityKey { get; set; }

    public string? ValueFieldTemplate { get; set; }

    public string? DisplayFieldTemplate { get; set; }

    public string? InitialValue { get; set; }

    public bool IsRequired { get; set; }

    public bool IsAdditional { get; set; }

    public bool IsVisible { get; set; } = true;

    public List<StaticValidValue> StaticValidValues { get; set; } = new();

    public List<ParameterValidatorValue> RuntimeSettings { get; set; } = new();

    public List<ReportingParameterImportDependency> Dependencies { get; } = new();
}

internal sealed record ReportingParameterImportDependency(
    string ParameterName,
    string DependsOnParameterName,
    bool CompareParams,
    string? CompareOperator,
    string? DependencyFilterPath,
    string? ComparisonValueTemplate);
