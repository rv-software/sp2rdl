namespace sp2rdlGenExtension.Model;

internal sealed class ReportGenerationRequest
{
    public string ConnectionString { get; set; } = string.Empty;

    public string StoredProcedureName { get; set; } = string.Empty;

    public string OutputPath { get; set; } = string.Empty;

    public ReportModel ReportModel { get; set; } = new();
}
