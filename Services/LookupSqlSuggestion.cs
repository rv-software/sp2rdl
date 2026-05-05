namespace sp2rdlGenExtension.Services;

internal sealed record LookupSqlSuggestion(
    string Sql,
    string DatasetName,
    string ValueField,
    string LabelField,
    string SourceTable);
