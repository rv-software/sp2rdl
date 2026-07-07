using sp2rdlGenExtension.Model;

namespace sp2rdlGenExtension.Services;

internal static class SqlProcedureColumnSuggester
{
    /// <summary>
    /// Suggests fields from the final result SELECT in a stored procedure definition.
    /// </summary>
    public static IReadOnlyList<DatasetField> SuggestFields(string procedureDefinition)
    {
        if (string.IsNullOrWhiteSpace(procedureDefinition))
        {
            return [];
        }

        return SqlTextAnalyzer.SuggestFieldsFromFinalResultSelect(procedureDefinition).Fields;
    }
}
