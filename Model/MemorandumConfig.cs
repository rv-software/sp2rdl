namespace sp2rdlGenExtension.Model;

internal sealed class MemorandumConfig
{
    public bool Enabled { get; set; }

    public ReportBandLayoutMode LayoutMode { get; set; } = ReportBandLayoutMode.Inline;

    public string? SubreportPath { get; set; }

    public string? SubreportName { get; set; }

    public bool FallbackToInline { get; set; } = true;

    public string? LogoImagePath { get; set; }

    public string? LogoSourceColumnName { get; set; }

    public string TextTemplate { get; set; } = string.Empty;

    public List<RichTextParagraphConfig> RichTextParagraphs { get; set; } = new();

    public bool ShowVerticalSeparator { get; set; } = true;

    public bool ShowBottomLine { get; set; } = true;

    public double HeightInCentimeters { get; set; } = 2.5d;

    public string LayoutPreset { get; set; } = "LogoLeftTextRight";
}
