using sp2rdlGenExtension.Model;

namespace sp2rdlGenExtension.Services;

internal sealed record SqlTextAnalysisResult(
    IReadOnlyList<DatasetField> Fields,
    string? Warning = null);
