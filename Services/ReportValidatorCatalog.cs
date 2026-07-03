using System.IO;
using System.Reflection;
using System.Text.Json;
using sp2rdlGenExtension.Model;

namespace sp2rdlGenExtension.Services;

internal sealed class ReportValidatorCatalog
{
    private const string ConfigRelativePath = "config/report-validators.json";
    private const string EmbeddedResourceName = "sp2rdlGenExtension.Config.report-validators.json";
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private readonly Dictionary<string, ValidatorDefinition> validatorsByCode;
    private readonly Dictionary<string, List<string>> validatorsByControlType;

    private ReportValidatorCatalog(
        Dictionary<string, ValidatorDefinition> validatorsByCode,
        Dictionary<string, List<string>> validatorsByControlType)
    {
        this.validatorsByCode = validatorsByCode;
        this.validatorsByControlType = validatorsByControlType;
    }

    /// <summary>
    /// Loads validator metadata from solution config, extension output config, or embedded defaults.
    /// </summary>
    public static ReportValidatorCatalog Load(string solutionDirectory)
    {
        var json = LoadJson(solutionDirectory);
        var config = JsonSerializer.Deserialize<ValidatorCatalogConfig>(json, JsonOptions)
            ?? new ValidatorCatalogConfig();

        var validators = config.Validators
            .Where(item => !string.IsNullOrWhiteSpace(item.Code))
            .GroupBy(item => item.Code.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.First().Normalize(),
                StringComparer.OrdinalIgnoreCase);

        var componentValidators = config.ComponentValidators
            .ToDictionary(
                pair => pair.Key,
                pair => pair.Value
                    .Where(code => validators.ContainsKey(code))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList(),
                StringComparer.OrdinalIgnoreCase);

        return new ReportValidatorCatalog(validators, componentValidators);
    }

    /// <summary>
    /// Returns validator definitions allowed for the specified report parameter control type.
    /// </summary>
    public IReadOnlyList<ValidatorDefinition> GetAllowedValidators(ControlType controlType)
    {
        var key = controlType.ToString();
        return this.validatorsByControlType.TryGetValue(key, out var codes)
            ? codes.Select(code => this.validatorsByCode[code]).ToList()
            : [];
    }

    private static string LoadJson(string solutionDirectory)
    {
        var solutionConfigPath = Path.Combine(solutionDirectory, ConfigRelativePath);
        if (File.Exists(solutionConfigPath))
        {
            return File.ReadAllText(solutionConfigPath);
        }

        var outputConfigPath = Path.Combine(AppContext.BaseDirectory, ConfigRelativePath);
        if (File.Exists(outputConfigPath))
        {
            return File.ReadAllText(outputConfigPath);
        }

        using var stream = typeof(ReportValidatorCatalog).Assembly.GetManifestResourceStream(EmbeddedResourceName);
        if (stream is null)
        {
            return "{}";
        }

        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private sealed class ValidatorCatalogConfig
    {
        public List<ValidatorDefinition> Validators { get; set; } = new();

        public Dictionary<string, List<string>> ComponentValidators { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }
}

internal sealed class ValidatorDefinition
{
    public string Code { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string ValueType { get; set; } = "string";

    public string Kind { get; set; } = "validation";

    public string? DefaultValue { get; set; }

    public List<string> AllowedValues { get; set; } = new();

    public string Description { get; set; } = string.Empty;

    /// <summary>
    /// Normalizes optional display metadata so the UI can bind without extra null checks.
    /// </summary>
    public ValidatorDefinition Normalize()
    {
        Code = Code.Trim();
        Name = string.IsNullOrWhiteSpace(Name) ? Code : Name.Trim();
        ValueType = string.IsNullOrWhiteSpace(ValueType) ? "string" : ValueType.Trim();
        Kind = string.IsNullOrWhiteSpace(Kind) ? "validation" : Kind.Trim();
        DefaultValue = string.IsNullOrWhiteSpace(DefaultValue) ? null : DefaultValue.Trim();
        Description = Description?.Trim() ?? string.Empty;
        AllowedValues = AllowedValues
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        return this;
    }
}
