using System.IO;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using sp2rdlGenExtension.Model;

namespace sp2rdlGenExtension.Services;

internal static class SqlProcedureColumnSuggester
{
    public static IReadOnlyList<DatasetField> SuggestFields(string procedureDefinition)
    {
        if (string.IsNullOrWhiteSpace(procedureDefinition))
        {
            return [];
        }

        var parser = new TSql160Parser(initialQuotedIdentifiers: false);
        using var reader = new StringReader(procedureDefinition);
        var fragment = parser.Parse(reader, out var errors);
        if (errors.Count > 0 || fragment is null)
        {
            return [];
        }

        var visitor = new LastSelectVisitor();
        fragment.Accept(visitor);

        if (visitor.LastQuerySpecification is null)
        {
            return [];
        }

        var fields = new List<DatasetField>();
        foreach (var selectElement in visitor.LastQuerySpecification.SelectElements)
        {
            if (selectElement is SelectStarExpression)
            {
                return [];
            }

            if (selectElement is not SelectScalarExpression scalarExpression)
            {
                continue;
            }

            var fieldName = GetFieldName(scalarExpression, fields.Count + 1);
            fields.Add(new DatasetField(fieldName, "nvarchar", true, fields.Count + 1));
        }

        return fields;
    }

    private static string GetFieldName(SelectScalarExpression expression, int ordinal)
    {
        if (!string.IsNullOrWhiteSpace(expression.ColumnName?.Value))
        {
            return expression.ColumnName.Value;
        }

        if (expression.Expression is ColumnReferenceExpression columnReference
            && columnReference.MultiPartIdentifier?.Identifiers.Count > 0)
        {
            return columnReference.MultiPartIdentifier.Identifiers[^1].Value;
        }

        return $"Column{ordinal}";
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
