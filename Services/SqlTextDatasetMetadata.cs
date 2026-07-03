using sp2rdlGenExtension.Model;

namespace sp2rdlGenExtension.Services;

internal sealed record SqlTextDatasetMetadata(
    IReadOnlyList<SpParameter> Parameters,
    IReadOnlyList<DatasetField> Fields,
    string? ResultSetWarning = null);
