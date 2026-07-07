namespace sp2rdlGenExtension.Model;

internal enum GroupRenderMode
{
    Band,
    TabularHorizontal,
    MatrixCrosstab
}

internal sealed class TablixStyleConfig
{
    public GroupRenderMode GroupRenderMode { get; set; } = GroupRenderMode.Band;

    public int MatrixExpectedColumnCount { get; set; } = 6;

    public bool ShowTabularHorizontalSubtotals { get; set; } = true;

    public bool ShowTabularHorizontalGrandTotal { get; set; } = true;

    public double WidthPercent { get; set; } = 100.0d;

    public string ShadeBaseColor { get; set; } = "#EDEDED";

    public string BorderColor { get; set; } = "#A6A6A6";

    public double BorderWidthInPoints { get; set; } = 0.5d;

    public string FontFamily { get; set; } = "Arial Narrow";

    public string FontColor { get; set; } = "#000000";

    public double FontSizeInPoints { get; set; } = 9.0d;
}
