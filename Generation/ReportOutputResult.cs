namespace sp2rdlGenExtension.Generation;

internal sealed record ReportOutputResult(
    string ReportPath,
    string ModelPath,
    string? LocalizationSeedPath);
