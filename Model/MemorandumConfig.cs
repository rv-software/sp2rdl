namespace sp2rdlGenExtension.Model;

internal sealed class MemorandumConfig
{
    public bool Enabled { get; set; }

    public string? LogoImagePath { get; set; }

    public string? LogoSourceColumnName { get; set; }

    public string TextTemplate { get; set; } = string.Empty;

    public bool ShowVerticalSeparator { get; set; } = true;

    public bool ShowBottomLine { get; set; } = true;

    public double HeightInCentimeters { get; set; } = 2.5d;

    public string LayoutPreset { get; set; } = "LogoLeftTextRight";
}
