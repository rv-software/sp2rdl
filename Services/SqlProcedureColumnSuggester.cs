using Microsoft.SqlServer.TransactSql.ScriptDom;
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

        if (!SqlTextAnalyzer.TryParse(procedureDefinition, out var fragment))
        {
            return [];
        }

        var visitor = new LastSelectVisitor();
        fragment.Accept(visitor);

        return visitor.LastQuerySpecification is null
            ? []
            : SqlTextAnalyzer.BuildFields(visitor.LastQuerySpecification);
    }

    private sealed class LastSelectVisitor : TSqlFragmentVisitor
    {
        public QuerySpecification? LastQuerySpecification { get; private set; }

        public override void ExplicitVisit(QuerySpecification node)
        {
            LastQuerySpecification = node;
            base.ExplicitVisit(node);
        }
    }
}
