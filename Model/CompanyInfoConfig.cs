namespace sp2rdlGenExtension.Model;

internal sealed class CompanyInfoConfig
{
    public string Text { get; set; } = string.Empty;

    public string? SqlExpression { get; set; }

    public string? BackendEndpoint { get; set; }
}
