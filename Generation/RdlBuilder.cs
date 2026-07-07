using System.Globalization;
using System.Data.SqlClient;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using sp2rdlGenExtension.Model;

namespace sp2rdlGenExtension.Generation;

#pragma warning disable CS0618 // Project decision: use System.Data.SqlClient for VSIX compatibility.
internal sealed class RdlBuilder
{
    private static readonly XNamespace Rdl = "http://schemas.microsoft.com/sqlserver/reporting/2016/01/reportdefinition";
    private static readonly XNamespace Rd = "http://schemas.microsoft.com/SQLServer/reporting/reportdesigner";
    private const string ReportLineColor = "#A6A6A6";
    private const string ReportLineWidth = "0.5pt";
    private const string GroupSpacerHeight = "0.14cm";
    private const string TablixFontFamily = "Arial Narrow";
    private const string HeaderBackgroundColor = "#EDEDED";
    private const string GrandTotalBackgroundColor = "#CFCFCF";
    private static readonly Regex HtmlParagraphRegex = new(
        @"<p\b(?<attributes>[^>]*)>(?<content>.*?)</p\s*>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex CssTextAlignRegex = new(
        @"text-align\s*:\s*(?<align>left|center|right)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex ReportSummaryLineMarkerRegex = new(
        @"(\{Line\}|<hr\s*/?>)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public XDocument Build(ReportModel model)
    {
        ArgumentNullException.ThrowIfNull(model);

        // Replace only the skeleton sections owned by this generator. Empty top-level
        // elements are deliberately omitted because Report Builder treats them as
        // invalid in several cases.
        var document = LoadSkeleton();
        var report = document.Root ?? throw new InvalidOperationException("RDL skeleton is missing the Report root element.");
        ValidateParameterDependencies(model);

        ReplaceTopLevelElement(report, BuildDataSources(model));
        ReplaceOptionalTopLevelElement(report, Rdl + "DataSets", BuildDataSets(model));
        var embeddedImages = BuildEmbeddedImages(model);
        if (embeddedImages is not null)
        {
            ReplaceTopLevelElement(report, embeddedImages);
        }
        ReplaceOptionalTopLevelElement(report, Rdl + "ReportParameters", BuildReportParameters(model));
        ReplaceOptionalTopLevelElement(report, Rdl + "ReportParametersLayout", BuildReportParametersLayout(model));
        ApplyPageSetup(report, model.PageSetup);
        ApplyBody(report, model);
        ApplyHeaderFooter(report, model);

        report.SetElementValue(Rd + "ReportID", Guid.NewGuid().ToString());
        return document;
    }

    public string BuildXml(ReportModel model)
        => Build(model).ToString(SaveOptions.DisableFormatting);

    private static XDocument LoadSkeleton()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var resourceName = assembly.GetManifestResourceNames()
            .SingleOrDefault(name => name.EndsWith("Resources.SkeletonTemplate.rdl", StringComparison.Ordinal));

        if (resourceName is null)
        {
            throw new InvalidOperationException("Embedded RDL skeleton resource was not found.");
        }

        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException("Embedded RDL skeleton resource could not be opened.");
        return XDocument.Load(stream, LoadOptions.PreserveWhitespace);
    }

    private static XElement BuildDataSources(ReportModel model)
    {
        var dataSource = new XElement(Rdl + "DataSource",
            new XAttribute("Name", model.SharedDataSourceName));

        if (string.IsNullOrWhiteSpace(model.SourceConnectionString))
        {
            dataSource.Add(new XElement(Rdl + "DataSourceReference", model.SharedDataSourceName));
            dataSource.Add(new XElement(Rd + "SecurityType", "None"));
        }
        else
        {
            var connectionInfo = BuildConnectionInfo(model.SourceConnectionString);
            dataSource.Add(new XElement(Rdl + "ConnectionProperties",
                new XElement(Rdl + "DataProvider", "SQL"),
                new XElement(Rdl + "ConnectString", connectionInfo.ConnectString),
                connectionInfo.IntegratedSecurity
                    ? new XElement(Rdl + "IntegratedSecurity", "true")
                    : null));
            dataSource.Add(new XElement(Rd + "SecurityType", connectionInfo.SecurityType));
        }

        dataSource.Add(
            new XElement(Rd + "DataSourceID", Guid.NewGuid().ToString()));

        return new XElement(Rdl + "DataSources", dataSource);
    }

    private static (string ConnectString, bool IntegratedSecurity, string SecurityType) BuildConnectionInfo(string connectionString)
    {
        try
        {
            var builder = new SqlConnectionStringBuilder(connectionString);
            var usesIntegratedSecurity = builder.IntegratedSecurity
                || (builder.TryGetValue("Authentication", out var authentication)
                    && authentication is not null
                    && authentication.ToString()?.IndexOf("Integrated", StringComparison.OrdinalIgnoreCase) >= 0);

            if (usesIntegratedSecurity)
            {
                builder.IntegratedSecurity = false;
                builder.Remove("Integrated Security");
                builder.Remove("Trusted_Connection");
                builder.Remove("User ID");
                builder.Remove("UID");
                builder.Remove("Password");
                builder.Remove("Pwd");
                builder.Remove("Authentication");

                return (builder.ConnectionString, true, "Integrated");
            }
        }
        catch
        {
        }

        return (connectionString, false, "None");
    }

    private static XElement? BuildDataSets(ReportModel model)
    {
        var datasets = model.Datasets.ToList();
        var reportVariablesDataset = BuildReportVariablesDataset(model);
        if (reportVariablesDataset is not null)
        {
            datasets.Add(reportVariablesDataset);
        }

        var localizationDataset = BuildLocalizationLabelsDataset(model);
        if (localizationDataset is not null)
        {
            datasets.Add(localizationDataset);
        }

        if (datasets.Count == 0)
        {
            return null;
        }

        return new XElement(Rdl + "DataSets",
            datasets.Select(dataset =>
                new XElement(Rdl + "DataSet",
                    new XAttribute("Name", dataset.Name),
                    BuildQuery(model, dataset),
                    BuildFields(dataset))));
    }

    private static DatasetConfig? BuildReportVariablesDataset(ReportModel model)
    {
        // One shared SQL dataset supplies all dynamic placeholders used by
        // memorandum, header/footer, and report summary templates.
        if (!model.ReportVariables.DynamicSource.Enabled
            || string.IsNullOrWhiteSpace(model.ReportVariables.DynamicSource.SqlExpression))
        {
            return null;
        }

        var fields = model.ReportVariables.Items
            .Where(variable => variable.Enabled && !string.IsNullOrWhiteSpace(variable.SourceColumnName))
            .Select((variable, index) => new DatasetField(
                variable.SourceColumnName!.Trim(),
                "nvarchar",
                true,
                index + 1))
            .DistinctBy(field => field.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (fields.Count == 0)
        {
            return null;
        }

        return new DatasetConfig
        {
            Name = string.IsNullOrWhiteSpace(model.ReportVariables.DynamicSource.DatasetName)
                ? "dsReportVariables"
                : model.ReportVariables.DynamicSource.DatasetName,
            Command = model.ReportVariables.DynamicSource.SqlExpression,
            CommandKind = CommandKind.Text,
            Fields = fields
        };
    }

    private static DatasetConfig? BuildLocalizationLabelsDataset(ReportModel model)
    {
        var labels = LocalizationLabelCollector.Collect(model);
        if (labels.Count == 0)
        {
            return null;
        }

        return new DatasetConfig
        {
            Name = "dsReportLabels",
            Command = BuildLocalizationLabelsSql(model.Localization, labels),
            CommandKind = CommandKind.Text,
            Fields = labels
                .Select((label, index) => new DatasetField(label.FieldName, "nvarchar", true, index + 1))
                .ToList(),
            ParameterBindings =
            [
                new DatasetParameterBinding { DatasetParameterName = "@ReportId", ReportParameterName = "ReportId" },
                new DatasetParameterBinding { DatasetParameterName = "@LanguageId", ReportParameterName = "LanguageId" }
            ]
        };
    }

    private static string BuildLocalizationLabelsSql(LocalizationConfig localization, IReadOnlyList<LocalizationLabel> labels)
    {
        var selectList = string.Join(
            "," + Environment.NewLine,
            labels.Select(label =>
                $"    COALESCE(MAX(CASE WHEN X.[Key] = '{EscapeSql(label.Key)}' THEN X.[Value] END), N'{EscapeSql(label.DefaultValue)}') AS {QuoteName(label.FieldName)}"));

        return $"""
            WITH T AS
            (
                SELECT RT.[Key], RT.[Value], 1 AS Priority
                FROM {QuoteName(localization.TranslationTable.Schema)}.{QuoteName(localization.TranslationTable.Name)} AS RT
                WHERE RT.ReportId = @ReportId
                  AND RT.LanguageId = @LanguageId
                  AND RT.Deleted = 0

                UNION ALL

                SELECT RT.[Key], RT.[Value], 2 AS Priority
                FROM {QuoteName(localization.TranslationTable.Schema)}.{QuoteName(localization.TranslationTable.Name)} AS RT
                WHERE RT.ReportId = {localization.GeneralReportId}
                  AND RT.LanguageId = @LanguageId
                  AND RT.Deleted = 0
            )
            SELECT
            {selectList}
            FROM
            (
                SELECT [Key], [Value]
                FROM
                (
                    SELECT
                        T.[Key],
                        T.[Value],
                        ROW_NUMBER() OVER (PARTITION BY T.[Key] ORDER BY T.Priority) AS RowNo
                    FROM T
                ) AS R
                WHERE R.RowNo = 1
            ) AS X;
            """;
    }

    private static XElement? BuildEmbeddedImages(ReportModel model)
    {
        var images = new List<XElement>();
        AddEmbeddedImage(images, GetFooterLogoImageName(), model.PageFooter.LogoImagePath);
        AddEmbeddedImage(images, GetMemorandumLogoImageName(), model.Memorandum.LogoImagePath);

        if (images.Count == 0)
        {
            return null;
        }

        return new XElement(Rdl + "EmbeddedImages", images);
    }

    private static void AddEmbeddedImage(ICollection<XElement> images, string imageName, string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return;
        }

        images.Add(new XElement(Rdl + "EmbeddedImage",
            new XAttribute("Name", imageName),
            new XElement(Rdl + "MIMEType", GetImageMimeType(path)),
            new XElement(Rdl + "ImageData", Convert.ToBase64String(File.ReadAllBytes(path)))));
    }

    /// <summary>
    /// Builds the RDL query block and resolves query parameter expressions against report parameter metadata.
    /// </summary>
    private static XElement BuildQuery(ReportModel model, DatasetConfig dataset)
        => new(Rdl + "Query",
            new XElement(Rdl + "DataSourceName", model.SharedDataSourceName),
            new XElement(Rdl + "CommandType", dataset.CommandKind == CommandKind.StoredProcedure ? "StoredProcedure" : "Text"),
            new XElement(Rdl + "CommandText", dataset.Command),
            BuildQueryParameters(model, dataset),
            new XElement(Rdl + "Timeout", GetDatasetTimeout(model, dataset)));

    /// <summary>
    /// Builds dataset query parameters and guards nullable typed parameters from passing the literal text NULL.
    /// </summary>
    private static XElement? BuildQueryParameters(ReportModel model, DatasetConfig dataset)
    {
        if (dataset.ParameterBindings.Count == 0)
        {
            return null;
        }

        var reportParameters = BuildEffectiveReportParameters(model)
            .ToDictionary(parameter => parameter.Name.TrimStart('@'), StringComparer.OrdinalIgnoreCase);

        return new XElement(Rdl + "QueryParameters",
            dataset.ParameterBindings.Select(binding =>
            {
                reportParameters.TryGetValue(binding.ReportParameterName.TrimStart('@'), out var parameter);
                return new XElement(Rdl + "QueryParameter",
                    new XAttribute("Name", EnsureAtPrefix(binding.DatasetParameterName)),
                    new XElement(Rdl + "Value", BuildQueryParameterValueExpression(binding, parameter)));
            }));
    }

    private static XElement BuildFields(DatasetConfig dataset)
        => new(Rdl + "Fields",
            dataset.Fields.Select(field =>
                new XElement(Rdl + "Field",
                    new XAttribute("Name", field.Name),
                    new XElement(Rdl + "DataField", field.Name),
                    new XElement(Rd + "TypeName", MapClrTypeName(field.SqlTypeName)))));

    /// <summary>
    /// Creates the query parameter expression, converting nullable typed literal NULL values to database nulls.
    /// </summary>
    private static string BuildQueryParameterValueExpression(DatasetParameterBinding binding, ReportParameter? parameter)
    {
        var parameterReference = $"Parameters!{binding.ReportParameterName}.Value";
        if (parameter is null || parameter.MultiValue || IsStringReportParameter(parameter))
        {
            return "=" + parameterReference;
        }

        return $"=IIF(IsNothing({parameterReference}) OR CStr({parameterReference}) = \"NULL\", Nothing, {parameterReference})";
    }

    private static XElement? BuildReportParameters(ReportModel model)
    {
        var parameters = BuildEffectiveReportParameters(model);
        if (parameters.Count == 0)
        {
            return null;
        }

        var parametersUsedInQueries = model.Datasets
            .SelectMany(dataset => dataset.ParameterBindings)
            .Select(binding => binding.ReportParameterName.TrimStart('@'))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (model.Localization.Enabled)
        {
            parametersUsedInQueries.Add("ReportId");
            parametersUsedInQueries.Add("LanguageId");
        }

        return new XElement(Rdl + "ReportParameters",
            parameters
                .OrderBy(parameter => parameter.OrdinalNumber <= 0 ? int.MaxValue : parameter.OrdinalNumber)
                .ThenBy(parameter => parameter.Name, StringComparer.OrdinalIgnoreCase)
                .Select(parameter => BuildReportParameter(
                    parameter,
                    parametersUsedInQueries.Contains(parameter.Name.TrimStart('@')))));
    }

    /// <summary>
    /// Builds one report parameter and normalizes explicit NULL defaults for nullable typed values.
    /// </summary>
    private static XElement BuildReportParameter(ReportParameter parameter, bool usedInQuery)
    {
        var element = new XElement(Rdl + "ReportParameter",
            new XAttribute("Name", parameter.Name),
            new XElement(Rdl + "DataType", MapReportParameterType(parameter)));

        if (!string.IsNullOrWhiteSpace(parameter.DefaultValueExpression))
        {
            element.Add(
                new XElement(Rdl + "DefaultValue",
                    new XElement(Rdl + "Values",
                        new XElement(Rdl + "Value", NormalizeReportParameterDefaultExpression(parameter)))));
        }
        else if (!string.IsNullOrWhiteSpace(parameter.DefaultValueDatasetName)
            && !string.IsNullOrWhiteSpace(parameter.DefaultValueField))
        {
            element.Add(
                new XElement(Rdl + "DefaultValue",
                    new XElement(Rdl + "DataSetReference",
                        new XElement(Rdl + "DataSetName", parameter.DefaultValueDatasetName),
                        new XElement(Rdl + "ValueField", parameter.DefaultValueField))));
        }

        if (parameter.Nullable)
        {
            element.Add(new XElement(Rdl + "Nullable", "true"));
        }

        if (parameter.AllowBlank)
        {
            element.Add(new XElement(Rdl + "AllowBlank", "true"));
        }

        element.Add(new XElement(Rdl + "Prompt", parameter.Prompt));

        if (parameter.Hidden)
        {
            element.Add(new XElement(Rdl + "Hidden", "true"));
        }

        if (parameter.MultiValue)
        {
            element.Add(new XElement(Rdl + "MultiValue", "true"));
        }

        if (usedInQuery)
        {
            element.Add(new XElement(Rdl + "UsedInQuery", "True"));
        }

        var validValues = BuildValidValues(parameter);
        if (validValues is not null)
        {
            element.Add(validValues);
        }

        return element;
    }

    /// <summary>
    /// Converts user-entered NULL defaults to the SSRS null expression for nullable non-string parameters.
    /// </summary>
    private static string NormalizeReportParameterDefaultExpression(ReportParameter parameter)
    {
        var expression = parameter.DefaultValueExpression?.Trim() ?? string.Empty;
        if (!parameter.Nullable || IsStringReportParameter(parameter))
        {
            return expression;
        }

        var normalized = expression.StartsWith("=", StringComparison.Ordinal)
            ? expression[1..].Trim()
            : expression;
        return string.Equals(normalized, "NULL", StringComparison.OrdinalIgnoreCase)
            ? "=Nothing"
            : expression;
    }

    private static XElement? BuildValidValues(ReportParameter parameter)
    {
        if (parameter.Lookup is not null)
        {
            return new XElement(Rdl + "ValidValues",
                new XElement(Rdl + "DataSetReference",
                    new XElement(Rdl + "DataSetName", parameter.Lookup.DatasetName),
                    new XElement(Rdl + "ValueField", parameter.Lookup.ValueField),
                    new XElement(Rdl + "LabelField", parameter.Lookup.LabelField)));
        }

        if (parameter.StaticValidValues.Count == 0)
        {
            return null;
        }

        return new XElement(Rdl + "ValidValues",
            new XElement(Rdl + "ParameterValues",
                parameter.StaticValidValues.Select(value =>
                    new XElement(Rdl + "ParameterValue",
                        new XElement(Rdl + "Value", value.Value),
                        new XElement(Rdl + "Label", string.IsNullOrWhiteSpace(value.Label) ? value.Value : value.Label)))));
    }

    private static XElement? BuildReportParametersLayout(ReportModel model)
    {
        var parameters = BuildEffectiveReportParameters(model)
            //.Where(parameter => !parameter.Hidden)
            .ToList();
        if (parameters.Count == 0)
        {
            return null;
        }

        var orderedParameters = parameters
            .OrderBy(parameter => parameter.OrdinalNumber <= 0 ? int.MaxValue : parameter.OrdinalNumber)
            .ThenBy(parameter => parameter.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var rows = Math.Max(1, orderedParameters.Count);
        const int columns = 1;
        var cellDefinitions = orderedParameters.Count == 0
            ? null
            : new XElement(Rdl + "CellDefinitions",
                orderedParameters.Select((parameter, index) =>
                    new XElement(Rdl + "CellDefinition",
                        new XElement(Rdl + "ColumnIndex", "0"),
                        new XElement(Rdl + "RowIndex", index.ToString(CultureInfo.InvariantCulture)),
                        new XElement(Rdl + "ParameterName", parameter.Name))));

        return new XElement(Rdl + "ReportParametersLayout",
            new XElement(Rdl + "GridLayoutDefinition",
                new XElement(Rdl + "NumberOfColumns", columns.ToString(CultureInfo.InvariantCulture)),
                new XElement(Rdl + "NumberOfRows", rows.ToString(CultureInfo.InvariantCulture)),
                cellDefinitions));
    }

    private static List<ReportParameter> BuildEffectiveReportParameters(ReportModel model)
    {
        var parameters = model.Parameters.ToList();
        if (!model.Localization.Enabled)
        {
            return parameters;
        }

        if (!parameters.Any(parameter => string.Equals(parameter.Name.TrimStart('@'), "ReportId", StringComparison.OrdinalIgnoreCase)))
        {
            parameters.Add(new ReportParameter
            {
                Name = "ReportId",
                SqlTypeName = "int",
                Prompt = "ReportId",
                Hidden = true,
                DefaultValueExpression = model.Localization.ReportId.ToString(CultureInfo.InvariantCulture),
                OrdinalNumber = -2
            });
        }

        if (!parameters.Any(parameter => string.Equals(parameter.Name.TrimStart('@'), "LanguageId", StringComparison.OrdinalIgnoreCase)))
        {
            parameters.Add(new ReportParameter
            {
                Name = "LanguageId",
                SqlTypeName = "int",
                Prompt = "Language",
                DefaultValueExpression = Math.Max(1, model.Localization.DefaultLanguageId).ToString(CultureInfo.InvariantCulture),
                OrdinalNumber = -1
            });
        }

        return parameters;
    }

    private static void ValidateParameterDependencies(ReportModel model)
    {
        var orderedParameters = model.Parameters
            .OrderBy(parameter => parameter.OrdinalNumber <= 0 ? int.MaxValue : parameter.OrdinalNumber)
            .ThenBy(parameter => parameter.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var duplicateParameter = orderedParameters
            .GroupBy(parameter => parameter.Name, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicateParameter is not null)
        {
            throw new InvalidOperationException($"Report parameter '{duplicateParameter.Key}' is defined more than once.");
        }

        var indexes = orderedParameters
            .Select((parameter, index) => new { parameter.Name, Index = index })
            .ToDictionary(item => item.Name, item => item.Index, StringComparer.OrdinalIgnoreCase);

        foreach (var parameter in orderedParameters)
        {
            if (string.IsNullOrWhiteSpace(parameter.DependsOnParameterName))
            {
                continue;
            }

            foreach (var dependencyName in ParseDependencyNames(parameter.DependsOnParameterName))
            {
                if (!indexes.TryGetValue(dependencyName, out var dependencyIndex))
                {
                    throw new InvalidOperationException($"Parameter '{parameter.Name}' depends on '{dependencyName}', but '{dependencyName}' is not defined.");
                }

                if (dependencyIndex >= indexes[parameter.Name])
                {
                    throw new InvalidOperationException($"Parameter '{parameter.Name}' depends on '{dependencyName}'. Move '{dependencyName}' before '{parameter.Name}' by changing Ordinal.");
                }
            }
        }
    }

    private static void ApplyHeaderFooter(XElement report, ReportModel model)
    {
        var page = report.Descendants(Rdl + "Page").FirstOrDefault()
            ?? throw new InvalidOperationException("RDL skeleton is missing Page element.");

        page.Element(Rdl + "PageHeader")?.Remove();
        page.Element(Rdl + "PageFooter")?.Remove();

        if (ShouldEmitPageHeader(model.PageHeader))
        {
            page.AddFirst(BuildPageHeader(model));
        }

        if (model.PageFooter.Enabled)
        {
            page.Add(BuildPageFooter(model.PageFooter, model.BaseFontFamily, model.PageSetup));
        }
    }

    private static void ApplyPageSetup(XElement report, PageSetupConfig pageSetup)
    {
        var page = report.Descendants(Rdl + "Page").FirstOrDefault()
            ?? throw new InvalidOperationException("RDL skeleton is missing Page element.");

        page.SetElementValue(Rdl + "PageWidth", ToCentimeters(pageSetup.WidthInCentimeters));
        page.SetElementValue(Rdl + "PageHeight", ToCentimeters(pageSetup.HeightInCentimeters));
        page.SetElementValue(Rdl + "LeftMargin", ToCentimeters(pageSetup.LeftMarginInCentimeters));
        page.SetElementValue(Rdl + "RightMargin", ToCentimeters(pageSetup.RightMarginInCentimeters));
        page.SetElementValue(Rdl + "TopMargin", ToCentimeters(pageSetup.TopMarginInCentimeters));
        page.SetElementValue(Rdl + "BottomMargin", ToCentimeters(pageSetup.BottomMarginInCentimeters));
    }

    private static void ApplyBody(XElement report, ReportModel model)
    {
        var reportSection = report.Descendants(Rdl + "ReportSection").FirstOrDefault()
            ?? throw new InvalidOperationException("RDL skeleton is missing ReportSection element.");
        var body = reportSection.Element(Rdl + "Body")
            ?? throw new InvalidOperationException("RDL skeleton is missing Body element.");

        var usableWidth = GetUsablePageWidth(model.PageSetup);
        reportSection.SetElementValue(Rdl + "Width", ToCentimeters(usableWidth));

        body.Element(Rdl + "ReportItems")?.Remove();
        var reportItems = new XElement(Rdl + "ReportItems");
        var currentTop = 0.0d;
        var localizationLabels = LocalizationLabelCollector.Collect(model);

        if (model.Purpose == ReportPurpose.MemorandumSubreport)
        {
            AddSubreportOnlyBand(
                reportItems,
                BuildInlineMemorandum(model, usableWidth, currentTop),
                body,
                model.Memorandum.HeightInCentimeters);
            return;
        }

        if (model.Purpose == ReportPurpose.ReportSummarySubreport)
        {
            AddSubreportOnlyBand(
                reportItems,
                BuildInlineReportSummary(model, usableWidth, currentTop),
                body,
                model.ReportSummary.HeightInCentimeters);
            return;
        }

        var memorandum = BuildMemorandumBand(model, usableWidth, currentTop);
        if (memorandum is not null)
        {
            reportItems.Add(memorandum);
            currentTop += model.Memorandum.HeightInCentimeters;
        }

        if (model.ReportTitle.Enabled && !string.IsNullOrWhiteSpace(model.ReportTitle.Text))
        {
            reportItems.Add(BuildPositionedTextbox(
                "sp2rdlReportTitle",
                GetLocalizationExpression(localizationLabels, "ReportTitle") ?? model.ReportTitle.Text,
                "0cm",
                ToCentimeters(currentTop),
                ToCentimeters(usableWidth),
                ToCentimeters(model.ReportTitle.HeightInCentimeters),
                model.ReportTitle.TextAlign,
                model.BaseFontFamily,
                $"{model.ReportTitle.FontSizeInPoints.ToString("0.#", CultureInfo.InvariantCulture)}pt",
                "Bold"));
            currentTop += model.ReportTitle.HeightInCentimeters;
        }

        var parameterSummary = BuildParameterSummaryPanel(model, usableWidth, currentTop);
        if (parameterSummary is not null)
        {
            reportItems.Add(parameterSummary);
            currentTop += GetParameterSummaryHeight(model);
        }

        var validationWarnings = BuildValidationWarningPanel(model, usableWidth, currentTop);
        if (validationWarnings is not null)
        {
            reportItems.Add(validationWarnings);
            currentTop += GetValidationWarningHeight(model);
        }

        var dataset = model.Datasets.FirstOrDefault(dataset => dataset.Name == model.MainDatasetName)
            ?? model.Datasets.FirstOrDefault();
        if (dataset is null || dataset.Fields.Count == 0)
        {
            reportItems.Add(BuildPositionedTextbox(
                    "sp2rdlPlaceholder",
                    "No dataset fields selected.",
                    "0cm",
                    ToCentimeters(currentTop),
                    ToCentimeters(usableWidth),
                    "0.8cm",
                    "Left",
                    model.BaseFontFamily));
            body.AddFirst(reportItems);
            body.SetElementValue(Rdl + "Height", ToCentimeters(Math.Max(2.0d, currentTop + 0.8d)));
            return;
        }

        reportItems.Add(BuildTablix(dataset, usableWidth, currentTop, model.TablixStyle ?? new TablixStyleConfig(), localizationLabels));
        currentTop += EstimateTablixHeight(dataset);

        var reportSummary = BuildReportSummaryBand(model, usableWidth, currentTop);
        if (reportSummary is not null)
        {
            reportItems.Add(reportSummary);
            currentTop += model.ReportSummary.HeightInCentimeters;
        }

        body.AddFirst(reportItems);
        body.SetElementValue(Rdl + "Height", ToCentimeters(Math.Max(2.0d, currentTop + 1.25d)));
    }

    private static void AddSubreportOnlyBand(XElement reportItems, XElement band, XElement body, double bandHeight)
    {
        reportItems.Add(band);
        body.AddFirst(reportItems);
        body.SetElementValue(Rdl + "Height", ToCentimeters(Math.Max(0.8d, bandHeight + 0.15d)));
    }

    private static XElement? BuildMemorandumBand(ReportModel model, double usableWidth, double top)
    {
        if (!model.Memorandum.Enabled)
        {
            return null;
        }

        if (model.Memorandum.LayoutMode == ReportBandLayoutMode.Subreport)
        {
            var subreport = BuildReportBandSubreport(
                "sp2rdlMemorandumSubreport",
                true,
                model.Memorandum.LayoutMode,
                model.Memorandum.SubreportName,
                model.Memorandum.SubreportPath,
                model.Memorandum.SubreportServerPath,
                model.Memorandum.SubreportParameterMappings,
                model,
                usableWidth,
                top,
                model.Memorandum.HeightInCentimeters);
            if (subreport is not null || !model.Memorandum.FallbackToInline)
            {
                return subreport;
            }
        }

        return BuildInlineMemorandum(model, usableWidth, top);
    }

    private static XElement? BuildReportSummaryBand(ReportModel model, double usableWidth, double top)
    {
        if (!model.ReportSummary.Enabled)
        {
            return null;
        }

        if (model.ReportSummary.LayoutMode == ReportBandLayoutMode.Subreport)
        {
            var subreport = BuildReportBandSubreport(
                "sp2rdlReportSummarySubreport",
                true,
                model.ReportSummary.LayoutMode,
                model.ReportSummary.SubreportName,
                model.ReportSummary.SubreportPath,
                model.ReportSummary.SubreportServerPath,
                model.ReportSummary.SubreportParameterMappings,
                model,
                usableWidth,
                top,
                model.ReportSummary.HeightInCentimeters);
            if (subreport is not null || !model.ReportSummary.FallbackToInline)
            {
                return subreport;
            }
        }

        return BuildInlineReportSummary(model, usableWidth, top);
    }

    private static XElement BuildInlineMemorandum(ReportModel model, double usableWidth, double top)
    {
        var height = Math.Max(0.8d, model.Memorandum.HeightInCentimeters);
        var reportItems = new XElement(Rdl + "ReportItems");
        var hasLogo = !string.IsNullOrWhiteSpace(model.Memorandum.LogoImagePath) && File.Exists(model.Memorandum.LogoImagePath);

        var alignment = model.Memorandum.LogoAlignment;
        var placement = model.Memorandum.TextPlacement;

        // Center logo + text Beside doesn't fit a single row geometry; force text below.
        if (alignment == MemorandumLogoAlignment.Center && placement == MemorandumTextPlacement.BesideLogo)
        {
            placement = MemorandumTextPlacement.BelowLogo;
        }

        var logoNominalWidth = hasLogo ? Math.Min(2.4d, usableWidth * 0.25d) : 0.0d;
        var gap = hasLogo && placement == MemorandumTextPlacement.BesideLogo ? 0.35d : 0.0d;
        var logoNominalHeight = hasLogo
            ? CalculateMemorandumLogoHeight(height, placement)
            : 0.0d;

        // Logo Left/Top inside the band rectangle.
        var logoLeft = 0.0d;
        var logoTop = 0.1d;
        if (hasLogo)
        {
            switch (alignment)
            {
                case MemorandumLogoAlignment.Center:
                    logoLeft = Math.Max(0.0d, (usableWidth - logoNominalWidth) / 2.0d);
                    break;
                case MemorandumLogoAlignment.Right:
                    logoLeft = Math.Max(0.0d, usableWidth - logoNominalWidth);
                    break;
                default:
                    logoLeft = 0.0d;
                    break;
            }
        }

        // Text frame depends on placement.
        double textLeft;
        double textTop;
        double textWidth;
        double textHeight;
        double separatorLeft = 0.0d;
        double separatorTop = 0.1d;
        double separatorHeight = 0.0d;
        var showVerticalSeparator = false;

        if (!hasLogo || placement == MemorandumTextPlacement.BelowLogo)
        {
            textLeft = 0.0d;
            textTop = hasLogo ? logoTop + logoNominalHeight + 0.1d : 0.0d;
            textWidth = usableWidth;
            textHeight = Math.Max(0.4d, height - textTop - 0.1d);
        }
        else
        {
            // BesideLogo with Left or Right alignment.
            textTop = 0.0d;
            textHeight = Math.Max(0.4d, height - 0.15d);
            if (alignment == MemorandumLogoAlignment.Right)
            {
                textLeft = 0.0d;
                textWidth = Math.Max(1.0d, logoLeft - gap);
                separatorLeft = Math.Max(0.0d, logoLeft - (gap / 2.0d));
            }
            else
            {
                textLeft = logoNominalWidth + gap;
                textWidth = Math.Max(1.0d, usableWidth - textLeft);
                separatorLeft = logoNominalWidth + (gap / 2.0d);
            }

            separatorTop = 0.1d;
            separatorHeight = Math.Max(0.4d, height - 0.25d);
            showVerticalSeparator = model.Memorandum.ShowVerticalSeparator;
        }

        if (hasLogo)
        {
            reportItems.Add(BuildImage(
                "sp2rdlMemorandumLogo",
                GetMemorandumLogoImageName(),
                ToCentimeters(logoLeft),
                ToCentimeters(logoTop),
                ToCentimeters(logoNominalWidth),
                ToCentimeters(logoNominalHeight)));
        }

        if (showVerticalSeparator)
        {
            reportItems.Add(BuildLine(
                "sp2rdlMemorandumVerticalLine",
                ToCentimeters(separatorLeft),
                ToCentimeters(separatorTop),
                "0cm",
                ToCentimeters(separatorHeight)));
        }

        if (hasLogo && model.Memorandum.ShowLogoBottomLine)
        {
            var logoBottomLineLeft = placement == MemorandumTextPlacement.BelowLogo ? 0.0d : logoLeft;
            var logoBottomLineWidth = placement == MemorandumTextPlacement.BelowLogo ? usableWidth : logoNominalWidth;
            var logoBottomLineTop = Math.Min(height - 0.05d, logoTop + logoNominalHeight + 0.05d);
            reportItems.Add(BuildLine(
                "sp2rdlMemorandumLogoBottomLine",
                ToCentimeters(Math.Max(0.0d, logoBottomLineLeft)),
                ToCentimeters(Math.Max(0.0d, logoBottomLineTop)),
                ToCentimeters(Math.Max(0.2d, logoBottomLineWidth)),
                "0cm"));
        }

        var memorandumTemplate = PrepareHtmlTemplateForRdl(model.Memorandum.TextTemplate);
        reportItems.Add(BuildPositionedHtmlTextbox(
            "sp2rdlMemorandumText",
            BuildTemplateExpression(memorandumTemplate.Html, model),
            ToCentimeters(textLeft),
            ToCentimeters(textTop),
            ToCentimeters(textWidth),
            ToCentimeters(textHeight),
            model.BaseFontFamily,
            memorandumTemplate.TextAlign));

        if (model.Memorandum.ShowBottomLine)
        {
            reportItems.Add(BuildLine(
                "sp2rdlMemorandumBottomLine",
                "0cm",
                ToCentimeters(Math.Max(0.0d, height - 0.05d)),
                ToCentimeters(usableWidth),
                "0cm"));
        }

        return BuildBandRectangle("sp2rdlMemorandum", reportItems, usableWidth, top, height);
    }

    private static double CalculateMemorandumLogoHeight(double bandHeight, MemorandumTextPlacement placement)
    {
        if (placement == MemorandumTextPlacement.BesideLogo)
        {
            return Math.Max(0.4d, bandHeight - 0.3d);
        }

        const double topPadding = 0.1d;
        const double logoTextGap = 0.1d;
        const double bottomPadding = 0.1d;
        const double minimumTextHeight = 0.6d;
        var availableForLogo = bandHeight - topPadding - logoTextGap - bottomPadding - minimumTextHeight;
        var preferredLogoHeight = Math.Min(1.2d, bandHeight * 0.45d);
        return Math.Max(0.35d, Math.Min(preferredLogoHeight, availableForLogo));
    }

    private static XElement BuildInlineReportSummary(ReportModel model, double usableWidth, double top)
    {
        var height = Math.Max(0.6d, model.ReportSummary.HeightInCentimeters);
        var reportItems = new XElement(Rdl + "ReportItems");
        var textTop = model.ReportSummary.ShowTopLine ? 0.15d : 0.0d;
        if (model.ReportSummary.ShowTopLine)
        {
            reportItems.Add(BuildLine("sp2rdlReportSummaryTopLine", "0cm", "0cm", ToCentimeters(usableWidth), "0cm"));
        }

        var columns = GetReportSummaryColumns(model.ReportSummary);
        var columnTop = textTop;
        if (columns.Count > 0)
        {
            AddReportSummaryColumns(reportItems, columns, model, usableWidth, height, columnTop);
        }

        return BuildBandRectangle("sp2rdlReportSummary", reportItems, usableWidth, top, height);
    }

    private static List<ReportSummaryColumnConfig> GetReportSummaryColumns(ReportSummaryConfig summary)
    {
        var count = Math.Clamp(summary.ColumnCount <= 0 ? 1 : summary.ColumnCount, 1, 3);
        var columns = (summary.Columns ?? new List<ReportSummaryColumnConfig>())
            .Take(count)
            .ToList();

        while (columns.Count < count)
        {
            columns.Add(new ReportSummaryColumnConfig());
        }

        return columns;
    }

    private static void AddReportSummaryColumns(
        XElement reportItems,
        IReadOnlyList<ReportSummaryColumnConfig> columns,
        ReportModel model,
        double usableWidth,
        double bandHeight,
        double top)
    {
        var count = columns.Count;
        var gap = count > 1 ? 0.35d : 0.0d;
        var availableWidth = Math.Max(1.0d, usableWidth - gap * (count - 1));
        var columnWidths = CalculateReportSummaryColumnWidths(columns, availableWidth);
        var columnHeight = Math.Max(0.4d, bandHeight - top - 0.1d);
        var left = 0.0d;

        for (var index = 0; index < count; index++)
        {
            var column = columns[index];
            var columnWidth = columnWidths[index];
            if (!column.ShowTopLine && string.IsNullOrWhiteSpace(column.TextTemplate))
            {
                left += columnWidth + gap;
                continue;
            }

            if (column.ShowTopLine)
            {
                reportItems.Add(BuildLine(
                    $"sp2rdlReportSummaryColumn{index + 1}Line",
                    ToCentimeters(left),
                    ToCentimeters(top),
                    ToCentimeters(columnWidth),
                    "0cm"));
            }

            var templateTop = top + (column.ShowTopLine ? 0.12d : 0.0d);
            AddReportSummaryColumnTemplate(
                reportItems,
                $"sp2rdlReportSummaryColumn{index + 1}",
                column.TextTemplate,
                model,
                left,
                templateTop,
                columnWidth,
                Math.Max(0.3d, columnHeight - (templateTop - top)),
                column.VerticalAlign,
                column.PaddingInPoints);
            left += columnWidth + gap;
        }
    }

    private static List<double> CalculateReportSummaryColumnWidths(
        IReadOnlyList<ReportSummaryColumnConfig> columns,
        double availableWidth)
    {
        var percentages = columns
            .Select(column => column.WidthPercent > 0 ? column.WidthPercent : 0.0d)
            .ToList();
        var totalPercent = percentages.Sum();
        if (totalPercent <= 0)
        {
            return columns.Select(_ => availableWidth / columns.Count).ToList();
        }

        return percentages
            .Select(percent => availableWidth * (percent > 0 ? percent : 0.0d) / totalPercent)
            .ToList();
    }

    private static void AddReportSummaryColumnTemplate(
        XElement reportItems,
        string namePrefix,
        string templateText,
        ReportModel model,
        double left,
        double top,
        double width,
        double height,
        string verticalAlign,
        double paddingInPoints)
    {
        var parts = ReportSummaryLineMarkerRegex.Split(templateText ?? string.Empty)
            .Where(part => !string.IsNullOrEmpty(part))
            .ToList();
        if (parts.Count == 0)
        {
            return;
        }

        var lineCount = parts.Count(part => ReportSummaryLineMarkerRegex.IsMatch(part));
        var textCount = Math.Max(1, parts.Count - lineCount);
        var lineGapHeight = 0.18d;
        var paddingCm = PointsToCentimeters(Math.Max(0.0d, paddingInPoints));
        var contentLeft = left + paddingCm;
        var contentTop = top + paddingCm;
        var contentWidth = Math.Max(0.2d, width - paddingCm * 2.0d);
        var contentHeight = Math.Max(0.2d, height - paddingCm * 2.0d);
        var textHeight = Math.Max(0.25d, (contentHeight - lineCount * lineGapHeight) / textCount);
        var currentTop = contentTop;
        var textIndex = 1;
        var lineIndex = 1;

        foreach (var part in parts)
        {
            if (ReportSummaryLineMarkerRegex.IsMatch(part))
            {
                currentTop += Math.Max(0.03d, lineGapHeight / 2.0d);
                reportItems.Add(BuildLine(
                    $"{namePrefix}TemplateLine{lineIndex++}",
                    ToCentimeters(contentLeft),
                    ToCentimeters(currentTop),
                    ToCentimeters(contentWidth),
                    "0cm"));
                currentTop += Math.Max(0.03d, lineGapHeight / 2.0d);
                continue;
            }

            var template = PrepareHtmlTemplateForRdl(part.Trim());
            reportItems.Add(BuildPositionedHtmlTextbox(
                $"{namePrefix}Text{textIndex++}",
                BuildTemplateExpression(template.Html, model),
                ToCentimeters(contentLeft),
                ToCentimeters(currentTop),
                ToCentimeters(contentWidth),
                ToCentimeters(textHeight),
                model.BaseFontFamily,
                template.TextAlign,
                NormalizeVerticalAlign(verticalAlign),
                0.0d));
            currentTop += textHeight;
        }
    }

    private static double PointsToCentimeters(double points)
        => points * 2.54d / 72.0d;

    private static string NormalizeVerticalAlign(string? verticalAlign)
        => string.Equals(verticalAlign, "Middle", StringComparison.OrdinalIgnoreCase)
            || string.Equals(verticalAlign, "Bottom", StringComparison.OrdinalIgnoreCase)
            ? verticalAlign!
            : "Top";

    private static XElement BuildBandRectangle(string name, XElement reportItems, double usableWidth, double top, double height)
        => new(Rdl + "Rectangle",
            new XAttribute("Name", name),
            reportItems,
            new XElement(Rdl + "KeepTogether", "true"),
            new XElement(Rdl + "Top", ToCentimeters(top)),
            new XElement(Rdl + "Left", "0cm"),
            new XElement(Rdl + "Height", ToCentimeters(height)),
            new XElement(Rdl + "Width", ToCentimeters(usableWidth)),
            new XElement(Rdl + "Style",
                new XElement(Rdl + "Border",
                    new XElement(Rdl + "Style", "None"))));

    private static XElement? BuildReportBandSubreport(
        string name,
        bool enabled,
        ReportBandLayoutMode layoutMode,
        string? subreportName,
        string? subreportPath,
        string? subreportServerPath,
        IReadOnlyList<SubreportParameterMapping> parameterMappings,
        ReportModel model,
        double usableWidth,
        double top,
        double height)
    {
        if (!enabled || layoutMode != ReportBandLayoutMode.Subreport)
        {
            return null;
        }

        var reportName = ResolveSubreportReportName(subreportServerPath, subreportName, subreportPath);
        if (string.IsNullOrWhiteSpace(reportName))
        {
            return null;
        }

        return new XElement(Rdl + "Subreport",
            new XAttribute("Name", name),
            new XElement(Rdl + "ReportName", reportName),
            BuildSubreportParameters(parameterMappings, subreportName, subreportPath, model),
            new XElement(Rdl + "Top", ToCentimeters(top)),
            new XElement(Rdl + "Left", "0cm"),
            new XElement(Rdl + "Height", ToCentimeters(Math.Max(0.4d, height))),
            new XElement(Rdl + "Width", ToCentimeters(usableWidth)),
            new XElement(Rdl + "Style",
                new XElement(Rdl + "Border",
                    new XElement(Rdl + "Style", "None"))));
    }

    private static string? ResolveSubreportReportName(string? subreportServerPath, string? subreportName, string? subreportPath)
    {
        // <ReportName> must resolve at design time (Report Builder / Power BI Report Builder
        // look for a sibling .rdl in the same project) and at runtime on the server.
        // A server-absolute value like "/Memorandum" breaks design-time lookup, so prefer
        // the local name/file. The server path is informational and used only as a last
        // resort, with leading slashes stripped so SSRS treats it as a relative reference.
        var localName = NormalizeSubreportReference(subreportName)
            ?? NormalizeSubreportReference(subreportPath);
        if (!string.IsNullOrWhiteSpace(localName))
        {
            return localName;
        }

        return NormalizeSubreportReference(subreportServerPath?.TrimStart('/', '\\'));
    }

    private static XElement? BuildSubreportParameters(
        IReadOnlyList<SubreportParameterMapping> parameterMappings,
        string? subreportName,
        string? subreportPath,
        ReportModel model)
    {
        // SSRS rejects parameters that the subreport does not declare. When a local
        // subreport file can be inspected, auto-forward only the matching parent
        // parameters. Explicit mappings are still honored and override auto mappings.
        var declaredSubreportParameters = TryReadSubreportParameterNames(subreportName, subreportPath, model);
        var autoMappings = declaredSubreportParameters is null
            ? Enumerable.Empty<SubreportParameterMapping>()
            : model.Parameters
                .Where(parameter => !string.IsNullOrWhiteSpace(parameter.Name))
                .Where(parameter => declaredSubreportParameters.Contains(parameter.Name.TrimStart('@')))
                .Select(parameter => new SubreportParameterMapping
                {
                    SubreportParameterName = parameter.Name.TrimStart('@'),
                    SourceKind = SubreportParameterSourceKind.ReportParameter,
                    SourceName = parameter.Name.TrimStart('@')
                });

        var mappings = autoMappings
            .Concat(parameterMappings
            .Where(mapping => !string.IsNullOrWhiteSpace(mapping.SubreportParameterName))
                .Where(mapping =>
                    declaredSubreportParameters is null
                    || declaredSubreportParameters.Contains(mapping.SubreportParameterName.TrimStart('@'))))
            .GroupBy(mapping => mapping.SubreportParameterName.TrimStart('@'), StringComparer.OrdinalIgnoreCase)
            .Select(group => group.Last())
            .ToList();

        if (mappings.Count == 0)
        {
            return null;
        }

        return new XElement(Rdl + "Parameters",
            mappings.Select(mapping =>
                new XElement(Rdl + "Parameter",
                    new XAttribute("Name", mapping.SubreportParameterName.TrimStart('@')),
                    new XElement(Rdl + "Value", BuildSubreportParameterValue(mapping, model)))));
    }

    private static string BuildSubreportParameterValue(SubreportParameterMapping mapping, ReportModel model)
        => mapping.SourceKind switch
        {
            SubreportParameterSourceKind.ReportParameter when !string.IsNullOrWhiteSpace(mapping.SourceName)
                => $"=Parameters!{mapping.SourceName.TrimStart('@')}.Value",
            SubreportParameterSourceKind.ReportVariable when !string.IsNullOrWhiteSpace(mapping.SourceName)
                => BuildTemplateExpression("{" + mapping.SourceName.Trim() + "}", model),
            SubreportParameterSourceKind.Expression when !string.IsNullOrWhiteSpace(mapping.Expression)
                => mapping.Expression.Trim().StartsWith("=", StringComparison.Ordinal)
                    ? mapping.Expression.Trim()
                    : "=" + mapping.Expression.Trim(),
            SubreportParameterSourceKind.StaticValue
                => mapping.StaticValue ?? string.Empty,
            _ => string.Empty
        };

    private static HashSet<string>? TryReadSubreportParameterNames(string? subreportName, string? subreportPath, ReportModel model)
    {
        foreach (var candidate in EnumerateSubreportFileCandidates(subreportName, subreportPath, model))
        {
            try
            {
                if (!File.Exists(candidate))
                {
                    continue;
                }

                var document = XDocument.Load(candidate);
                var report = document.Root;
                if (report is null)
                {
                    continue;
                }

                var ns = report.Name.Namespace;
                return report
                    .Descendants(ns + "ReportParameter")
                    .Select(parameter => parameter.Attribute("Name")?.Value)
                    .Where(name => !string.IsNullOrWhiteSpace(name))
                    .Select(name => name!.TrimStart('@'))
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
            }
            catch
            {
                // If the subreport cannot be inspected, avoid auto-forwarding to keep
                // the generated main report valid. Explicit mappings remain available.
            }
        }

        return null;
    }

    private static IEnumerable<string> EnumerateSubreportFileCandidates(string? subreportName, string? subreportPath, ReportModel model)
    {
        var outputDirectory = string.IsNullOrWhiteSpace(model.OutputPath)
            ? null
            : Path.GetDirectoryName(model.OutputPath);

        foreach (var value in new[] { subreportPath, subreportName })
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            var trimmed = value.Trim();
            foreach (var candidate in ExpandSubreportCandidate(trimmed, outputDirectory))
            {
                yield return candidate;
            }
        }
    }

    private static IEnumerable<string> ExpandSubreportCandidate(string value, string? outputDirectory)
    {
        var paths = new List<string> { value };
        if (string.IsNullOrWhiteSpace(Path.GetExtension(value)))
        {
            paths.Add(value + ".rdl");
            paths.Add(value + ".rdlc");
        }

        foreach (var path in paths)
        {
            yield return path;

            if (!Path.IsPathRooted(path) && !string.IsNullOrWhiteSpace(outputDirectory))
            {
                yield return Path.Combine(outputDirectory, path);
            }
        }
    }

    private static string? NormalizeSubreportReference(string? subreportPath)
    {
        if (string.IsNullOrWhiteSpace(subreportPath))
        {
            return null;
        }

        var value = subreportPath.Trim();
        var extension = Path.GetExtension(value);
        return string.IsNullOrWhiteSpace(extension)
            ? value
            : Path.GetFileNameWithoutExtension(value);
    }

    private static double EstimateTablixHeight(DatasetConfig dataset)
    {
        var fields = dataset.Fields
            .OrderBy(field => field.OrdinalPosition <= 0 ? int.MaxValue : field.OrdinalPosition)
            .ThenBy(field => field.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var groups = GetTablixGroups(fields);
        var aggregateFields = fields
            .Where(HasAggregateFunction)
            .ToList();
        var rowCount = 1 + groups.Count * 2 + 1 + groups.Count + (aggregateFields.Count > 0 ? 1 : 0);
        return Math.Max(1.25d, rowCount * 0.6d);
    }

    /// <summary>
    /// Builds the main tablix using the configured grouping render mode.
    /// </summary>
    private static XElement BuildTablix(
        DatasetConfig dataset,
        double usableWidth,
        double top,
        TablixStyleConfig tablixStyle,
        IReadOnlyList<LocalizationLabel> localizationLabels)
        => tablixStyle.GroupRenderMode switch
        {
            GroupRenderMode.TabularHorizontal => BuildTabularHorizontalTablix(dataset, usableWidth, top, tablixStyle, localizationLabels),
            GroupRenderMode.MatrixCrosstab => BuildMatrixCrosstabTablix(dataset, usableWidth, top, tablixStyle, localizationLabels),
            _ => BuildBandTablix(dataset, usableWidth, top, tablixStyle, localizationLabels)
        };

    /// <summary>
    /// Builds the existing band-oriented tablix layout.
    /// </summary>
    private static XElement BuildBandTablix(
        DatasetConfig dataset,
        double usableWidth,
        double top,
        TablixStyleConfig tablixStyle,
        IReadOnlyList<LocalizationLabel> localizationLabels)
    {
        var fields = dataset.Fields
            .OrderBy(field => field.OrdinalPosition <= 0 ? int.MaxValue : field.OrdinalPosition)
            .ThenBy(field => field.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var groups = GetTablixGroups(fields);
        var detailFields = fields
            .Where(field => field.IncludeInReport && field.GroupLevel <= 0)
            .ToList();
        if (detailFields.Count == 0)
        {
            detailFields = fields
                .Where(field => field.IncludeInReport)
                .ToList();
        }

        if (detailFields.Count == 0)
        {
            detailFields = fields
                .Where(field => field.GroupLevel <= 0)
                .Take(1)
                .ToList();
        }

        if (detailFields.Count == 0)
        {
            detailFields = fields.Take(1).ToList();
        }

        var aggregateFields = detailFields
            .Where(HasAggregateFunction)
            .ToList();
        // The configured percentage controls the whole tablix; individual columns
        // keep the automatic type-based distribution inside that width.
        var tablixWidth = GetTablixWidth(usableWidth, tablixStyle);
        var columnWidths = CalculateTablixColumnWidths(detailFields, tablixWidth);
        var tablixRows = new List<XElement>
        {
            BuildTablixRow(detailFields, "Header", "0.65cm", field => GetLocalizationExpression(localizationLabels, "Column." + field.Name) ?? field.Name, true, tablixStyle)
        };

        foreach (var group in groups)
        {
            tablixRows.Add(BuildSpacerRow(detailFields, $"Group{group.Level}HeaderSpacer"));
            tablixRows.Add(BuildGroupHeaderRow(detailFields, group, tablixStyle, localizationLabels));
        }

        tablixRows.Add(BuildTablixRow(detailFields, "Detail", "0.6cm", field => $"=Fields!{field.Name}.Value", false, tablixStyle));

        foreach (var group in groups.AsEnumerable().Reverse())
        {
            var style = GetGroupVisualStyle(group.Level, tablixStyle);
            tablixRows.Add(BuildAggregateRow(detailFields, aggregateFields, $"sp2rdlGroup{group.Level}", BuildGroupSubtotalLabel(group, localizationLabels), style.BackgroundColor, style.FontStyle, tablixStyle));
        }

        if (aggregateFields.Count > 0)
        {
            tablixRows.Add(BuildAggregateRow(detailFields, aggregateFields, null, GetLocalizationExpression(localizationLabels, "GrandTotal") ?? "Ukupno", GetGrandTotalBackgroundColor(tablixStyle), fontStyle: null, tablixStyle));
        }

        return new XElement(Rdl + "Tablix",
            new XAttribute("Name", "TablixMain"),
            new XElement(Rdl + "TablixBody",
                new XElement(Rdl + "TablixColumns",
                    columnWidths.Select(width => new XElement(Rdl + "TablixColumn",
                        new XElement(Rdl + "Width", ToCentimeters(width))))),
                new XElement(Rdl + "TablixRows", tablixRows)),
            new XElement(Rdl + "TablixColumnHierarchy",
                new XElement(Rdl + "TablixMembers", detailFields.Select(_ => new XElement(Rdl + "TablixMember")))),
            BuildTablixRowHierarchy(groups, dataset.Name, aggregateFields.Count > 0),
            new XElement(Rdl + "DataSetName", dataset.Name),
            new XElement(Rdl + "Top", ToCentimeters(top)),
            new XElement(Rdl + "Left", "0cm"),
            new XElement(Rdl + "Height", ToCentimeters(Math.Max(1.25d, tablixRows.Count * 0.6d))),
            new XElement(Rdl + "Width", ToCentimeters(tablixWidth)),
            new XElement(Rdl + "Style",
                new XElement(Rdl + "Border",
                    new XElement(Rdl + "Style", "None"))));
    }

    /// <summary>
    /// Builds an Excel-like horizontal grouping layout where group fields remain as tablix columns.
    /// </summary>
    private static XElement BuildTabularHorizontalTablix(
        DatasetConfig dataset,
        double usableWidth,
        double top,
        TablixStyleConfig tablixStyle,
        IReadOnlyList<LocalizationLabel> localizationLabels)
    {
        var fields = dataset.Fields
            .OrderBy(field => field.OrdinalPosition <= 0 ? int.MaxValue : field.OrdinalPosition)
            .ThenBy(field => field.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var groups = GetTablixGroups(fields);
        var columns = fields
            .Where(field => field.IncludeInReport)
            .ToList();
        if (columns.Count == 0)
        {
            columns = fields.Take(1).ToList();
        }

        var aggregateFields = columns
            .Where(HasAggregateFunction)
            .ToList();
        var aggregateFieldNames = aggregateFields
            .Select(field => field.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var tablixWidth = GetTablixWidth(usableWidth, tablixStyle);
        var columnWidths = CalculateTablixColumnWidths(columns, tablixWidth);
        var tablixRows = new List<XElement>
        {
            BuildTablixRow(columns, "Header", "0.65cm", field => GetLocalizationExpression(localizationLabels, "Column." + field.Name) ?? field.Name, true, tablixStyle),
            BuildTabularHorizontalDetailRow(columns, groups, aggregateFieldNames, tablixStyle)
        };

        if (tablixStyle.ShowTabularHorizontalSubtotals)
        {
            foreach (var group in groups.AsEnumerable().Reverse())
            {
                var style = GetGroupVisualStyle(group.Level, tablixStyle);
                tablixRows.Add(BuildAggregateRow(columns, aggregateFields, $"sp2rdlGroup{group.Level}", BuildGroupSubtotalLabel(group, localizationLabels), style.BackgroundColor, style.FontStyle, tablixStyle));
            }
        }

        if (tablixStyle.ShowTabularHorizontalGrandTotal && aggregateFields.Count > 0)
        {
            tablixRows.Add(BuildAggregateRow(columns, aggregateFields, null, GetLocalizationExpression(localizationLabels, "GrandTotal") ?? "Ukupno", GetGrandTotalBackgroundColor(tablixStyle), fontStyle: null, tablixStyle));
        }

        return new XElement(Rdl + "Tablix",
            new XAttribute("Name", "TablixMain"),
            new XElement(Rdl + "TablixBody",
                new XElement(Rdl + "TablixColumns",
                    columnWidths.Select(width => new XElement(Rdl + "TablixColumn",
                        new XElement(Rdl + "Width", ToCentimeters(width))))),
                new XElement(Rdl + "TablixRows", tablixRows)),
            new XElement(Rdl + "TablixColumnHierarchy",
                new XElement(Rdl + "TablixMembers", columns.Select(_ => new XElement(Rdl + "TablixMember")))),
            BuildTabularHorizontalRowHierarchy(groups, tablixStyle.ShowTabularHorizontalSubtotals, tablixStyle.ShowTabularHorizontalGrandTotal && aggregateFields.Count > 0),
            new XElement(Rdl + "DataSetName", dataset.Name),
            new XElement(Rdl + "Top", ToCentimeters(top)),
            new XElement(Rdl + "Left", "0cm"),
            new XElement(Rdl + "Height", ToCentimeters(Math.Max(1.25d, tablixRows.Count * 0.6d))),
            new XElement(Rdl + "Width", ToCentimeters(tablixWidth)),
            new XElement(Rdl + "Style",
                new XElement(Rdl + "Border",
                    new XElement(Rdl + "Style", "None"))));
    }

    /// <summary>
    /// Builds a detail-cell expression that suppresses repeated group values and places aggregates on the first group row.
    /// </summary>
    private static string BuildTabularHorizontalDetailExpression(
        DatasetField field,
        IReadOnlyList<TablixGroup> groups,
        ISet<string> aggregateFieldNames)
    {
        if (field.GroupLevel is >= 1 and <= 4)
        {
            return $"=IIF(RowNumber(\"sp2rdlGroup{field.GroupLevel}\") = 1, Fields!{field.Name}.Value, Nothing)";
        }

        if (aggregateFieldNames.Contains(field.Name) && groups.Count > 0)
        {
            var deepestGroup = groups[^1];
            var scopeName = $"sp2rdlGroup{deepestGroup.Level}";
            return $"=IIF(RowNumber(\"{scopeName}\") = 1, {BuildAggregateExpressionBody(field, scopeName)}, Nothing)";
        }

        return $"=Fields!{field.Name}.Value";
    }

    /// <summary>
    /// Builds the horizontal-grouping detail row with group and aggregate cells visually merged inside their group.
    /// </summary>
    private static XElement BuildTabularHorizontalDetailRow(
        IReadOnlyList<DatasetField> columns,
        IReadOnlyList<TablixGroup> groups,
        ISet<string> aggregateFieldNames,
        TablixStyleConfig tablixStyle)
    {
        var aggregateBorderScope = groups.Count > 0
            ? $"sp2rdlGroup{groups[^1].Level}"
            : null;

        return new(Rdl + "TablixRow",
            new XElement(Rdl + "Height", "0.6cm"),
            new XElement(Rdl + "TablixCells",
                columns.Select((field, index) =>
                {
                    var isGroupField = field.GroupLevel is >= 1 and <= 4;
                    var isAggregateField = aggregateFieldNames.Contains(field.Name) && aggregateBorderScope is not null;
                    var borderScope = isGroupField
                        ? $"sp2rdlGroup{field.GroupLevel}"
                        : isAggregateField
                            ? aggregateBorderScope
                            : null;

                    return new XElement(Rdl + "TablixCell",
                        new XElement(Rdl + "CellContents",
                            BuildCellTextbox(
                                $"sp2rdlTabularDetail{index + 1}",
                                BuildTabularHorizontalDetailExpression(field, groups, aggregateFieldNames),
                                isHeader: false,
                                field.Format,
                                GetFieldTextAlign(field),
                                verticalOnlyBorders: isGroupField || isAggregateField,
                                conditionalHorizontalBorderScope: borderScope,
                                tablixStyle: tablixStyle)));
                })));
    }

    /// <summary>
    /// Builds the first crosstab layout: row groups on the left, one dynamic column group, and one measure.
    /// </summary>
    private static XElement BuildMatrixCrosstabTablix(
        DatasetConfig dataset,
        double usableWidth,
        double top,
        TablixStyleConfig tablixStyle,
        IReadOnlyList<LocalizationLabel> localizationLabels)
    {
        var fields = dataset.Fields
            .OrderBy(field => field.OrdinalPosition <= 0 ? int.MaxValue : field.OrdinalPosition)
            .ThenBy(field => field.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var rowGroups = fields
            .Where(field => field.MatrixRole == MatrixFieldRole.RowGroup)
            .OrderBy(field => field.MatrixLevel <= 0 ? int.MaxValue : field.MatrixLevel)
            .ThenBy(field => field.OrdinalPosition <= 0 ? int.MaxValue : field.OrdinalPosition)
            .ThenBy(field => field.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var columnGroups = fields
            .Where(field => field.MatrixRole == MatrixFieldRole.ColumnGroup)
            .OrderBy(field => field.MatrixLevel <= 0 ? int.MaxValue : field.MatrixLevel)
            .ThenBy(field => field.OrdinalPosition <= 0 ? int.MaxValue : field.OrdinalPosition)
            .ThenBy(field => field.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var measures = fields
            .Where(field => field.MatrixRole == MatrixFieldRole.Measure)
            .OrderBy(field => field.OrdinalPosition <= 0 ? int.MaxValue : field.OrdinalPosition)
            .ThenBy(field => field.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        ValidateMatrixCrosstabConfiguration(rowGroups, columnGroups, measures);

        var columnGroup = columnGroups[0];
        var measure = measures[0];
        var columns = rowGroups.Concat([measure, measure]).ToList();
        var tablixWidth = GetTablixWidth(usableWidth, tablixStyle);
        var columnWidths = CalculateMatrixColumnWidths(rowGroups, measure, tablixWidth, tablixStyle);
        var headerRow = BuildMatrixHeaderRow(rowGroups, columnGroup, tablixStyle, localizationLabels);
        var detailRow = BuildMatrixDetailRow(rowGroups, measure, tablixStyle);
        var totalRow = BuildMatrixColumnTotalRow(rowGroups, measure, tablixStyle, localizationLabels);

        return new XElement(Rdl + "Tablix",
            new XAttribute("Name", "TablixMain"),
            new XElement(Rdl + "TablixBody",
                new XElement(Rdl + "TablixColumns",
                    columnWidths.Select(width => new XElement(Rdl + "TablixColumn",
                        new XElement(Rdl + "Width", ToCentimeters(width))))),
                new XElement(Rdl + "TablixRows", headerRow, detailRow, totalRow)),
            BuildMatrixColumnHierarchy(rowGroups, columnGroup),
            BuildMatrixRowHierarchy(rowGroups),
            new XElement(Rdl + "DataSetName", dataset.Name),
            new XElement(Rdl + "Top", ToCentimeters(top)),
            new XElement(Rdl + "Left", "0cm"),
            new XElement(Rdl + "Height", "1.85cm"),
            new XElement(Rdl + "Width", ToCentimeters(tablixWidth)),
            new XElement(Rdl + "Style",
                new XElement(Rdl + "Border",
                    new XElement(Rdl + "Style", "None"))));
    }

    /// <summary>
    /// Validates the first supported matrix shape before RDL generation.
    /// </summary>
    private static void ValidateMatrixCrosstabConfiguration(
        IReadOnlyList<DatasetField> rowGroups,
        IReadOnlyList<DatasetField> columnGroups,
        IReadOnlyList<DatasetField> measures)
    {
        if (rowGroups.Count == 0)
        {
            throw new InvalidOperationException("Matrix / Crosstab requires at least one field with Matrix role = RowGroup.");
        }

        if (columnGroups.Count != 1)
        {
            throw new InvalidOperationException("Matrix / Crosstab currently requires exactly one field with Matrix role = ColumnGroup.");
        }

        if (measures.Count != 1)
        {
            throw new InvalidOperationException("Matrix / Crosstab currently requires exactly one field with Matrix role = Measure.");
        }

        if (!HasAggregateFunction(measures[0]))
        {
            throw new InvalidOperationException("Matrix / Crosstab measure field must have an Aggregate selected.");
        }
    }

    /// <summary>
    /// Calculates matrix widths so the expected dynamic columns plus total column fit inside the configured tablix width.
    /// </summary>
    private static IReadOnlyList<double> CalculateMatrixColumnWidths(
        IReadOnlyList<DatasetField> rowGroups,
        DatasetField measure,
        double tablixWidth,
        TablixStyleConfig tablixStyle)
    {
        var rowGroupWidths = rowGroups
            .Select(field => Math.Min(GetDesiredColumnWidth(field), GetMaximumColumnWidth(field)))
            .ToList();
        var expectedDynamicColumns = Math.Clamp(tablixStyle.MatrixExpectedColumnCount <= 0 ? 6 : tablixStyle.MatrixExpectedColumnCount, 1, 50);
        var totalMeasureColumns = expectedDynamicColumns + 1;
        var availableForMeasures = tablixWidth - rowGroupWidths.Sum();
        var minimumMeasureWidth = Math.Min(GetDesiredColumnWidth(measure), GetMaximumColumnWidth(measure));
        var measureWidth = availableForMeasures > 0
            ? Math.Max(0.45d, availableForMeasures / totalMeasureColumns)
            : 0.8d;

        if (measureWidth < 0.6d && rowGroupWidths.Count > 0)
        {
            var targetRowWidth = Math.Max(0.8d, (tablixWidth - 0.6d * totalMeasureColumns) / rowGroupWidths.Count);
            rowGroupWidths = rowGroupWidths.Select(width => Math.Min(width, targetRowWidth)).ToList();
            measureWidth = Math.Max(0.6d, (tablixWidth - rowGroupWidths.Sum()) / totalMeasureColumns);
        }

        measureWidth = Math.Min(measureWidth, Math.Max(minimumMeasureWidth, 0.8d));
        return rowGroupWidths.Concat([measureWidth, measureWidth]).ToList();
    }

    /// <summary>
    /// Builds the matrix header row with row group captions and the dynamic column group caption.
    /// </summary>
    private static XElement BuildMatrixHeaderRow(
        IReadOnlyList<DatasetField> rowGroups,
        DatasetField columnGroup,
        TablixStyleConfig tablixStyle,
        IReadOnlyList<LocalizationLabel> localizationLabels)
        => new(Rdl + "TablixRow",
            new XElement(Rdl + "Height", "0.65cm"),
            new XElement(Rdl + "TablixCells",
                rowGroups.Select((field, index) => BuildMatrixCell(
                        $"sp2rdlMatrixRowHeader{index + 1}",
                        GetLocalizationExpression(localizationLabels, "Column." + field.Name) ?? field.Name,
                        field,
                        isHeader: true,
                        tablixStyle))
                    .Append(BuildMatrixCell(
                        "sp2rdlMatrixColumnHeader",
                        $"=Fields!{columnGroup.Name}.Value",
                        columnGroup,
                        isHeader: true,
                        tablixStyle))
                    .Append(BuildMatrixCell(
                        "sp2rdlMatrixRowTotalHeader",
                        GetLocalizationExpression(localizationLabels, "GrandTotal") ?? "Ukupno",
                        columnGroup,
                        isHeader: true,
                        tablixStyle))));

    /// <summary>
    /// Builds the matrix value row with row group values and the aggregated measure value.
    /// </summary>
    private static XElement BuildMatrixDetailRow(
        IReadOnlyList<DatasetField> rowGroups,
        DatasetField measure,
        TablixStyleConfig tablixStyle)
        => new(Rdl + "TablixRow",
            new XElement(Rdl + "Height", "0.6cm"),
            new XElement(Rdl + "TablixCells",
                rowGroups.Select((field, index) => BuildMatrixCell(
                        $"sp2rdlMatrixRowValue{index + 1}",
                        $"=Fields!{field.Name}.Value",
                        field,
                        isHeader: false,
                        tablixStyle))
                    .Append(BuildMatrixCell(
                        "sp2rdlMatrixMeasureValue",
                        BuildAggregateExpression(measure, null),
                        measure,
                        isHeader: false,
                        tablixStyle))
                    .Append(BuildMatrixCell(
                        "sp2rdlMatrixRowTotalValue",
                        BuildAggregateExpression(measure, null),
                        measure,
                        isHeader: false,
                        fontWeight: "Bold",
                        backgroundColor: null,
                        tablixStyle))));

    /// <summary>
    /// Builds the matrix bottom total row with column totals and the grand total corner.
    /// </summary>
    private static XElement BuildMatrixColumnTotalRow(
        IReadOnlyList<DatasetField> rowGroups,
        DatasetField measure,
        TablixStyleConfig tablixStyle,
        IReadOnlyList<LocalizationLabel> localizationLabels)
    {
        var cells = new List<XElement>();
        var labelCellContents = new XElement(Rdl + "CellContents",
            BuildCellTextbox(
                "sp2rdlMatrixColumnTotalLabel",
                GetLocalizationExpression(localizationLabels, "GrandTotal") ?? "Ukupno",
                isHeader: false,
                format: null,
                textAlign: "Left",
                backgroundColor: GetGrandTotalBackgroundColor(tablixStyle),
                fontWeight: "Bold",
                tablixStyle: tablixStyle));
        if (rowGroups.Count > 1)
        {
            labelCellContents.Add(new XElement(Rdl + "ColSpan", rowGroups.Count.ToString(CultureInfo.InvariantCulture)));
        }

        cells.Add(new XElement(Rdl + "TablixCell", labelCellContents));
        for (var index = 1; index < rowGroups.Count; index++)
        {
            cells.Add(new XElement(Rdl + "TablixCell"));
        }

        cells.Add(BuildMatrixCell(
            "sp2rdlMatrixColumnTotalValue",
            BuildAggregateExpression(measure, null),
            measure,
            isHeader: false,
            fontWeight: "Bold",
            backgroundColor: GetGrandTotalBackgroundColor(tablixStyle),
            tablixStyle));
        cells.Add(BuildMatrixCell(
            "sp2rdlMatrixGrandTotalValue",
            BuildAggregateExpression(measure, null),
            measure,
            isHeader: false,
            fontWeight: "Bold",
            backgroundColor: GetGrandTotalBackgroundColor(tablixStyle),
            tablixStyle));

        return new XElement(Rdl + "TablixRow",
            new XElement(Rdl + "Height", "0.6cm"),
            new XElement(Rdl + "TablixCells", cells));
    }

    /// <summary>
    /// Builds one matrix cell using the existing textbox styling rules.
    /// </summary>
    private static XElement BuildMatrixCell(
        string name,
        string value,
        DatasetField field,
        bool isHeader,
        string? fontWeight,
        string? backgroundColor,
        TablixStyleConfig tablixStyle)
        => new(Rdl + "TablixCell",
            new XElement(Rdl + "CellContents",
                BuildCellTextbox(
                    name,
                    value,
                    isHeader,
                    field.Format,
                    GetFieldTextAlign(field),
                    backgroundColor ?? (isHeader ? GetHeaderBackgroundColor(tablixStyle) : null),
                    fontWeight ?? (isHeader ? "Bold" : null),
                    tablixStyle: tablixStyle)));

    /// <summary>
    /// Builds one matrix cell using default header/detail styling.
    /// </summary>
    private static XElement BuildMatrixCell(
        string name,
        string value,
        DatasetField field,
        bool isHeader,
        TablixStyleConfig tablixStyle)
        => BuildMatrixCell(name, value, field, isHeader, fontWeight: null, backgroundColor: null, tablixStyle);

    /// <summary>
    /// Builds static row-group columns plus one dynamic column group for the measure area.
    /// </summary>
    private static XElement BuildMatrixColumnHierarchy(IReadOnlyList<DatasetField> rowGroups, DatasetField columnGroup)
        => new(Rdl + "TablixColumnHierarchy",
            new XElement(Rdl + "TablixMembers",
                rowGroups.Select(_ => new XElement(Rdl + "TablixMember"))
                    .Append(new XElement(Rdl + "TablixMember",
                        new XElement(Rdl + "Group",
                            new XAttribute("Name", "sp2rdlMatrixColumnGroup"),
                            new XElement(Rdl + "GroupExpressions",
                                new XElement(Rdl + "GroupExpression", $"=Fields!{columnGroup.Name}.Value"))),
                        new XElement(Rdl + "SortExpressions",
                            new XElement(Rdl + "SortExpression",
                                new XElement(Rdl + "Value", $"=Fields!{columnGroup.Name}.Value")))))
                    .Append(new XElement(Rdl + "TablixMember"))));

    /// <summary>
    /// Builds the matrix row hierarchy from configured row group fields.
    /// </summary>
    private static XElement BuildMatrixRowHierarchy(IReadOnlyList<DatasetField> rowGroups)
        => new(Rdl + "TablixRowHierarchy",
            new XElement(Rdl + "TablixMembers",
                new XElement(Rdl + "TablixMember",
                    new XElement(Rdl + "KeepWithGroup", "After"),
                    new XElement(Rdl + "RepeatOnNewPage", "true")),
                BuildMatrixRowGroupMember(rowGroups, 0),
                new XElement(Rdl + "TablixMember")));

    /// <summary>
    /// Builds nested row group members with a static leaf row for the matrix measure cells.
    /// </summary>
    private static XElement BuildMatrixRowGroupMember(IReadOnlyList<DatasetField> rowGroups, int index)
    {
        var field = rowGroups[index];
        var groupName = $"sp2rdlMatrixRowGroup{index + 1}";
        var childMember = index + 1 < rowGroups.Count
            ? BuildMatrixRowGroupMember(rowGroups, index + 1)
            : new XElement(Rdl + "TablixMember");

        return new XElement(Rdl + "TablixMember",
            new XElement(Rdl + "Group",
                new XAttribute("Name", groupName),
                new XElement(Rdl + "GroupExpressions",
                    new XElement(Rdl + "GroupExpression", $"=Fields!{field.Name}.Value"))),
            new XElement(Rdl + "SortExpressions",
                new XElement(Rdl + "SortExpression",
                    new XElement(Rdl + "Value", $"=Fields!{field.Name}.Value"))),
            new XElement(Rdl + "TablixMembers", childMember));
    }

    private sealed record TablixGroup(int Level, IReadOnlyList<DatasetField> Fields);

    private sealed record GroupVisualStyle(string BackgroundColor, string? FontStyle);

    private static List<TablixGroup> GetTablixGroups(IReadOnlyList<DatasetField> fields)
        => fields
            .Where(field => field.GroupLevel is >= 1 and <= 4)
            .GroupBy(field => field.GroupLevel)
            .OrderBy(group => group.Key)
            .Select(group => new TablixGroup(
                group.Key,
                group
                    .OrderBy(field => field.OrdinalPosition <= 0 ? int.MaxValue : field.OrdinalPosition)
                    .ThenBy(field => field.Name, StringComparer.OrdinalIgnoreCase)
                    .ToList()))
            .ToList();

    private static XElement BuildGroupHeaderRow(
        IReadOnlyList<DatasetField> columns,
        TablixGroup group,
        TablixStyleConfig tablixStyle,
        IReadOnlyList<LocalizationLabel> localizationLabels)
    {
        var style = GetGroupVisualStyle(group.Level, tablixStyle);
        var cellContents = new XElement(Rdl + "CellContents",
            BuildCellTextbox(
                $"sp2rdlGroup{group.Level}Header1",
                BuildGroupHeaderExpression(group, localizationLabels),
                isHeader: true,
                format: null,
                textAlign: "Left",
                backgroundColor: style.BackgroundColor,
                fontWeight: "Bold",
                fontStyle: style.FontStyle,
                horizontalOnlyBorders: true,
                textAlignOverride: "Left",
                tablixStyle: tablixStyle));

        if (columns.Count > 1)
        {
            cellContents.Add(new XElement(Rdl + "ColSpan", columns.Count.ToString(CultureInfo.InvariantCulture)));
        }

        var cells = new List<XElement>
        {
            new(Rdl + "TablixCell", cellContents)
        };

        for (var index = 1; index < columns.Count; index++)
        {
            cells.Add(new XElement(Rdl + "TablixCell"));
        }

        return new XElement(Rdl + "TablixRow",
            new XElement(Rdl + "Height", "0.6cm"),
            new XElement(Rdl + "TablixCells", cells));
    }

    private static string BuildGroupHeaderExpression(TablixGroup group, IReadOnlyList<LocalizationLabel> localizationLabels)
        => "=" + string.Join(" & \"   \" & ", group.Fields.Select(field =>
            (GetLocalizationExpressionBody(localizationLabels, "Group." + field.Name) ?? QuoteExpressionText(field.Name))
            + " & \": \" & CStr(Fields!" + field.Name + ".Value)"));

    private static string BuildGroupSubtotalLabel(TablixGroup group, IReadOnlyList<LocalizationLabel> localizationLabels)
    {
        var subtotal = GetLocalizationExpressionBody(localizationLabels, "Subtotal") ?? QuoteExpressionText("Podzbir");
        var fieldLabels = group.Fields.Select(field => GetLocalizationExpressionBody(localizationLabels, "Group." + field.Name) ?? QuoteExpressionText(field.Name));
        return "=" + subtotal + " & \": \" & " + string.Join(" & \", \" & ", fieldLabels);
    }

    private static XElement BuildAggregateRow(
        IReadOnlyList<DatasetField> columns,
        IReadOnlyList<DatasetField> aggregateFields,
        string? scopeName,
        string label,
        string backgroundColor,
        string? fontStyle,
        TablixStyleConfig tablixStyle)
    {
        // Keep aggregate values visible even when the first visible column also has
        // an aggregate; the label spans only columns before the first aggregate.
        var rowName = MakeRdlName(label);
        var aggregateFieldNames = aggregateFields
            .Select(field => field.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var firstAggregateIndex = columns
            .Select((field, index) => new { Field = field, Index = index })
            .Where(item => aggregateFieldNames.Contains(item.Field.Name))
            .Select(item => (int?)item.Index)
            .FirstOrDefault();
        var labelSpan = firstAggregateIndex is null or <= 0 ? 1 : firstAggregateIndex.Value;
        if (aggregateFieldNames.Count == 0)
        {
            labelSpan = columns.Count;
        }

        var cells = new List<XElement>();
        var index = 0;
        while (index < columns.Count)
        {
            var field = columns[index];
            var hasAggregate = aggregateFieldNames.Contains(field.Name);
            if (index == 0 && !hasAggregate)
            {
                var cellContents = new XElement(Rdl + "CellContents",
                    BuildCellTextbox(
                        $"sp2rdl{rowName}{index + 1}",
                        label,
                        isHeader: false,
                        format: null,
                        textAlign: "Left",
                        backgroundColor: backgroundColor,
                        fontWeight: "Bold",
                        fontStyle: fontStyle,
                        tablixStyle: tablixStyle));
                if (labelSpan > 1)
                {
                    cellContents.Add(new XElement(Rdl + "ColSpan", labelSpan.ToString(CultureInfo.InvariantCulture)));
                }

                cells.Add(new XElement(Rdl + "TablixCell", cellContents));
                for (var placeholderIndex = 1; placeholderIndex < labelSpan; placeholderIndex++)
                {
                    cells.Add(new XElement(Rdl + "TablixCell"));
                }

                index = labelSpan;
                continue;
            }

            cells.Add(new XElement(Rdl + "TablixCell",
                new XElement(Rdl + "CellContents",
                    BuildCellTextbox(
                        $"sp2rdl{rowName}{index + 1}",
                        hasAggregate ? BuildAggregateExpression(field, scopeName) : string.Empty,
                        isHeader: false,
                        field.Format,
                        GetFieldTextAlign(field),
                        backgroundColor,
                        "Bold",
                        fontStyle,
                        tablixStyle: tablixStyle))));
            index++;
        }

        return new XElement(Rdl + "TablixRow",
            new XElement(Rdl + "Height", "0.6cm"),
            new XElement(Rdl + "TablixCells", cells));
    }

    private static XElement BuildSpacerRow(IReadOnlyList<DatasetField> columns, string rowName)
        => BuildCustomTablixRow(
            columns,
            rowName,
            GroupSpacerHeight,
            (_, _) => string.Empty,
            isHeader: false,
            backgroundColor: null,
            fontWeight: null,
            noBorders: true);

    private static string BuildAggregateExpression(DatasetField field, string? scopeName)
    {
        var expressionBody = BuildAggregateExpressionBody(field, scopeName);
        return string.IsNullOrWhiteSpace(expressionBody) ? string.Empty : "=" + expressionBody;
    }

    /// <summary>
    /// Determines whether a dataset field has one of the supported aggregate functions selected.
    /// </summary>
    private static bool HasAggregateFunction(DatasetField field)
        => NormalizeAggregateFunctionName(field.AggregateFunction) is not null;

    /// <summary>
    /// Builds the aggregate expression body so callers can compose it inside larger RDL expressions.
    /// </summary>
    private static string BuildAggregateExpressionBody(DatasetField field, string? scopeName)
    {
        var scope = string.IsNullOrWhiteSpace(scopeName) ? string.Empty : ", " + QuoteExpressionText(scopeName);
        return NormalizeAggregateFunctionName(field.AggregateFunction) switch
        {
            "Sum" => $"Sum(Fields!{field.Name}.Value{scope})",
            "Avg" => $"Avg(Fields!{field.Name}.Value{scope})",
            "Min" => $"Min(Fields!{field.Name}.Value{scope})",
            "Max" => $"Max(Fields!{field.Name}.Value{scope})",
            "Count" => $"Count(Fields!{field.Name}.Value{scope})",
            "CountDistinct" => $"CountDistinct(Fields!{field.Name}.Value{scope})",
            _ => string.Empty
        };
    }

    /// <summary>
    /// Normalizes aggregate function names from UI or saved state to the canonical generator names.
    /// </summary>
    private static string? NormalizeAggregateFunctionName(string? aggregateFunction)
    {
        if (string.IsNullOrWhiteSpace(aggregateFunction))
        {
            return null;
        }

        return aggregateFunction.Trim().ToLowerInvariant() switch
        {
            "sum" => "Sum",
            "avg" => "Avg",
            "min" => "Min",
            "max" => "Max",
            "count" => "Count",
            "countdistinct" => "CountDistinct",
            "count_distinct" => "CountDistinct",
            "count distinct" => "CountDistinct",
            _ => null
        };
    }

    private static GroupVisualStyle GetGroupVisualStyle(int level, TablixStyleConfig tablixStyle)
        => level switch
        {
            1 => new(ShiftColor(tablixStyle.ShadeBaseColor, -0.10d, "#D4D4D4"), null),
            2 => new(ShiftColor(tablixStyle.ShadeBaseColor, -0.06d, "#DEDEDE"), "Italic"),
            3 => new(ShiftColor(tablixStyle.ShadeBaseColor, -0.02d, "#E8E8E8"), null),
            4 => new(ShiftColor(tablixStyle.ShadeBaseColor, 0.03d, "#F2F2F2"), "Italic"),
            _ => new(ShiftColor(tablixStyle.ShadeBaseColor, 0.03d, "#F2F2F2"), null)
        };

    private static string GetHeaderBackgroundColor(TablixStyleConfig tablixStyle)
        => NormalizeHexColor(tablixStyle.ShadeBaseColor, HeaderBackgroundColor);

    private static string GetGrandTotalBackgroundColor(TablixStyleConfig tablixStyle)
        => ShiftColor(tablixStyle.ShadeBaseColor, -0.20d, GrandTotalBackgroundColor);

    private static double GetTablixWidth(double usableWidth, TablixStyleConfig tablixStyle)
    {
        var percent = tablixStyle.WidthPercent <= 0 ? 100.0d : Math.Clamp(tablixStyle.WidthPercent, 1.0d, 100.0d);
        return Math.Max(1.0d, usableWidth * percent / 100.0d);
    }

    /// <summary>
    /// Calculates generated column widths, honoring optional per-column width percentages before auto-sizing the rest.
    /// </summary>
    private static IReadOnlyList<double> CalculateTablixColumnWidths(IReadOnlyList<DatasetField> fields, double usableWidth)
    {
        if (fields.Count == 0)
        {
            return [];
        }

        if (fields.Any(field => field.WidthPercent > 0))
        {
            return CalculateTablixColumnWidthsWithOverrides(fields, usableWidth);
        }

        // Start with type-aware widths, then stretch mostly text columns so the
        // tablix fills its configured width without letting one long field dominate.
        var desiredWidths = fields.Select(GetDesiredColumnWidth).ToList();
        var minimumWidths = fields.Select(GetMinimumColumnWidth).ToList();
        var maximumWidths = fields.Select(GetMaximumColumnWidth).ToList();
        var totalDesired = desiredWidths.Sum();

        if (totalDesired <= usableWidth)
        {
            var extraWidth = usableWidth - totalDesired;
            var flexibleIndexes = fields
                .Select((field, index) => new { Field = field, Index = index })
                .Where(item => IsLongTextField(item.Field))
                .Select(item => item.Index)
                .DefaultIfEmpty()
                .ToList();

            if (flexibleIndexes.Count == 1 && flexibleIndexes[0] == 0 && !IsLongTextField(fields[0]))
            {
                flexibleIndexes = Enumerable.Range(0, fields.Count).ToList();
            }

            DistributeExtraWidth(desiredWidths, maximumWidths, flexibleIndexes, extraWidth);
            DistributeExtraWidth(desiredWidths, maximumWidths, Enumerable.Range(0, fields.Count), usableWidth - desiredWidths.Sum());
            FillRemainingWidth(desiredWidths, fields, usableWidth);
            return desiredWidths;
        }

        var minimumTotal = minimumWidths.Sum();
        if (minimumTotal >= usableWidth)
        {
            var scale = usableWidth / minimumTotal;
            return minimumWidths.Select(width => Math.Max(0.5d, width * scale)).ToList();
        }

        var shrinkableTotal = desiredWidths.Zip(minimumWidths, (desired, minimum) => desired - minimum).Sum();
        var shrinkBy = totalDesired - usableWidth;
        if (shrinkableTotal <= 0)
        {
            return minimumWidths;
        }

        return desiredWidths
            .Zip(minimumWidths, (desired, minimum) => desired - ((desired - minimum) / shrinkableTotal * shrinkBy))
            .ToList();
    }

    /// <summary>
    /// Applies user-defined width percentages and auto-sizes columns that do not have an explicit width.
    /// </summary>
    private static IReadOnlyList<double> CalculateTablixColumnWidthsWithOverrides(IReadOnlyList<DatasetField> fields, double usableWidth)
    {
        var widths = new double[fields.Count];
        var automaticFields = new List<DatasetField>();
        var automaticIndexes = new List<int>();

        for (var index = 0; index < fields.Count; index++)
        {
            var widthPercent = fields[index].WidthPercent > 0
                ? Math.Clamp(fields[index].WidthPercent, 1.0d, 100.0d)
                : 0.0d;
            if (widthPercent > 0)
            {
                widths[index] = usableWidth * widthPercent / 100.0d;
                continue;
            }

            automaticFields.Add(fields[index]);
            automaticIndexes.Add(index);
        }

        var remainingWidth = usableWidth - widths.Sum();
        if (automaticFields.Count > 0 && remainingWidth > 0.1d)
        {
            var automaticWidths = CalculateTablixColumnWidths(automaticFields, remainingWidth);
            for (var index = 0; index < automaticIndexes.Count; index++)
            {
                widths[automaticIndexes[index]] = automaticWidths[index];
            }

            return widths;
        }

        if (automaticFields.Count > 0)
        {
            foreach (var index in automaticIndexes)
            {
                widths[index] = GetMinimumColumnWidth(fields[index]);
            }
        }

        var totalWidth = widths.Sum();
        if (totalWidth <= 0.0d)
        {
            return Enumerable.Repeat(usableWidth / fields.Count, fields.Count).ToList();
        }

        var scale = usableWidth / totalWidth;
        return widths.Select(width => Math.Max(0.35d, width * scale)).ToList();
    }

    private static void DistributeExtraWidth(IList<double> widths, IReadOnlyList<double> maximumWidths, IEnumerable<int> indexes, double extraWidth)
    {
        var availableIndexes = indexes.Distinct().Where(index => index >= 0 && index < widths.Count).ToList();
        while (extraWidth > 0.001d && availableIndexes.Count > 0)
        {
            var perColumn = extraWidth / availableIndexes.Count;
            var usedWidth = 0.0d;
            foreach (var index in availableIndexes.ToList())
            {
                var capacity = maximumWidths[index] - widths[index];
                var increment = Math.Min(capacity, perColumn);
                if (increment <= 0)
                {
                    availableIndexes.Remove(index);
                    continue;
                }

                widths[index] += increment;
                usedWidth += increment;
            }

            if (usedWidth <= 0)
            {
                break;
            }

            extraWidth -= usedWidth;
        }
    }

    private static void FillRemainingWidth(IList<double> widths, IReadOnlyList<DatasetField> fields, double usableWidth)
    {
        var remainingWidth = usableWidth - widths.Sum();
        if (remainingWidth <= 0.001d || widths.Count == 0)
        {
            return;
        }

        var targetIndex = FindBestFillColumnIndex(fields);
        widths[targetIndex] += remainingWidth;
    }

    private static int FindBestFillColumnIndex(IReadOnlyList<DatasetField> fields)
    {
        var longTextIndex = fields
            .Select((field, index) => new { Field = field, Index = index })
            .Where(item => IsLongTextField(item.Field))
            .OrderByDescending(item => TryGetSqlTypeLength(item.Field.SqlTypeName) ?? int.MaxValue)
            .ThenBy(item => item.Index)
            .Select(item => (int?)item.Index)
            .FirstOrDefault();
        if (longTextIndex.HasValue)
        {
            return longTextIndex.Value;
        }

        var textIndex = fields
            .Select((field, index) => new { Field = field, Index = index })
            .Where(item => IsTextSqlType(NormalizeSqlType(item.Field.SqlTypeName)))
            .OrderByDescending(item => TryGetSqlTypeLength(item.Field.SqlTypeName) ?? 0)
            .ThenBy(item => item.Index)
            .Select(item => (int?)item.Index)
            .FirstOrDefault();
        return textIndex ?? Math.Max(0, fields.Count - 1);
    }

    private static double GetDesiredColumnWidth(DatasetField field)
    {
        var normalized = NormalizeSqlType(field.SqlTypeName);
        if (IsNumericSqlType(normalized))
        {
            return normalized is "tinyint" or "smallint" or "int" ? 1.6d : 2.1d;
        }

        if (IsDateSqlType(normalized))
        {
            return normalized == "time" ? 1.7d : 2.2d;
        }

        if (normalized == "bit")
        {
            return 1.2d;
        }

        var length = TryGetSqlTypeLength(field.SqlTypeName);
        if (IsTextSqlType(normalized) && length is > 0 and <= 20)
        {
            return Math.Clamp(1.4d + length.Value * 0.07d, 1.8d, 3.0d);
        }

        return IsLongTextField(field) ? 4.0d : 2.4d;
    }

    private static double GetMinimumColumnWidth(DatasetField field)
    {
        var normalized = NormalizeSqlType(field.SqlTypeName);
        if (IsNumericSqlType(normalized))
        {
            return 1.3d;
        }

        if (IsDateSqlType(normalized))
        {
            return 1.7d;
        }

        return IsLongTextField(field) ? 2.2d : 1.5d;
    }

    private static double GetMaximumColumnWidth(DatasetField field)
    {
        var normalized = NormalizeSqlType(field.SqlTypeName);
        if (IsNumericSqlType(normalized) || IsDateSqlType(normalized) || normalized == "bit")
        {
            return GetDesiredColumnWidth(field) + 0.4d;
        }

        return IsLongTextField(field) ? 6.0d : 3.4d;
    }

    private static bool IsLongTextField(DatasetField field)
    {
        var normalized = NormalizeSqlType(field.SqlTypeName);
        if (!IsTextSqlType(normalized))
        {
            return false;
        }

        var length = TryGetSqlTypeLength(field.SqlTypeName);
        return length is null or > 20;
    }

    private static bool IsNumericSqlType(string normalizedSqlType)
        => normalizedSqlType is "tinyint" or "smallint" or "int" or "bigint" or "decimal" or "numeric" or "money" or "smallmoney" or "float" or "real";

    private static bool IsDateSqlType(string normalizedSqlType)
        => normalizedSqlType is "date" or "datetime" or "datetime2" or "smalldatetime" or "datetimeoffset" or "time";

    private static bool IsTextSqlType(string normalizedSqlType)
        => normalizedSqlType is "char" or "varchar" or "nchar" or "nvarchar" or "text" or "ntext";

    private static int? TryGetSqlTypeLength(string sqlTypeName)
    {
        var openParen = sqlTypeName.IndexOf('(');
        var closeParen = sqlTypeName.IndexOf(')');
        if (openParen < 0 || closeParen <= openParen)
        {
            return null;
        }

        var lengthText = sqlTypeName[(openParen + 1)..closeParen].Split(',', 2)[0].Trim();
        if (lengthText.Equals("max", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return int.TryParse(lengthText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var length)
            ? length
            : null;
    }

    private static XElement BuildTablixRowHierarchy(IReadOnlyList<TablixGroup> groups, string datasetName, bool includeGrandTotal)
    {
        var members = new List<XElement>
        {
            new(Rdl + "TablixMember",
                new XElement(Rdl + "KeepWithGroup", "After"),
                new XElement(Rdl + "RepeatOnNewPage", "true"))
        };

        members.Add(groups.Count == 0
            ? BuildDetailsMember()
            : BuildGroupMember(groups, 0));

        if (includeGrandTotal)
        {
            members.Add(new XElement(Rdl + "TablixMember"));
        }

        return new XElement(Rdl + "TablixRowHierarchy",
            new XElement(Rdl + "TablixMembers", members));
    }

    /// <summary>
    /// Builds row hierarchy for horizontal grouping without separate group header rows.
    /// </summary>
    private static XElement BuildTabularHorizontalRowHierarchy(IReadOnlyList<TablixGroup> groups, bool includeSubtotals, bool includeGrandTotal)
    {
        var members = new List<XElement>
        {
            new(Rdl + "TablixMember",
                new XElement(Rdl + "KeepWithGroup", "After"),
                new XElement(Rdl + "RepeatOnNewPage", "true"))
        };

        members.Add(groups.Count == 0
            ? BuildDetailsMember()
            : BuildTabularHorizontalGroupMember(groups, 0, includeSubtotals));

        if (includeGrandTotal)
        {
            members.Add(new XElement(Rdl + "TablixMember"));
        }

        return new XElement(Rdl + "TablixRowHierarchy",
            new XElement(Rdl + "TablixMembers", members));
    }

    /// <summary>
    /// Builds one nested group member for horizontal grouping with optional subtotal rows.
    /// </summary>
    private static XElement BuildTabularHorizontalGroupMember(IReadOnlyList<TablixGroup> groups, int index, bool includeSubtotals)
    {
        var group = groups[index];
        var groupScopeName = $"sp2rdlGroup{group.Level}";
        var childMembers = new List<XElement>
        {
            index + 1 < groups.Count
                ? BuildTabularHorizontalGroupMember(groups, index + 1, includeSubtotals)
                : BuildDetailsMember()
        };
        if (includeSubtotals)
        {
            childMembers.Add(new XElement(Rdl + "TablixMember"));
        }

        return new XElement(Rdl + "TablixMember",
            new XElement(Rdl + "Group",
                new XAttribute("Name", groupScopeName),
                new XElement(Rdl + "GroupExpressions",
                    group.Fields.Select(field =>
                        new XElement(Rdl + "GroupExpression", $"=Fields!{field.Name}.Value")))),
            new XElement(Rdl + "SortExpressions",
                group.Fields.Select(field =>
                    new XElement(Rdl + "SortExpression",
                        new XElement(Rdl + "Value", $"=Fields!{field.Name}.Value")))),
            new XElement(Rdl + "TablixMembers", childMembers));
    }

    private static XElement BuildGroupMember(IReadOnlyList<TablixGroup> groups, int index)
    {
        var group = groups[index];
        var groupScopeName = $"sp2rdlGroup{group.Level}";
        var childMembers = new List<XElement>
        {
            BuildSpacerMember(group, groups.Take(index).ToList())
        };

        childMembers.Add(new XElement(Rdl + "TablixMember"));

        childMembers.Add(index + 1 < groups.Count
            ? BuildGroupMember(groups, index + 1)
            : BuildDetailsMember());
        childMembers.Add(new XElement(Rdl + "TablixMember"));

        return new XElement(Rdl + "TablixMember",
            new XElement(Rdl + "Group",
                new XAttribute("Name", groupScopeName),
                new XElement(Rdl + "GroupExpressions",
                    group.Fields.Select(field =>
                        new XElement(Rdl + "GroupExpression", $"=Fields!{field.Name}.Value")))),
            new XElement(Rdl + "SortExpressions",
                group.Fields.Select(field =>
                    new XElement(Rdl + "SortExpression",
                        new XElement(Rdl + "Value", $"=Fields!{field.Name}.Value")))),
            new XElement(Rdl + "TablixMembers", childMembers));
    }

    private static XElement BuildSpacerMember(TablixGroup group, IReadOnlyList<TablixGroup> parentGroups)
        => new(Rdl + "TablixMember",
            new XElement(Rdl + "Visibility",
                new XElement(Rdl + "Hidden", BuildFirstGroupInstanceHiddenExpression(group, parentGroups))));

    private static string BuildFirstGroupInstanceHiddenExpression(TablixGroup group, IReadOnlyList<TablixGroup> parentGroups)
    {
        var currentGroupField = group.Fields.First();
        var conditions = new List<string>
        {
            $"IsNothing(Previous(Fields!{currentGroupField.Name}.Value))"
        };

        conditions.AddRange(parentGroups.SelectMany(parentGroup => parentGroup.Fields).Select(field =>
            $"CStr(Fields!{field.Name}.Value) <> CStr(Previous(Fields!{field.Name}.Value))"));

        return "=IIF(" + string.Join(" OR ", conditions) + ", True, False)";
    }

    private static XElement BuildDetailsMember()
        => new(Rdl + "TablixMember",
            new XElement(Rdl + "Group", new XAttribute("Name", "Details")));

    private static XElement? BuildParameterSummaryPanel(ReportModel model, double usableWidth, double top)
    {
        var parameters = GetSummaryParameters(model);
        if (parameters.Count == 0)
        {
            return null;
        }

        return BuildPositionedTextbox(
            "sp2rdlParameterSummary",
            BuildParameterSummaryExpression(parameters),
            "0cm",
            ToCentimeters(top + 0.15d),
            ToCentimeters(usableWidth),
            ToCentimeters(GetParameterSummaryHeight(model) - 0.3d),
            "Left",
            model.BaseFontFamily,
            "8pt",
            borderStyle: "Solid",
            backgroundColor: "#F7F7F7");
    }

    private static List<ReportParameter> GetSummaryParameters(ReportModel model)
        => model.Parameters
            .Where(parameter => !parameter.Hidden)
            .OrderBy(parameter => parameter.OrdinalNumber <= 0 ? int.MaxValue : parameter.OrdinalNumber)
            .ThenBy(parameter => parameter.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static double GetParameterSummaryHeight(ReportModel model)
    {
        var count = GetSummaryParameters(model).Count;
        return count == 0 ? 0.0d : Math.Max(0.9d, 0.35d + count * 0.32d);
    }

    private static XElement? BuildValidationWarningPanel(ReportModel model, double usableWidth, double top)
    {
        var rules = GetComparisonRules(model);
        if (rules.Count == 0)
        {
            return null;
        }

        var reportItems = new XElement(Rdl + "ReportItems");
        var lineHeight = 0.45d;
        for (var index = 0; index < rules.Count; index++)
        {
            var parameter = rules[index];
            reportItems.Add(BuildPositionedTextbox(
                $"sp2rdlValidationWarning{index + 1}",
                BuildValidationWarningText(parameter),
                "0cm",
                ToCentimeters(index * lineHeight),
                ToCentimeters(usableWidth),
                ToCentimeters(lineHeight),
                "Left",
                model.BaseFontFamily,
                "8pt",
                hiddenExpression: BuildValidationHiddenExpression(parameter),
                borderStyle: "Solid",
                backgroundColor: "#FFF4CE",
                fontColor: "#7A2E0E"));
        }

        return new XElement(Rdl + "Rectangle",
            new XAttribute("Name", "sp2rdlValidationWarnings"),
            reportItems,
            new XElement(Rdl + "KeepTogether", "true"),
            new XElement(Rdl + "Top", ToCentimeters(top + 0.1d)),
            new XElement(Rdl + "Left", "0cm"),
            new XElement(Rdl + "Height", ToCentimeters(GetValidationWarningHeight(model) - 0.2d)),
            new XElement(Rdl + "Width", ToCentimeters(usableWidth)),
            new XElement(Rdl + "Style",
                new XElement(Rdl + "Border",
                    new XElement(Rdl + "Style", "None"))));
    }

    private static List<ReportParameter> GetComparisonRules(ReportModel model)
        => model.Parameters
            .Where(parameter => !parameter.Hidden
                && !parameter.MultiValue
                && !string.IsNullOrWhiteSpace(parameter.CompareToParameterName)
                && !string.IsNullOrWhiteSpace(parameter.CompareOperator))
            .OrderBy(parameter => parameter.OrdinalNumber <= 0 ? int.MaxValue : parameter.OrdinalNumber)
            .ThenBy(parameter => parameter.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static double GetValidationWarningHeight(ReportModel model)
    {
        var count = GetComparisonRules(model).Count;
        return count == 0 ? 0.0d : 0.2d + count * 0.45d;
    }

    private static string BuildValidationWarningText(ReportParameter parameter)
        => "=\"Upozorenje: "
            + EscapeExpressionText(GetParameterCaption(parameter))
            + " mora biti "
            + EscapeExpressionText(parameter.CompareOperator ?? string.Empty)
            + " "
            + EscapeExpressionText(parameter.CompareToParameterName ?? string.Empty)
            + ".\"";

    private static string BuildValidationHiddenExpression(ReportParameter parameter)
    {
        var parameterValue = $"Parameters!{parameter.Name}.Value";
        var compareValue = $"Parameters!{parameter.CompareToParameterName}.Value";
        return $"=IIF(IsNothing({parameterValue}) OR IsNothing({compareValue}), True, {parameterValue} {parameter.CompareOperator} {compareValue})";
    }

    private static string GetParameterCaption(ReportParameter parameter)
        => string.IsNullOrWhiteSpace(parameter.Prompt) ? parameter.Name : parameter.Prompt;

    private static string BuildParameterSummaryExpression(IReadOnlyList<ReportParameter> parameters)
        => "=" + string.Join(" & vbCrLf & ", parameters.Select(BuildParameterSummaryLineExpression));

    private static string BuildParameterSummaryLineExpression(ReportParameter parameter)
        => QuoteExpressionText((string.IsNullOrWhiteSpace(parameter.Prompt) ? parameter.Name : parameter.Prompt).Trim() + ": ")
            + " & " + BuildParameterValueExpression(parameter);

    private static string BuildParameterValueExpression(ReportParameter parameter)
    {
        var parameterReference = $"Parameters!{parameter.Name}.";
        if (parameter.MultiValue)
        {
            if (parameter.Lookup is not null && !string.IsNullOrWhiteSpace(parameter.Lookup.DatasetName))
            {
                return $"IIF({parameterReference}Count = CountRows({QuoteExpressionText(parameter.Lookup.DatasetName)}), \"svi\", Join({parameterReference}Label, \", \"))";
            }

            return $"Join({parameterReference}Value, \", \")";
        }

        var selectedValueExpression = parameter.Lookup is not null
            ? parameterReference + "Label"
            : "CStr(" + parameterReference + "Value)";

        return parameter.Nullable
            ? $"IIF(IsNothing({parameterReference}Value), \"svi\", {selectedValueExpression})"
            : selectedValueExpression;
    }

    private static XElement BuildTablixRow(
        IReadOnlyList<DatasetField> fields,
        string rowName,
        string height,
        Func<DatasetField, string> valueFactory,
        bool isHeader,
        TablixStyleConfig tablixStyle)
        => BuildCustomTablixRow(
            fields,
            rowName,
            height,
            (field, _) => valueFactory(field),
            isHeader,
            isHeader ? GetHeaderBackgroundColor(tablixStyle) : null,
            isHeader ? "Bold" : null,
            tablixStyle: tablixStyle);

    private static XElement BuildCustomTablixRow(
        IReadOnlyList<DatasetField> fields,
        string rowName,
        string height,
        Func<DatasetField, int, string> valueFactory,
        bool isHeader,
        string? backgroundColor,
        string? fontWeight,
        string? fontStyle = null,
        bool horizontalOnlyBorders = false,
        bool noBorders = false,
        TablixStyleConfig? tablixStyle = null)
        => new(Rdl + "TablixRow",
            new XElement(Rdl + "Height", height),
            new XElement(Rdl + "TablixCells",
                fields.Select((field, index) => new XElement(Rdl + "TablixCell",
                    new XElement(Rdl + "CellContents",
                        BuildCellTextbox(
                            $"sp2rdl{rowName}{index + 1}",
                            valueFactory(field, index),
                            isHeader,
                            field.Format,
                            GetFieldTextAlign(field),
                            backgroundColor,
                            fontWeight,
                            fontStyle,
                            horizontalOnlyBorders,
                            noBorders,
                            tablixStyle: tablixStyle))))));

    private static XElement BuildCellTextbox(
        string name,
        string value,
        bool isHeader,
        string? format,
        string textAlign,
        string? backgroundColor = null,
        string? fontWeight = null,
        string? fontStyle = null,
        bool horizontalOnlyBorders = false,
        bool noBorders = false,
        bool verticalOnlyBorders = false,
        string? conditionalHorizontalBorderScope = null,
        string? textAlignOverride = null,
        TablixStyleConfig? tablixStyle = null)
    {
        tablixStyle ??= new TablixStyleConfig();
        var textRunStyle = new XElement(Rdl + "Style",
            new XElement(Rdl + "FontFamily", string.IsNullOrWhiteSpace(tablixStyle.FontFamily) ? TablixFontFamily : tablixStyle.FontFamily),
            new XElement(Rdl + "FontSize", ToPoints(tablixStyle.FontSizeInPoints <= 0 ? 9.0d : tablixStyle.FontSizeInPoints)),
            new XElement(Rdl + "Color", NormalizeHexColor(tablixStyle.FontColor, "#000000")));
        if (isHeader || !string.IsNullOrWhiteSpace(fontWeight))
        {
            textRunStyle.Add(new XElement(Rdl + "FontWeight", string.IsNullOrWhiteSpace(fontWeight) ? "Bold" : fontWeight));
        }

        if (!string.IsNullOrWhiteSpace(fontStyle))
        {
            textRunStyle.Add(new XElement(Rdl + "FontStyle", fontStyle));
        }

        if (!isHeader && !string.IsNullOrWhiteSpace(format))
        {
            textRunStyle.Add(new XElement(Rdl + "Format", format));
        }

        return new XElement(Rdl + "Textbox",
            new XAttribute("Name", name),
            new XElement(Rdl + "CanGrow", "true"),
            new XElement(Rdl + "KeepTogether", "true"),
            new XElement(Rdl + "Paragraphs",
                new XElement(Rdl + "Paragraph",
                    new XElement(Rdl + "TextRuns",
                        new XElement(Rdl + "TextRun",
                            new XElement(Rdl + "Value", value),
                            textRunStyle)),
                new XElement(Rdl + "Style",
                    new XElement(Rdl + "TextAlign", textAlignOverride ?? (isHeader ? "Center" : textAlign))))),
            new XElement(Rdl + "Style",
                BuildCellBorders(horizontalOnlyBorders, noBorders, verticalOnlyBorders, conditionalHorizontalBorderScope, tablixStyle),
                string.IsNullOrWhiteSpace(backgroundColor)
                    ? null
                    : new XElement(Rdl + "BackgroundColor", backgroundColor),
                new XElement(Rdl + "PaddingLeft", "2pt"),
                new XElement(Rdl + "PaddingRight", "2pt"),
                new XElement(Rdl + "PaddingTop", "2pt"),
                new XElement(Rdl + "PaddingBottom", "2pt")));
    }

    /// <summary>
    /// Builds RDL border elements for normal, horizontal-only, vertical-only, or borderless cells.
    /// </summary>
    private static object[] BuildCellBorders(bool horizontalOnlyBorders, bool noBorders, bool verticalOnlyBorders, string? conditionalHorizontalBorderScope, TablixStyleConfig tablixStyle)
    {
        var borderColor = NormalizeHexColor(tablixStyle.BorderColor, ReportLineColor);
        var borderWidth = ToPoints(tablixStyle.BorderWidthInPoints <= 0 ? 0.5d : tablixStyle.BorderWidthInPoints);
        if (noBorders)
        {
            return
            [
                new XElement(Rdl + "Border",
                    new XElement(Rdl + "Style", "None"))
            ];
        }

        if (verticalOnlyBorders)
        {
            var borders = new List<object>
            {
                new XElement(Rdl + "Border",
                    new XElement(Rdl + "Style", "None"))
            };
            var topBorder = BuildConditionalHorizontalBorder("TopBorder", conditionalHorizontalBorderScope, isTopBorder: true, borderColor, borderWidth);
            var bottomBorder = BuildConditionalHorizontalBorder("BottomBorder", conditionalHorizontalBorderScope, isTopBorder: false, borderColor, borderWidth);
            if (topBorder is not null)
            {
                borders.Add(topBorder);
            }

            if (bottomBorder is not null)
            {
                borders.Add(bottomBorder);
            }

            borders.Add(new XElement(Rdl + "LeftBorder",
                new XElement(Rdl + "Style", "Solid"),
                new XElement(Rdl + "Color", borderColor),
                new XElement(Rdl + "Width", borderWidth)));
            borders.Add(new XElement(Rdl + "RightBorder",
                new XElement(Rdl + "Style", "Solid"),
                new XElement(Rdl + "Color", borderColor),
                new XElement(Rdl + "Width", borderWidth)));
            return borders.ToArray();
        }

        if (!horizontalOnlyBorders)
        {
            return
            [
                new XElement(Rdl + "Border",
                    new XElement(Rdl + "Style", "Solid"),
                    new XElement(Rdl + "Color", borderColor),
                    new XElement(Rdl + "Width", borderWidth))
            ];
        }

        return
        [
            new XElement(Rdl + "Border",
                new XElement(Rdl + "Style", "None")),
            new XElement(Rdl + "TopBorder",
                new XElement(Rdl + "Style", "Solid"),
                new XElement(Rdl + "Color", borderColor),
                new XElement(Rdl + "Width", borderWidth)),
            new XElement(Rdl + "BottomBorder",
                new XElement(Rdl + "Style", "Solid"),
                new XElement(Rdl + "Color", borderColor),
                new XElement(Rdl + "Width", borderWidth)),
            new XElement(Rdl + "LeftBorder",
                new XElement(Rdl + "Style", "Solid"),
                new XElement(Rdl + "Color", borderColor),
                new XElement(Rdl + "Width", borderWidth)),
            new XElement(Rdl + "RightBorder",
                new XElement(Rdl + "Style", "Solid"),
                new XElement(Rdl + "Color", borderColor),
                new XElement(Rdl + "Width", borderWidth))
        ];
    }

    /// <summary>
    /// Builds a top or bottom border that appears only at the start or end of a group scope.
    /// </summary>
    private static XElement? BuildConditionalHorizontalBorder(
        string borderElementName,
        string? scopeName,
        bool isTopBorder,
        string borderColor,
        string borderWidth)
    {
        if (string.IsNullOrWhiteSpace(scopeName))
        {
            return null;
        }

        var condition = isTopBorder
            ? $"RowNumber({QuoteExpressionText(scopeName)}) = 1"
            : $"RowNumber({QuoteExpressionText(scopeName)}) = CountRows({QuoteExpressionText(scopeName)})";
        return new XElement(Rdl + borderElementName,
            new XElement(Rdl + "Style", $"=IIF({condition}, \"Solid\", \"None\")"),
            new XElement(Rdl + "Color", borderColor),
            new XElement(Rdl + "Width", borderWidth));
    }

    private static string GetFieldTextAlign(DatasetField field)
    {
        if (string.Equals(field.TextAlign, "Left", StringComparison.OrdinalIgnoreCase)
            || string.Equals(field.TextAlign, "Center", StringComparison.OrdinalIgnoreCase)
            || string.Equals(field.TextAlign, "Right", StringComparison.OrdinalIgnoreCase))
        {
            return field.TextAlign!;
        }

        var normalized = NormalizeSqlType(field.SqlTypeName);
        return normalized switch
        {
            "bit" or "tinyint" or "smallint" or "int" or "bigint" or "decimal" or "numeric" or "money" or "smallmoney" or "float" or "real" => "Right",
            "date" or "datetime" or "datetime2" or "smalldatetime" or "datetimeoffset" or "time" => "Center",
            _ => "Left"
        };
    }

    private static bool ShouldEmitPageHeader(PageHeaderConfig header)
    {
        if (!header.Enabled)
        {
            return false;
        }

        // SSRS reserves the page-header slot regardless of PrintOnFirstPage/LastPage.
        // To avoid an empty band leaving a white strip on every page, omit the
        // <PageHeader> element entirely when the user left both texts blank.
        return !string.IsNullOrWhiteSpace(header.LeftText)
            || !string.IsNullOrWhiteSpace(header.RightText);
    }

    private static XElement BuildPageHeader(ReportModel model)
    {
        var header = model.PageHeader;
        var usableWidth = GetUsablePageWidth(model.PageSetup);
        var gap = Math.Min(0.5d, usableWidth / 20d);
        var textboxWidth = Math.Max(1.0d, (usableWidth - gap) / 2d);
        var leftText = header.LeftText;
        var rightText = header.RightText;
        var headerHeight = Math.Max(0.4d, header.HeightInCentimeters);
        // Fill the band so SSRS doesn't leave dead space below the textboxes.
        var textboxHeight = Math.Max(0.3d, headerHeight - 0.05d);
        var fontSize = $"{header.FontSizeInPoints.ToString("0.#", CultureInfo.InvariantCulture)}pt";

        return new XElement(Rdl + "PageHeader",
            new XElement(Rdl + "Height", ToCentimeters(headerHeight)),
            new XElement(Rdl + "PrintOnFirstPage", header.PrintOnFirstPage.ToString().ToLowerInvariant()),
            new XElement(Rdl + "PrintOnLastPage", header.PrintOnLastPage.ToString().ToLowerInvariant()),
            new XElement(Rdl + "ReportItems",
                BuildPositionedTextbox(
                    "sp2rdlHeaderLeft",
                    BuildTemplateExpression(leftText, model),
                    "0cm",
                    "0cm",
                    ToCentimeters(textboxWidth),
                    ToCentimeters(textboxHeight),
                    "Left",
                    model.BaseFontFamily,
                    fontSize),
                BuildPositionedTextbox(
                    "sp2rdlHeaderRight",
                    BuildTemplateExpression(rightText, model),
                    ToCentimeters(textboxWidth + gap),
                    "0cm",
                    ToCentimeters(textboxWidth),
                    ToCentimeters(textboxHeight),
                    "Right",
                    model.BaseFontFamily,
                    fontSize)),
            new XElement(Rdl + "Style",
                new XElement(Rdl + "Border",
                    new XElement(Rdl + "Style", "None"))));
    }

    private static XElement BuildPageFooter(PageFooterConfig footer, string baseFontFamily, PageSetupConfig pageSetup)
    {
        var usableWidth = GetUsablePageWidth(pageSetup);
        var gap = Math.Min(0.4d, usableWidth / 25d);
        var hasLogo = !string.IsNullOrWhiteSpace(footer.LogoImagePath) && File.Exists(footer.LogoImagePath);
        var logoWidth = hasLogo ? 0.8d : 0.0d;
        var leftTextLeft = logoWidth > 0 ? logoWidth + gap : 0.0d;
        var rightTextValue = BuildFooterRightValue(footer);
        var rightTextWidth = Math.Min(5.5d, usableWidth);
        var rightTextLeft = Math.Max(0.0d, usableWidth - rightTextWidth);
        var leftTextWidth = Math.Max(1.0d, rightTextLeft - leftTextLeft - gap);

        return new XElement(Rdl + "PageFooter",
            new XElement(Rdl + "Height", ToCentimeters(footer.HeightInCentimeters)),
            new XElement(Rdl + "PrintOnFirstPage", footer.PrintOnFirstPage.ToString().ToLowerInvariant()),
            new XElement(Rdl + "PrintOnLastPage", footer.PrintOnLastPage.ToString().ToLowerInvariant()),
            new XElement(Rdl + "ReportItems",
                hasLogo
                    ? BuildImage("sp2rdlFooterLogo", GetFooterLogoImageName(), "0cm", "0cm", ToCentimeters(logoWidth), "0.6cm")
                    : null,
                string.IsNullOrWhiteSpace(footer.LeftText)
                    ? null
                    : BuildPositionedTextbox(
                        "sp2rdlFooterLeft",
                        footer.LeftText,
                        ToCentimeters(leftTextLeft),
                        "0cm",
                        ToCentimeters(leftTextWidth),
                        "0.6cm",
                        "Left",
                        baseFontFamily),
                string.IsNullOrWhiteSpace(rightTextValue)
                    ? null
                    : BuildPositionedTextbox(
                        "sp2rdlFooterRight",
                        rightTextValue,
                        ToCentimeters(rightTextLeft),
                        "0cm",
                        ToCentimeters(rightTextWidth),
                        "0.6cm",
                        "Right",
                        baseFontFamily)),
            new XElement(Rdl + "Style",
                new XElement(Rdl + "Border",
                    new XElement(Rdl + "Style", "None"))));
    }

    private static string BuildFooterRightValue(PageFooterConfig footer)
    {
        if (string.IsNullOrWhiteSpace(footer.RightText))
        {
            return footer.ShowPageNumber ? "=\"Strana \" & Globals!PageNumber & \" od \" & Globals!TotalPages" : string.Empty;
        }

        var template = footer.RightText.Trim();
        if (template.Contains("{PageNo}", StringComparison.OrdinalIgnoreCase)
            || template.Contains("{PageCount}", StringComparison.OrdinalIgnoreCase))
        {
            return BuildFooterTemplateExpression(template);
        }

        return template;
    }

    private static string BuildFooterTemplateExpression(string template)
    {
        var expression = QuoteExpressionText(template);
        expression = expression.Replace("{PageNo}", "\" & Globals!PageNumber & \"", StringComparison.OrdinalIgnoreCase);
        expression = expression.Replace("{PageCount}", "\" & Globals!TotalPages & \"", StringComparison.OrdinalIgnoreCase);
        return "=" + expression;
    }

    private static string ResolveTemplateText(string template, ReportModel model)
    {
        if (string.IsNullOrWhiteSpace(template))
        {
            return string.Empty;
        }

        var values = model.ReportVariables.Items
            .Where(variable => variable.Enabled && !string.IsNullOrWhiteSpace(variable.Name))
            .GroupBy(variable => variable.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group =>
                {
                    var variable = group.First();
                    return variable.StaticValue
                        ?? variable.FallbackValue
                        ?? (string.Equals(variable.Name, "CompanyName", StringComparison.OrdinalIgnoreCase)
                            ? model.CompanyInfo.Text
                            : string.Empty);
                },
                StringComparer.OrdinalIgnoreCase);

        if (!values.ContainsKey("CompanyName"))
        {
            values["CompanyName"] = model.CompanyInfo.Text;
        }

        values["ReportTitle"] = string.IsNullOrWhiteSpace(model.ReportTitle.Text)
            ? model.Name
            : model.ReportTitle.Text;

        var resolved = template;
        foreach (var item in values)
        {
            resolved = resolved.Replace("{" + item.Key + "}", item.Value, StringComparison.OrdinalIgnoreCase);
        }

        return resolved;
    }

    private static string BuildTemplateExpression(string template, ReportModel model)
    {
        if (string.IsNullOrWhiteSpace(template))
        {
            return string.Empty;
        }

        var tokens = TokenizeTemplate(template, model).ToList();
        if (tokens.Count == 0)
        {
            return string.Empty;
        }

        return tokens.Count == 1 && !tokens[0].IsExpression
            ? tokens[0].Value
            : "=" + string.Join(" & ", tokens.Select(token => token.IsExpression ? token.Value : QuoteExpressionText(token.Value)));
    }

    private static IEnumerable<TemplateToken> TokenizeTemplate(string template, ReportModel model)
    {
        var variablesByName = model.ReportVariables.Items
            .Where(variable => variable.Enabled && !string.IsNullOrWhiteSpace(variable.Name))
            .GroupBy(variable => variable.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

        var index = 0;
        while (index < template.Length)
        {
            var start = template.IndexOf('{', index);
            if (start < 0)
            {
                yield return new TemplateToken(template[index..], false);
                yield break;
            }

            if (start > index)
            {
                yield return new TemplateToken(template[index..start], false);
            }

            var end = template.IndexOf('}', start + 1);
            if (end < 0)
            {
                yield return new TemplateToken(template[start..], false);
                yield break;
            }

            var name = template.Substring(start + 1, end - start - 1).Trim();
            yield return BuildPlaceholderToken(name, model, variablesByName);
            index = end + 1;
        }
    }

    private static TemplateToken BuildPlaceholderToken(string name, ReportModel model, IReadOnlyDictionary<string, ReportVariableConfig> variablesByName)
    {
        if (string.Equals(name, "ReportTitle", StringComparison.OrdinalIgnoreCase))
        {
            return new TemplateToken(string.IsNullOrWhiteSpace(model.ReportTitle.Text) ? model.Name : model.ReportTitle.Text, false);
        }

        if (!variablesByName.TryGetValue(name, out var variable))
        {
            return new TemplateToken("{" + name + "}", false);
        }

        if (!string.IsNullOrWhiteSpace(variable.StaticValue))
        {
            return new TemplateToken(variable.StaticValue!, false);
        }

        if (model.ReportVariables.DynamicSource.Enabled
            && !string.IsNullOrWhiteSpace(model.ReportVariables.DynamicSource.SqlExpression)
            && !string.IsNullOrWhiteSpace(variable.SourceColumnName))
        {
            var datasetName = string.IsNullOrWhiteSpace(model.ReportVariables.DynamicSource.DatasetName)
                ? "dsReportVariables"
                : model.ReportVariables.DynamicSource.DatasetName;
            var firstExpr = $"First(Fields!{variable.SourceColumnName.Trim()}.Value, {QuoteExpressionText(datasetName)})";
            var fallbackLiteral = QuoteExpressionText(variable.FallbackValue ?? string.Empty);
            return new TemplateToken(
                $"IIf(IsNothing({firstExpr}) OrElse Trim(CStr({firstExpr})) = \"\", {fallbackLiteral}, CStr({firstExpr}))",
                true);
        }

        return new TemplateToken(variable.FallbackValue ?? string.Empty, false);
    }

    private sealed record TemplateToken(string Value, bool IsExpression);

    private static HtmlTemplateForRdl PrepareHtmlTemplateForRdl(string template)
    {
        if (string.IsNullOrWhiteSpace(template))
        {
            return new HtmlTemplateForRdl(string.Empty, "Left");
        }

        var matches = HtmlParagraphRegex.Matches(template);
        if (matches.Count == 0)
        {
            return new HtmlTemplateForRdl(template, "Left");
        }

        var paragraphAlignments = matches
            .Select(match => ExtractParagraphTextAlign(match.Groups["attributes"].Value))
            .Where(align => !string.IsNullOrWhiteSpace(align))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var textAlign = paragraphAlignments.Count == 1 ? NormalizeTextAlign(paragraphAlignments[0]!) : "Left";

        var compactHtml = new StringBuilder();
        var currentIndex = 0;
        for (var i = 0; i < matches.Count; i++)
        {
            var match = matches[i];
            compactHtml.Append(template, currentIndex, match.Index - currentIndex);
            compactHtml.Append(match.Groups["content"].Value.Trim());
            if (i < matches.Count - 1)
            {
                compactHtml.Append("<br/>");
            }

            currentIndex = match.Index + match.Length;
        }

        compactHtml.Append(template, currentIndex, template.Length - currentIndex);
        return new HtmlTemplateForRdl(compactHtml.ToString(), textAlign);
    }

    private static string? ExtractParagraphTextAlign(string attributes)
    {
        var match = CssTextAlignRegex.Match(attributes);
        return match.Success ? match.Groups["align"].Value : null;
    }

    private static string NormalizeTextAlign(string textAlign)
        => textAlign.ToLowerInvariant() switch
        {
            "right" => "Right",
            "center" => "Center",
            _ => "Left"
        };

    private sealed record HtmlTemplateForRdl(string Html, string TextAlign);

    private static XElement BuildPositionedTextbox(
        string name,
        string value,
        string left,
        string top,
        string width,
        string height,
        string textAlign,
        string fontFamily,
        string fontSize = "9pt",
        string? fontWeight = null,
        string? hiddenExpression = null,
        string borderStyle = "None",
        string? backgroundColor = null,
        string? fontColor = null)
        => new(Rdl + "Textbox",
            new XAttribute("Name", name),
            new XElement(Rdl + "CanGrow", "true"),
            new XElement(Rdl + "KeepTogether", "true"),
            new XElement(Rdl + "Paragraphs",
                new XElement(Rdl + "Paragraph",
                    new XElement(Rdl + "TextRuns",
                        new XElement(Rdl + "TextRun",
                            new XElement(Rdl + "Value", value),
                            new XElement(Rdl + "Style",
                                new XElement(Rdl + "FontFamily", fontFamily),
                                new XElement(Rdl + "FontSize", fontSize),
                                string.IsNullOrWhiteSpace(fontColor)
                                    ? null
                                    : new XElement(Rdl + "Color", fontColor),
                                string.IsNullOrWhiteSpace(fontWeight)
                                    ? null
                                    : new XElement(Rdl + "FontWeight", fontWeight)))),
                    new XElement(Rdl + "Style",
                        new XElement(Rdl + "TextAlign", textAlign)))),
            string.IsNullOrWhiteSpace(hiddenExpression)
                ? null
                : new XElement(Rdl + "Visibility", new XElement(Rdl + "Hidden", hiddenExpression)),
            new XElement(Rdl + "Top", top),
            new XElement(Rdl + "Left", left),
            new XElement(Rdl + "Height", height),
            new XElement(Rdl + "Width", width),
            new XElement(Rdl + "Style",
                new XElement(Rdl + "Border",
                    new XElement(Rdl + "Style", borderStyle),
                    borderStyle.Equals("None", StringComparison.OrdinalIgnoreCase)
                        ? null
                        : new XElement(Rdl + "Color", ReportLineColor),
                    borderStyle.Equals("None", StringComparison.OrdinalIgnoreCase)
                        ? null
                        : new XElement(Rdl + "Width", ReportLineWidth)),
                string.IsNullOrWhiteSpace(backgroundColor)
                    ? null
                    : new XElement(Rdl + "BackgroundColor", backgroundColor),
                new XElement(Rdl + "PaddingLeft", "3pt"),
                new XElement(Rdl + "PaddingRight", "3pt"),
                new XElement(Rdl + "PaddingTop", "3pt"),
                new XElement(Rdl + "PaddingBottom", "3pt")));

    private static XElement BuildPositionedHtmlTextbox(
        string name,
        string html,
        string left,
        string top,
        string width,
        string height,
        string fontFamily,
        string textAlign = "Left",
        string verticalAlign = "Top",
        double paddingInPoints = 3.0d)
        => new(Rdl + "Textbox",
            new XAttribute("Name", name),
            new XElement(Rdl + "CanGrow", "true"),
            new XElement(Rdl + "KeepTogether", "true"),
            new XElement(Rdl + "Paragraphs",
                new XElement(Rdl + "Paragraph",
                    new XElement(Rdl + "TextRuns",
                        new XElement(Rdl + "TextRun",
                            new XElement(Rdl + "Value", string.IsNullOrWhiteSpace(html) ? string.Empty : html),
                            new XElement(Rdl + "MarkupType", "HTML"),
                            new XElement(Rdl + "Style",
                                new XElement(Rdl + "FontFamily", fontFamily),
                                new XElement(Rdl + "FontSize", "9pt")))),
                    new XElement(Rdl + "Style",
                        new XElement(Rdl + "TextAlign", textAlign)))),
            new XElement(Rdl + "Top", top),
            new XElement(Rdl + "Left", left),
            new XElement(Rdl + "Height", height),
            new XElement(Rdl + "Width", width),
            new XElement(Rdl + "Style",
                new XElement(Rdl + "Border",
                    new XElement(Rdl + "Style", "None")),
                new XElement(Rdl + "VerticalAlign", NormalizeVerticalAlign(verticalAlign)),
                new XElement(Rdl + "PaddingLeft", ToPoints(paddingInPoints)),
                new XElement(Rdl + "PaddingRight", ToPoints(paddingInPoints)),
                new XElement(Rdl + "PaddingTop", ToPoints(paddingInPoints)),
                new XElement(Rdl + "PaddingBottom", ToPoints(paddingInPoints))));

    private static XElement BuildImage(
        string name,
        string path,
        string left,
        string top,
        string width,
        string height)
        => new(Rdl + "Image",
            new XAttribute("Name", name),
            new XElement(Rdl + "Source", "Embedded"),
            new XElement(Rdl + "Value", path),
            new XElement(Rdl + "Sizing", "FitProportional"),
            new XElement(Rdl + "Top", top),
            new XElement(Rdl + "Left", left),
            new XElement(Rdl + "Height", height),
            new XElement(Rdl + "Width", width),
            new XElement(Rdl + "Style",
                new XElement(Rdl + "Border",
                    new XElement(Rdl + "Style", "None")),
                new XElement(Rdl + "TextAlign", "Center"),
                new XElement(Rdl + "VerticalAlign", "Middle")));

    private static XElement BuildLine(
        string name,
        string left,
        string top,
        string width,
        string height)
        => new(Rdl + "Line",
            new XAttribute("Name", name),
            new XElement(Rdl + "Top", top),
            new XElement(Rdl + "Left", left),
            new XElement(Rdl + "Height", height),
            new XElement(Rdl + "Width", width),
            new XElement(Rdl + "Style",
                new XElement(Rdl + "Border",
                    new XElement(Rdl + "Style", "Solid"),
                    new XElement(Rdl + "Color", ReportLineColor),
                    new XElement(Rdl + "Width", ReportLineWidth))));

    private static string GetFooterLogoImageName()
        => "sp2rdlFooterLogoImage";

    private static string GetMemorandumLogoImageName()
        => "sp2rdlMemorandumLogoImage";

    private static string GetImageMimeType(string path)
    {
        return Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".jpg" or ".jpeg" => "image/jpeg",
            ".bmp" => "image/bmp",
            ".gif" => "image/gif",
            _ => "image/png"
        };
    }

    private static void ReplaceTopLevelElement(XElement report, XElement replacement)
    {
        report.Element(replacement.Name)?.Remove();
        var replacementOrder = GetTopLevelElementOrder(replacement.Name);
        var insertBefore = report.Elements()
            .FirstOrDefault(element => GetTopLevelElementOrder(element.Name) > replacementOrder);

        if (insertBefore is null)
        {
            report.Add(replacement);
        }
        else
        {
            insertBefore.AddBeforeSelf(replacement);
        }
    }

    private static void ReplaceOptionalTopLevelElement(XElement report, XName name, XElement? replacement)
    {
        report.Element(name)?.Remove();
        if (replacement is not null)
        {
            ReplaceTopLevelElement(report, replacement);
        }
    }

    private static int GetTopLevelElementOrder(XName name)
    {
        if (name == Rdl + "Description") return 10;
        if (name == Rdl + "Author") return 20;
        if (name == Rdl + "AutoRefresh") return 30;
        if (name == Rdl + "DataSources") return 40;
        if (name == Rdl + "DataSets") return 50;
        if (name == Rdl + "EmbeddedImages") return 60;
        if (name == Rdl + "ReportSections") return 70;
        if (name == Rdl + "ReportParameters") return 80;
        if (name == Rdl + "ReportParametersLayout") return 90;
        if (name == Rdl + "Code") return 100;
        if (name.Namespace == Rd) return 1000;
        return 500;
    }

    private static string EnsureAtPrefix(string value)
        => value.StartsWith('@') ? value : $"@{value}";

    private static string MakeRdlName(string value)
    {
        var cleaned = new string(value.Where(char.IsLetterOrDigit).ToArray());
        return string.IsNullOrWhiteSpace(cleaned) ? "Item" : cleaned;
    }

    private static string QuoteExpressionText(string value)
        => "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    private static string EscapeExpressionText(string value)
        => value.Replace("\"", "\"\"", StringComparison.Ordinal);

    private static string? GetLocalizationExpression(IReadOnlyList<LocalizationLabel> labels, string key)
    {
        var expressionBody = GetLocalizationExpressionBody(labels, key);
        return expressionBody is null ? null : "=" + expressionBody;
    }

    private static string? GetLocalizationExpressionBody(IReadOnlyList<LocalizationLabel> labels, string key)
    {
        var label = labels.FirstOrDefault(item => string.Equals(item.Key, key, StringComparison.OrdinalIgnoreCase));
        return label is null
            ? null
            : $"First(Fields!{label.FieldName}.Value, {QuoteExpressionText("dsReportLabels")})";
    }

    private static string QuoteName(string value)
        => "[" + (string.IsNullOrWhiteSpace(value) ? "dbo" : value.Trim()).Replace("]", "]]", StringComparison.Ordinal) + "]";

    private static string EscapeSql(string value)
        => value.Replace("'", "''", StringComparison.Ordinal);

    private static List<string> ParseDependencyNames(string? dependencyList)
        => string.IsNullOrWhiteSpace(dependencyList)
            ? []
            : dependencyList
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(name => name.Trim().TrimStart('@'))
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

    private static string GetDatasetTimeout(ReportModel model, DatasetConfig dataset)
        => dataset.CommandKind == CommandKind.Text
            && !string.Equals(dataset.Name, model.MainDatasetName, StringComparison.OrdinalIgnoreCase)
                ? "8"
                : "30";

    private static string ToCentimeters(double value)
        => $"{value.ToString("0.###", CultureInfo.InvariantCulture)}cm";

    private static string ToPoints(double value)
        => $"{Math.Max(0.0d, value).ToString("0.###", CultureInfo.InvariantCulture)}pt";

    private static string NormalizeHexColor(string? value, string fallback)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        var trimmed = value.Trim();
        if (!trimmed.StartsWith("#", StringComparison.Ordinal))
        {
            trimmed = "#" + trimmed;
        }

        return Regex.IsMatch(trimmed, "^#[0-9A-Fa-f]{6}$") ? trimmed.ToUpperInvariant() : fallback;
    }

    private static string ShiftColor(string? baseColor, double amount, string fallback)
    {
        var normalized = NormalizeHexColor(baseColor, fallback);
        try
        {
            var red = Convert.ToInt32(normalized.Substring(1, 2), 16);
            var green = Convert.ToInt32(normalized.Substring(3, 2), 16);
            var blue = Convert.ToInt32(normalized.Substring(5, 2), 16);
            return $"#{ShiftChannel(red, amount):X2}{ShiftChannel(green, amount):X2}{ShiftChannel(blue, amount):X2}";
        }
        catch
        {
            return fallback;
        }
    }

    private static int ShiftChannel(int value, double amount)
        => amount >= 0
            ? Math.Clamp((int)Math.Round(value + (255 - value) * amount), 0, 255)
            : Math.Clamp((int)Math.Round(value * (1 + amount)), 0, 255);

    private static double GetUsablePageWidth(PageSetupConfig pageSetup)
        => Math.Max(1.0d, pageSetup.WidthInCentimeters - pageSetup.LeftMarginInCentimeters - pageSetup.RightMarginInCentimeters);

    private static string MapReportParameterType(ReportParameter parameter)
    {
        var normalized = NormalizeSqlType(parameter.SqlTypeName);
        return normalized switch
        {
            "bit" => "Boolean",
            "date" or "datetime" or "datetime2" or "smalldatetime" or "datetimeoffset" => "DateTime",
            "tinyint" or "smallint" or "int" or "bigint" => "Integer",
            "decimal" or "numeric" or "money" or "smallmoney" or "float" or "real" => "Float",
            _ => "String"
        };
    }

    /// <summary>
    /// Indicates whether a report parameter is rendered as an SSRS string parameter.
    /// </summary>
    private static bool IsStringReportParameter(ReportParameter parameter)
        => string.Equals(MapReportParameterType(parameter), "String", StringComparison.Ordinal);

    private static string MapClrTypeName(string sqlTypeName)
    {
        var normalized = NormalizeSqlType(sqlTypeName);
        return normalized switch
        {
            "bit" => "System.Boolean",
            "tinyint" => "System.Byte",
            "smallint" => "System.Int16",
            "int" => "System.Int32",
            "bigint" => "System.Int64",
            "decimal" or "numeric" or "money" or "smallmoney" => "System.Decimal",
            "float" => "System.Double",
            "real" => "System.Single",
            "date" or "datetime" or "datetime2" or "smalldatetime" or "datetimeoffset" => "System.DateTime",
            "uniqueidentifier" => "System.Guid",
            "binary" or "varbinary" or "image" => "System.Byte[]",
            _ => "System.String"
        };
    }

    private static string NormalizeSqlType(string sqlTypeName)
        => sqlTypeName.Split('(', 2)[0].Trim().ToLowerInvariant();
}
#pragma warning restore CS0618
