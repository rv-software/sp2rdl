namespace sp2rdlGenExtension.Model;

internal sealed class ReportModel
{
    public int SchemaVersion { get; set; } = 1;

    public string Name { get; set; } = string.Empty;

    public string? Description { get; set; }

    public string? Author { get; set; }

    public OutputMode OutputMode { get; set; } = OutputMode.Rdl;

    public ReportPurpose Purpose { get; set; } = ReportPurpose.MainReport;

    public string? SourceConnectionString { get; set; }

    public string? SourceStoredProcedureName { get; set; }

    public string? OutputPath { get; set; }

    public string BaseFontFamily { get; set; } = "Arial";

    public string SharedDataSourceName { get; set; } = "dsrMain";

    public string MainDatasetName { get; set; } = "dsMain";

    public List<DatasetConfig> Datasets { get; set; } = new();

    public List<ReportParameter> Parameters { get; set; } = new();

    public ReportTitleConfig ReportTitle { get; set; } = new();

    public CompanyInfoConfig CompanyInfo { get; set; } = new();

    public ReportVariablesConfig ReportVariables { get; set; } = new();

    public MemorandumConfig Memorandum { get; set; } = new();

    public ReportSummaryConfig ReportSummary { get; set; } = new();

    public TablixStyleConfig TablixStyle { get; set; } = new();

    public PageSetupConfig PageSetup { get; set; } = new();

    public PageHeaderConfig PageHeader { get; set; } = new();

    public PageFooterConfig PageFooter { get; set; } = new();
}
