using System.Text.RegularExpressions;
using sp2rdlGenExtension.Model;

namespace sp2rdlGenExtension.Generation;

internal static class LocalizationLabelCollector
{
    private static readonly Regex InvalidFieldNameCharacterRegex = new("[^A-Za-z0-9_]", RegexOptions.Compiled);

    public static IReadOnlyList<LocalizationLabel> Collect(ReportModel model)
    {
        if (!model.Localization.Enabled)
        {
            return [];
        }

        var labels = new List<LocalizationLabel>();
        Add(labels, "ReportTitle", "ReportTitle", string.IsNullOrWhiteSpace(model.ReportTitle.Text) ? model.Name : model.ReportTitle.Text);
        Add(labels, "GrandTotal", "GrandTotal", "Ukupno");
        Add(labels, "Subtotal", "Subtotal", "Podzbir");

        var mainDataset = model.Datasets.FirstOrDefault(dataset =>
                string.Equals(dataset.Name, model.MainDatasetName, StringComparison.OrdinalIgnoreCase))
            ?? model.Datasets.FirstOrDefault();
        if (mainDataset is null)
        {
            return labels;
        }

        foreach (var field in GetDisplayedFields(mainDataset.Fields))
        {
            Add(labels, "Column." + field.Name, "Column_" + MakeFieldName(field.Name), GetDefaultLabel(field));
        }

        foreach (var groupField in mainDataset.Fields.Where(field => field.GroupLevel is >= 1 and <= 4))
        {
            Add(labels, "Group." + groupField.Name, "Group_" + MakeFieldName(groupField.Name), GetDefaultLabel(groupField));
        }

        return labels;
    }

    private static IReadOnlyList<DatasetField> GetDisplayedFields(IReadOnlyList<DatasetField> fields)
    {
        var orderedFields = fields
            .OrderBy(field => field.OrdinalPosition <= 0 ? int.MaxValue : field.OrdinalPosition)
            .ThenBy(field => field.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var detailFields = orderedFields
            .Where(field => field.IncludeInReport && field.GroupLevel <= 0)
            .ToList();

        if (detailFields.Count > 0)
        {
            return detailFields;
        }

        detailFields = orderedFields.Where(field => field.IncludeInReport).ToList();
        return detailFields.Count > 0 ? detailFields : orderedFields.Take(1).ToList();
    }

    private static void Add(ICollection<LocalizationLabel> labels, string key, string fieldName, string defaultValue)
    {
        if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(fieldName))
        {
            return;
        }

        var normalizedFieldName = MakeFieldName(fieldName);
        if (labels.Any(label => string.Equals(label.FieldName, normalizedFieldName, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        labels.Add(new LocalizationLabel(key.Trim(), normalizedFieldName, defaultValue.Trim()));
    }

    private static string GetDefaultLabel(DatasetField field)
        => string.IsNullOrWhiteSpace(field.DefaultLabel) ? field.Name : field.DefaultLabel.Trim();

    private static string MakeFieldName(string value)
    {
        var normalized = InvalidFieldNameCharacterRegex.Replace(value.Trim(), "_").Trim('_');
        if (string.IsNullOrWhiteSpace(normalized))
        {
            normalized = "Label";
        }

        return char.IsLetter(normalized[0]) || normalized[0] == '_'
            ? normalized
            : "L_" + normalized;
    }
}
