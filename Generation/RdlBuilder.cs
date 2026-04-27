using System.Globalization;
using System.Data.SqlClient;
using System.IO;
using System.Reflection;
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
    private const string TablixFontFamily = "Arial Narrow";
    private const string HeaderBackgroundColor = "#EDEDED";

    public XDocument Build(ReportModel model)
    {
        ArgumentNullException.ThrowIfNull(model);

        var document = LoadSkeleton();
        var report = document.Root ?? throw new InvalidOperationException("RDL skeleton is missing the Report root element.");
        ValidateParameterDependencies(model);

        ReplaceTopLevelElement(report, BuildDataSources(model));
        ReplaceTopLevelElement(report, BuildDataSets(model));
        var embeddedImages = BuildEmbeddedImages(model);
        if (embeddedImages is not null)
        {
            ReplaceTopLevelElement(report, embeddedImages);
        }
        ReplaceTopLevelElement(report, BuildReportParameters(model));
        ReplaceTopLevelElement(report, BuildReportParametersLayout(model));
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

    private static XElement BuildDataSets(ReportModel model)
        => new(Rdl + "DataSets",
            model.Datasets.Select(dataset =>
                new XElement(Rdl + "DataSet",
                    new XAttribute("Name", dataset.Name),
                    BuildQuery(model, dataset),
                    BuildFields(dataset))));

    private static XElement? BuildEmbeddedImages(ReportModel model)
    {
        var footerLogoPath = model.PageFooter.LogoImagePath;

        if (string.IsNullOrWhiteSpace(footerLogoPath) || !File.Exists(footerLogoPath))
        {
            return null;
        }

        return new XElement(Rdl + "EmbeddedImages",
            new XElement(Rdl + "EmbeddedImage",
            new XAttribute("Name", GetFooterLogoImageName()),
            new XElement(Rdl + "MIMEType", GetImageMimeType(footerLogoPath)),
            new XElement(Rdl + "ImageData", Convert.ToBase64String(File.ReadAllBytes(footerLogoPath)))));
    }

    private static XElement BuildQuery(ReportModel model, DatasetConfig dataset)
        => new(Rdl + "Query",
            new XElement(Rdl + "DataSourceName", model.SharedDataSourceName),
            new XElement(Rdl + "CommandType", dataset.CommandKind == CommandKind.StoredProcedure ? "StoredProcedure" : "Text"),
            new XElement(Rdl + "CommandText", dataset.Command),
            BuildQueryParameters(dataset),
            new XElement(Rdl + "Timeout", GetDatasetTimeout(model, dataset)));

    private static XElement? BuildQueryParameters(DatasetConfig dataset)
    {
        if (dataset.ParameterBindings.Count == 0)
        {
            return null;
        }

        return new XElement(Rdl + "QueryParameters",
            dataset.ParameterBindings.Select(binding =>
                new XElement(Rdl + "QueryParameter",
                    new XAttribute("Name", EnsureAtPrefix(binding.DatasetParameterName)),
                    new XElement(Rdl + "Value", $"=Parameters!{binding.ReportParameterName}.Value"))));
    }

    private static XElement BuildFields(DatasetConfig dataset)
        => new(Rdl + "Fields",
            dataset.Fields.Select(field =>
                new XElement(Rdl + "Field",
                    new XAttribute("Name", field.Name),
                    new XElement(Rdl + "DataField", field.Name),
                    new XElement(Rd + "TypeName", MapClrTypeName(field.SqlTypeName)))));

    private static XElement BuildReportParameters(ReportModel model)
    {
        var parametersUsedInQueries = model.Datasets
            .SelectMany(dataset => dataset.ParameterBindings)
            .Select(binding => binding.ReportParameterName.TrimStart('@'))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return new XElement(Rdl + "ReportParameters",
            model.Parameters
                .OrderBy(parameter => parameter.OrdinalNumber <= 0 ? int.MaxValue : parameter.OrdinalNumber)
                .ThenBy(parameter => parameter.Name, StringComparer.OrdinalIgnoreCase)
                .Select(parameter => BuildReportParameter(
                    parameter,
                    parametersUsedInQueries.Contains(parameter.Name.TrimStart('@')))));
    }

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
                        new XElement(Rdl + "Value", parameter.DefaultValueExpression))));
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
                        new XElement(Rdl + "Value", value),
                        new XElement(Rdl + "Label", value)))));
    }

    private static XElement BuildReportParametersLayout(ReportModel model)
    {
        var orderedParameters = model.Parameters
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

        if (model.PageHeader.Enabled)
        {
            page.AddFirst(BuildPageHeader(model.PageHeader, model.ReportTitle, model.CompanyInfo, model.BaseFontFamily, model.PageSetup));
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

        if (model.ReportTitle.Enabled && !string.IsNullOrWhiteSpace(model.ReportTitle.Text))
        {
            reportItems.Add(BuildPositionedTextbox(
                "sp2rdlReportTitle",
                model.ReportTitle.Text,
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

        reportItems.Add(BuildTablix(dataset, usableWidth, currentTop, model.BaseFontFamily));
        body.AddFirst(reportItems);
        body.SetElementValue(Rdl + "Height", ToCentimeters(Math.Max(2.0d, currentTop + 1.25d)));
    }

    private static XElement BuildTablix(DatasetConfig dataset, double usableWidth, double top, string baseFontFamily)
    {
        var fields = dataset.Fields
            .OrderBy(field => field.OrdinalPosition <= 0 ? int.MaxValue : field.OrdinalPosition)
            .ThenBy(field => field.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var columnWidth = usableWidth / Math.Max(fields.Count, 1);

        return new XElement(Rdl + "Tablix",
            new XAttribute("Name", "TablixMain"),
            new XElement(Rdl + "TablixBody",
                new XElement(Rdl + "TablixColumns",
                    fields.Select(_ => new XElement(Rdl + "TablixColumn",
                        new XElement(Rdl + "Width", ToCentimeters(columnWidth))))),
                new XElement(Rdl + "TablixRows",
                    BuildTablixRow(fields, "Header", "0.65cm", field => field.Name, true),
                    BuildTablixRow(fields, "Detail", "0.6cm", field => $"=Fields!{field.Name}.Value", false))),
            new XElement(Rdl + "TablixColumnHierarchy",
                new XElement(Rdl + "TablixMembers", fields.Select(_ => new XElement(Rdl + "TablixMember")))),
            new XElement(Rdl + "TablixRowHierarchy",
                new XElement(Rdl + "TablixMembers",
                    new XElement(Rdl + "TablixMember",
                        new XElement(Rdl + "KeepWithGroup", "After"),
                        new XElement(Rdl + "RepeatOnNewPage", "true")),
                    new XElement(Rdl + "TablixMember",
                        new XElement(Rdl + "Group", new XAttribute("Name", "Details"))))),
            new XElement(Rdl + "DataSetName", dataset.Name),
            new XElement(Rdl + "Top", ToCentimeters(top)),
            new XElement(Rdl + "Left", "0cm"),
            new XElement(Rdl + "Height", "1.25cm"),
            new XElement(Rdl + "Width", ToCentimeters(usableWidth)),
            new XElement(Rdl + "Style",
                new XElement(Rdl + "Border",
                    new XElement(Rdl + "Style", "None"))));
    }

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
        bool isHeader)
        => new(Rdl + "TablixRow",
            new XElement(Rdl + "Height", height),
            new XElement(Rdl + "TablixCells",
                fields.Select((field, index) => new XElement(Rdl + "TablixCell",
                    new XElement(Rdl + "CellContents",
                        BuildCellTextbox(
                            $"sp2rdl{rowName}{index + 1}",
                            valueFactory(field),
                            isHeader,
                            field.Format,
                            GetFieldTextAlign(field)))))));

    private static XElement BuildCellTextbox(
        string name,
        string value,
        bool isHeader,
        string? format,
        string textAlign)
    {
        var textRunStyle = new XElement(Rdl + "Style",
            new XElement(Rdl + "FontFamily", TablixFontFamily),
            new XElement(Rdl + "FontSize", "9pt"));
        if (isHeader)
        {
            textRunStyle.Add(new XElement(Rdl + "FontWeight", "Bold"));
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
                        new XElement(Rdl + "TextAlign", isHeader ? "Center" : textAlign)))),
            new XElement(Rdl + "Style",
                new XElement(Rdl + "Border",
                    new XElement(Rdl + "Style", "Solid"),
                    new XElement(Rdl + "Color", ReportLineColor),
                    new XElement(Rdl + "Width", ReportLineWidth)),
                isHeader
                    ? new XElement(Rdl + "BackgroundColor", HeaderBackgroundColor)
                    : null,
                new XElement(Rdl + "PaddingLeft", "2pt"),
                new XElement(Rdl + "PaddingRight", "2pt"),
                new XElement(Rdl + "PaddingTop", "2pt"),
                new XElement(Rdl + "PaddingBottom", "2pt")));
    }

    private static string GetFieldTextAlign(DatasetField field)
    {
        var normalized = NormalizeSqlType(field.SqlTypeName);
        return normalized switch
        {
            "bit" or "tinyint" or "smallint" or "int" or "bigint" or "decimal" or "numeric" or "money" or "smallmoney" or "float" or "real" => "Right",
            "date" or "datetime" or "datetime2" or "smalldatetime" or "datetimeoffset" or "time" => "Center",
            _ => "Left"
        };
    }

    private static XElement BuildPageHeader(PageHeaderConfig header, ReportTitleConfig title, CompanyInfoConfig companyInfo, string baseFontFamily, PageSetupConfig pageSetup)
    {
        var usableWidth = GetUsablePageWidth(pageSetup);
        var gap = Math.Min(0.5d, usableWidth / 20d);
        var textboxWidth = Math.Max(1.0d, (usableWidth - gap) / 2d);
        var leftText = title.Enabled && title.ShowInPageHeaderAfterFirstPage && !string.IsNullOrWhiteSpace(title.Text)
            ? title.Text
            : header.LeftText;
        var rightText = !string.IsNullOrWhiteSpace(companyInfo.Text)
            ? companyInfo.Text
            : header.RightText;
        var printOnFirstPage = header.PrintOnFirstPage
            && !(title.Enabled && !string.IsNullOrWhiteSpace(title.Text));
        var hiddenExpression = title.Enabled && title.ShowInPageHeaderAfterFirstPage && !string.IsNullOrWhiteSpace(title.Text)
            ? "=Globals!PageNumber = 1"
            : null;

        return new XElement(Rdl + "PageHeader",
            new XElement(Rdl + "Height", ToCentimeters(header.HeightInCentimeters)),
            new XElement(Rdl + "PrintOnFirstPage", printOnFirstPage.ToString().ToLowerInvariant()),
            new XElement(Rdl + "PrintOnLastPage", header.PrintOnLastPage.ToString().ToLowerInvariant()),
            new XElement(Rdl + "ReportItems",
                BuildPositionedTextbox(
                    "sp2rdlHeaderLeft",
                    leftText,
                    "0cm",
                    "0cm",
                    ToCentimeters(textboxWidth),
                    "0.6cm",
                    "Left",
                    baseFontFamily,
                    hiddenExpression: hiddenExpression),
                BuildPositionedTextbox(
                    "sp2rdlHeaderRight",
                    rightText,
                    ToCentimeters(textboxWidth + gap),
                    "0cm",
                    ToCentimeters(textboxWidth),
                    "0.6cm",
                    "Right",
                    baseFontFamily)),
            new XElement(Rdl + "Style",
                new XElement(Rdl + "Border",
                    new XElement(Rdl + "Style", "None"))));
    }

    private static XElement BuildPageFooter(PageFooterConfig footer, string baseFontFamily, PageSetupConfig pageSetup)
    {
        var usableWidth = GetUsablePageWidth(pageSetup);
        var pageNumberWidth = Math.Min(5.5d, usableWidth);
        var pageNumberLeft = Math.Max(0.0d, usableWidth - pageNumberWidth);
        var gap = Math.Min(0.4d, usableWidth / 25d);
        var hasLogo = !string.IsNullOrWhiteSpace(footer.LogoImagePath) && File.Exists(footer.LogoImagePath);
        var logoWidth = hasLogo ? 0.8d : 0.0d;
        var leftTextLeft = logoWidth > 0 ? logoWidth + gap : 0.0d;
        var leftTextWidth = Math.Max(1.0d, pageNumberLeft - leftTextLeft - gap);

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
                footer.ShowPageNumber
                    ? BuildPositionedTextbox(
                        "sp2rdlFooterPageNumber",
                        "=\"Page \" & Globals!PageNumber & \" of \" & Globals!TotalPages",
                        ToCentimeters(pageNumberLeft),
                        "0cm",
                        ToCentimeters(pageNumberWidth),
                        "0.6cm",
                        "Right",
                        baseFontFamily)
                    : null),
            new XElement(Rdl + "Style",
                new XElement(Rdl + "Border",
                    new XElement(Rdl + "Style", "None"))));
    }

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
        string? backgroundColor = null)
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

    private static string GetFooterLogoImageName()
        => "sp2rdlFooterLogoImage";

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

    private static string QuoteExpressionText(string value)
        => "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

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
