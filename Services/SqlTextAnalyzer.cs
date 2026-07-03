using System.IO;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using sp2rdlGenExtension.Model;

namespace sp2rdlGenExtension.Services;

internal static class SqlTextAnalyzer
{
    /// <summary>
    /// Returns parameter names referenced by the SQL text, excluding variables declared inside the text.
    /// </summary>
    public static IReadOnlyList<string> FindUndeclaredParameterNames(string sqlText)
    {
        if (string.IsNullOrWhiteSpace(sqlText) || !TryParse(sqlText, out var fragment))
        {
            return [];
        }

        var visitor = new ParameterVisitor();
        fragment.Accept(visitor);

        return visitor.ReferencedVariables
            .Except(visitor.DeclaredVariables, StringComparer.OrdinalIgnoreCase)
            .Where(name => !name.StartsWith("@@", StringComparison.Ordinal))
            .Select(name => name.TrimStart('@'))
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Suggests fields from one unambiguous top-level result-producing SELECT in SQL text.
    /// </summary>
    public static SqlTextAnalysisResult SuggestFieldsFromFinalResultSelect(string sqlText)
    {
        if (string.IsNullOrWhiteSpace(sqlText))
        {
            return new SqlTextAnalysisResult([]);
        }

        if (!TryParse(sqlText, out var fragment))
        {
            return new SqlTextAnalysisResult([], "SQL text could not be parsed. Add columns manually or simplify the SQL.");
        }

        var visitor = new TopLevelResultSelectVisitor();
        fragment.Accept(visitor);

        if (visitor.ResultSelects.Count > 1)
        {
            return new SqlTextAnalysisResult(
                [],
                "More than one top-level result SELECT was detected. Columns were not auto-filled; keep one final SELECT or add columns manually.");
        }

        if (visitor.ResultSelects.Count == 0)
        {
            return new SqlTextAnalysisResult([], "No top-level result-producing SELECT was detected.");
        }

        return new SqlTextAnalysisResult(BuildFields(visitor.ResultSelects[0]));
    }

    /// <summary>
    /// Builds dataset fields from a SELECT list using conservative SQL type inference.
    /// </summary>
    internal static IReadOnlyList<DatasetField> BuildFields(QuerySpecification querySpecification)
    {
        var fields = new List<DatasetField>();
        foreach (var selectElement in querySpecification.SelectElements)
        {
            if (selectElement is SelectStarExpression)
            {
                return [];
            }

            if (selectElement is SelectScalarExpression scalarExpression)
            {
                var fieldName = GetFieldName(scalarExpression, fields.Count + 1);
                var sqlTypeName = InferSqlTypeName(scalarExpression.Expression);
                fields.Add(new DatasetField(
                    fieldName,
                    sqlTypeName,
                    true,
                    fields.Count + 1,
                    DatasetFieldDraft.GetDefaultFormat(sqlTypeName)));
            }
        }

        return fields;
    }

    /// <summary>
    /// Parses SQL text with the ScriptDom parser used by the generator.
    /// </summary>
    internal static bool TryParse(string sqlText, out TSqlFragment fragment)
    {
        var parser = new TSql160Parser(initialQuotedIdentifiers: false);
        using var reader = new StringReader(sqlText);
        fragment = parser.Parse(reader, out var errors);
        return errors.Count == 0 && fragment is not null;
    }

    /// <summary>
    /// Infers a practical SQL type for a scalar expression when metadata is unavailable.
    /// </summary>
    internal static string InferSqlTypeName(ScalarExpression expression)
        => expression switch
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

    /// <summary>
    /// Reads a stable field name from a SELECT scalar expression.
    /// </summary>
    internal static string GetFieldName(SelectScalarExpression expression, int ordinal)
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

    private sealed class ParameterVisitor : TSqlFragmentVisitor
    {
        public HashSet<string> DeclaredVariables { get; } = new(StringComparer.OrdinalIgnoreCase);

        public HashSet<string> ReferencedVariables { get; } = new(StringComparer.OrdinalIgnoreCase);

        public override void ExplicitVisit(DeclareVariableElement node)
        {
            if (!string.IsNullOrWhiteSpace(node.VariableName?.Value))
            {
                DeclaredVariables.Add(node.VariableName.Value);
            }

            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(VariableReference node)
        {
            if (!string.IsNullOrWhiteSpace(node.Name))
            {
                ReferencedVariables.Add(node.Name);
            }

            base.ExplicitVisit(node);
        }
    }

    private sealed class TopLevelResultSelectVisitor : TSqlFragmentVisitor
    {
        private int statementDepth;

        public List<QuerySpecification> ResultSelects { get; } = new();

        public override void ExplicitVisit(InsertStatement node)
        {
            this.statementDepth++;
            base.ExplicitVisit(node);
            this.statementDepth--;
        }

        public override void ExplicitVisit(SelectStatement node)
        {
            if (this.statementDepth == 0
                && node.Into is null
                && FindFirstQuerySpecification(node.QueryExpression) is { } querySpecification
                && IsResultProducingSelect(querySpecification))
            {
                ResultSelects.Add(querySpecification);
            }

            this.statementDepth++;
            base.ExplicitVisit(node);
            this.statementDepth--;
        }

        private static bool IsResultProducingSelect(QuerySpecification querySpecification)
        {
            if (querySpecification.SelectElements.Count == 0)
            {
                return false;
            }

            return querySpecification.SelectElements.Any(element => element is not SelectSetVariable);
        }

        private static QuerySpecification? FindFirstQuerySpecification(QueryExpression? queryExpression)
            => queryExpression switch
            {
                QuerySpecification querySpecification => querySpecification,
                BinaryQueryExpression binaryQueryExpression => FindFirstQuerySpecification(binaryQueryExpression.FirstQueryExpression),
                QueryParenthesisExpression parenthesisExpression => FindFirstQuerySpecification(parenthesisExpression.QueryExpression),
                _ => null
            };
    }
}
