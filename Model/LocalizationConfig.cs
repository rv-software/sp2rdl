namespace sp2rdlGenExtension.Model;

internal sealed class LocalizationConfig
{
    public bool Enabled { get; set; }

    public int ReportId { get; set; }

    public int DefaultLanguageId { get; set; } = 3;

    public int GeneralReportId { get; set; }

    public LocalizationAccessMode AccessMode { get; set; } = LocalizationAccessMode.Table;

    // Tool stays database-agnostic: defaults are intentionally empty so the
    // user fills in the schema/name that exists in their own database.
    public TranslationObjectReference TranslationTable { get; set; } = new();

    public TranslationObjectReference ReportTable { get; set; } = new();

    // Retained for JSON compatibility; UI no longer exposes procedure access.
    public TranslationObjectReference TranslationProcedure { get; set; } = new();

    public bool GenerateDefaultLanguageSeed { get; set; } = true;

    public bool SkipKeysFromGeneralReport { get; set; }

    public List<int> GenerateLanguageTemplatesFor { get; set; } = new();

    public LanguageTemplateValueMode LanguageTemplateValueMode { get; set; } = LanguageTemplateValueMode.CopyDefault;
}
