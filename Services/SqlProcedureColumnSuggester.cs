using System.IO;
using System.Linq;
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
            var sqlTypeName = InferSqlTypeName(scalarExpression.Expression);
            fields.Add(new DatasetField(
                fieldName,
                sqlTypeName,
                true,
                fields.Count + 1,
                DatasetFieldDraft.GetDefaultFormat(sqlTypeName)));
        }

        return fields;
    }

    private static string InferSqlTypeName(ScalarExpression expression)
    {
        return expression switch
        {
            CastCall castCall => ReadDataType(castCall.DataType) ?? "nvarchar",
            ConvertCall convertCall => ReadDataType(convertCall.DataType) ?? "nvarchar",
            FunctionCall functionCall => InferFunctionSqlTypeName(functionCall),
            IntegerLiteral => "int",
            NumericLiteral => "decimal(18,2)",
            MoneyLiteral => "money",
            StringLiteral => "nvarchar",
            BinaryLiteral => "varbinary",
            NullLiteral => "nvarchar",
            ParenthesisExpression parenthesisExpression => InferSqlTypeName(parenthesisExpression.Expression),
            UnaryExpression unaryExpression => InferSqlTypeName(unaryExpression.Expression),
            BinaryExpression binaryExpression => InferBinaryExpressionSqlTypeName(binaryExpression),
            _ => "nvarchar"
        };
    }

    private static string InferFunctionSqlTypeName(FunctionCall functionCall)
    {
        var functionName = functionCall.FunctionName?.Value?.ToLowerInvariant();

        return functionName switch
        {
            "count" => "int",
            "count_big" => "bigint",
            "sum" or "avg" => "decimal(18,2)",
            "min" or "max" when functionCall.Parameters.Count > 0 => InferSqlTypeName(functionCall.Parameters[0]),
            "getdate" or "sysdatetime" or "current_timestamp" => "datetime",
            "isnull" or "coalesce" when functionCall.Parameters.Count > 0 => InferFirstKnownParameterType(functionCall),
            _ => "nvarchar"
        };
    }

    private static string InferFirstKnownParameterType(FunctionCall functionCall)
    {
        foreach (var parameter in functionCall.Parameters)
        {
            var sqlTypeName = InferSqlTypeName(parameter);
            if (!IsFallbackType(sqlTypeName))
            {
                return sqlTypeName;
            }
        }

        return "nvarchar";
    }

    private static string InferBinaryExpressionSqlTypeName(BinaryExpression binaryExpression)
    {
        var firstType = InferSqlTypeName(binaryExpression.FirstExpression);
        var secondType = InferSqlTypeName(binaryExpression.SecondExpression);

        if (IsDecimalLike(firstType) || IsDecimalLike(secondType))
        {
            return "decimal(18,2)";
        }

        if (IsIntegerLike(firstType) && IsIntegerLike(secondType))
        {
            return "int";
        }

        return "nvarchar";
    }

    private static bool IsFallbackType(string sqlTypeName)
        => string.Equals(sqlTypeName, "nvarchar", StringComparison.OrdinalIgnoreCase);

    private static bool IsIntegerLike(string sqlTypeName)
    {
        var normalized = NormalizeBaseType(sqlTypeName);
        return normalized is "tinyint" or "smallint" or "int" or "bigint";
    }

    private static bool IsDecimalLike(string sqlTypeName)
    {
        var normalized = NormalizeBaseType(sqlTypeName);
        return normalized is "decimal" or "numeric" or "money" or "smallmoney" or "float" or "real";
    }

    private static string NormalizeBaseType(string sqlTypeName)
        => sqlTypeName.Split('(', 2)[0].Trim().ToLowerInvariant();

    private static string? ReadDataType(DataTypeReference? dataType)
    {
        if (dataType is null)
        {
            return null;
        }

        if (dataType.FirstTokenIndex < 0
            || dataType.LastTokenIndex < dataType.FirstTokenIndex
            || dataType.ScriptTokenStream is null)
        {
            return null;
        }

        return string.Concat(
            dataType.ScriptTokenStream
                .Skip(dataType.FirstTokenIndex)
                .Take(dataType.LastTokenIndex - dataType.FirstTokenIndex + 1)
                .Select(token => token.Text));
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
