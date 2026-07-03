using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows;
using sp2rdlGenExtension.Model;
using sp2rdlGenExtension.Services;

namespace sp2rdlGenExtension.Dialogs;

public partial class ParameterValidatorsDialog : Window
{
    private static readonly Regex DateExpressionRegex = new(
        @"^(today|startofweek|endofweek|startofmonth|endofmonth|startofquarter|endofquarter|startofyear|endofyear)([+-]\d+[dwmqy])?$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private readonly ObservableCollection<ValidatorRow> rows;

    internal List<ParameterValidatorValue> RuntimeSettings { get; private set; } = new();

    internal ParameterValidatorsDialog(
        string parameterName,
        ControlType controlType,
        IReadOnlyList<ValidatorDefinition> allowedValidators,
        IEnumerable<ParameterValidatorValue> selectedRuntimeSettings)
    {
        InitializeComponent();
        TxtTitle.Text = $"Runtime settings - {parameterName}";
        TxtHint.Text = $"Control type: {controlType}. Only settings allowed by config/report-validators.json are shown.";

        var selectedByCode = selectedRuntimeSettings
            .Where(item => !string.IsNullOrWhiteSpace(item.Code))
            .ToDictionary(item => item.Code.Trim(), StringComparer.OrdinalIgnoreCase);

        this.rows = new ObservableCollection<ValidatorRow>(
            allowedValidators.Select((definition, index) =>
            {
                selectedByCode.TryGetValue(definition.Code, out var selected);
                return ValidatorRow.FromDefinition(definition, selected, index + 1);
            }));

        GridValidators.ItemsSource = this.rows;
    }

    /// <summary>
    /// Validates selected values and exposes them to the caller.
    /// </summary>
    private void OkButton_Click(object sender, RoutedEventArgs e)
    {
        GridValidators.CommitEdit();

        foreach (var row in this.rows.Where(item => item.IsEnabled))
        {
            var error = ValidateValue(row);
            if (!string.IsNullOrWhiteSpace(error))
            {
                MessageBox.Show(this, error, "Runtime settings", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
        }

        RuntimeSettings = this.rows
            .Where(row => row.IsEnabled)
            .Select(row => new ParameterValidatorValue
            {
                Code = row.Code,
                Value = row.Value.Trim(),
                ValueType = row.ValueType,
                Kind = row.Kind,
                SortOrder = row.SortOrder,
                IsEnabled = true
            })
            .ToList();

        DialogResult = true;
        Close();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    /// <summary>
    /// Checks the text value against the configured validator value type.
    /// </summary>
    private static string? ValidateValue(ValidatorRow row)
    {
        if (string.IsNullOrWhiteSpace(row.Value))
        {
            return $"Value is required for validator '{row.Code}'.";
        }

        var value = row.Value.Trim();
        return row.ValueType.Trim().ToLowerInvariant() switch
        {
            "bool" or "boolean" => IsBool(value) ? null : $"Validator '{row.Code}' expects true/false or 1/0.",
            "int" or "integer" => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out _) ? null : $"Validator '{row.Code}' expects an integer value.",
            "decimal" or "number" => decimal.TryParse(value.Replace(',', '.'), NumberStyles.Number, CultureInfo.InvariantCulture, out _) ? null : $"Validator '{row.Code}' expects a numeric value.",
            "date" => DateTime.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _) ? null : $"Validator '{row.Code}' expects a date value in yyyy-MM-dd format.",
            "dateexpression" => IsDateExpression(value) ? null : $"Validator '{row.Code}' expects yyyy-MM-dd or a date expression like today, today-7d, startOfMonth, endOfYear+1d.",
            "enum" => row.AllowedValues.Contains(value, StringComparer.OrdinalIgnoreCase) ? null : $"Validator '{row.Code}' expects one of: {row.AllowedValuesText}.",
            _ => null
        };
    }

    private static bool IsBool(string value)
        => bool.TryParse(value, out _) || value is "0" or "1";

    /// <summary>
    /// Validates fixed ISO dates and supported relative date expressions.
    /// </summary>
    private static bool IsDateExpression(string value)
    {
        if (DateTime.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
        {
            return true;
        }

        var normalized = value.Replace(" ", string.Empty);
        return DateExpressionRegex.IsMatch(normalized);
    }

    private sealed class ValidatorRow
    {
        public bool IsEnabled { get; set; }

        public string Code { get; set; } = string.Empty;

        public string Name { get; set; } = string.Empty;

        public string ValueType { get; set; } = "string";

        public string Kind { get; set; } = "validation";

        public string Value { get; set; } = string.Empty;

        public List<string> AllowedValues { get; set; } = new();

        public string AllowedValuesText => string.Join(", ", AllowedValues);

        public string Description { get; set; } = string.Empty;

        public int SortOrder { get; set; }

        /// <summary>
        /// Creates a UI row from a catalog definition and an optional previously selected value.
        /// </summary>
        public static ValidatorRow FromDefinition(ValidatorDefinition definition, ParameterValidatorValue? selected, int sortOrder)
            => new()
            {
                IsEnabled = selected?.IsEnabled ?? false,
                Code = definition.Code,
                Name = definition.Name,
                ValueType = definition.ValueType,
                Kind = selected?.Kind ?? definition.Kind,
                Value = selected?.Value ?? definition.DefaultValue ?? string.Empty,
                AllowedValues = definition.AllowedValues,
                Description = definition.Description,
                SortOrder = selected?.SortOrder > 0 ? selected.SortOrder : sortOrder
            };
    }
}
