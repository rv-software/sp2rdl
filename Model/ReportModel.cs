namespace sp2rdlGenExtension.Model;

internal sealed class ReportModel
{
    public int SchemaVersion { get; set; } = 1;

    public string Name { get; set; } = string.Empty;

    public string? Description { get; set; }

    public string? Author { get; set; }

    public OutputMode OutputMode { get; set; } = OutputMode.Rdl;

    public string? SourceConnectionString { get; set; }

    public string? SourceStoredProcedureName { get; set; }

    public string? OutputPath { get; set; }

    public string SharedDataSourceName { get; set; } = "MainDS";

    public string MainDatasetName { get; set; } = "DsMain";

    public List<DatasetConfig> Datasets { get; set; } = new();

    public List<ReportParameter> Parameters { get; set; } = new();

    public PageSetupConfig PageSetup { get; set; } = new();

    public PageHeaderConfig PageHeader { get; set; } = new();

    public PageFooterConfig PageFooter { get; set; } = new();
}
