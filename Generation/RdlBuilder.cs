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
    private const string GroupSpacerHeight = "0.14cm";
    private const string TablixFontFamily = "Arial Narrow";
    private const string HeaderBackgroundColor = "#EDEDED";
    private const string GrandTotalBackgroundColor = "#CFCFCF";

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
                        new XElement(Rdl + "Value", value.Value),
                        new XElement(Rdl + "Label", string.IsNullOrWhiteSpace(value.Label) ? value.Value : value.Label)))));
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

        reportItems.Add(BuildTablix(dataset, usableWidth, currentTop, model.BaseFontFamily));
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
        var logoWidth = hasLogo ? Math.Min(2.4d, usableWidth * 0.25d) : 0.0d;
        var gap = hasLogo ? 0.35d : 0.0d;
        var separatorLeft = logoWidth + (gap / 2.0d);
        var textLeft = hasLogo ? logoWidth + gap : 0.0d;
        var textWidth = Math.Max(1.0d, usableWidth - textLeft);

        if (hasLogo)
        {
            reportItems.Add(BuildImage(
                "sp2rdlMemorandumLogo",
                GetMemorandumLogoImageName(),
                "0cm",
                "0.1cm",
                ToCentimeters(logoWidth),
                ToCentimeters(Math.Max(0.4d, height - 0.3d))));
        }

        if (hasLogo && model.Memorandum.ShowVerticalSeparator)
        {
            reportItems.Add(BuildLine(
                "sp2rdlMemorandumVerticalLine",
                ToCentimeters(separatorLeft),
                "0.1cm",
                "0cm",
                ToCentimeters(Math.Max(0.4d, height - 0.25d))));
        }

        reportItems.Add(BuildPositionedRichTextbox(
            "sp2rdlMemorandumText",
            GetMemorandumParagraphs(model),
            model,
            ToCentimeters(textLeft),
            "0cm",
            ToCentimeters(textWidth),
            ToCentimeters(Math.Max(0.4d, height - 0.15d)),
            model.BaseFontFamily));

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

    private static XElement BuildInlineReportSummary(ReportModel model, double usableWidth, double top)
    {
        var height = Math.Max(0.6d, model.ReportSummary.HeightInCentimeters);
        var reportItems = new XElement(Rdl + "ReportItems");
        var textTop = model.ReportSummary.ShowTopLine ? 0.15d : 0.0d;
        if (model.ReportSummary.ShowTopLine)
        {
            reportItems.Add(BuildLine("sp2rdlReportSummaryTopLine", "0cm", "0cm", ToCentimeters(usableWidth), "0cm"));
        }

        reportItems.Add(BuildPositionedTextbox(
            "sp2rdlReportSummaryText",
            ResolveTemplateText(model.ReportSummary.TextTemplate, model),
            "0cm",
            ToCentimeters(textTop),
            ToCentimeters(usableWidth),
            ToCentimeters(Math.Max(0.4d, height - textTop)),
            "Left",
            model.BaseFontFamily,
            "9pt"));

        return BuildBandRectangle("sp2rdlReportSummary", reportItems, usableWidth, top, height);
    }

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
        double usableWidth,
        double top,
        double height)
    {
        if (!enabled || layoutMode != ReportBandLayoutMode.Subreport)
        {
            return null;
        }

        var reportName = !string.IsNullOrWhiteSpace(subreportName)
            ? subreportName.Trim()
            : NormalizeSubreportReference(subreportPath);
        if (string.IsNullOrWhiteSpace(reportName))
        {
            return null;
        }

        return new XElement(Rdl + "Subreport",
            new XAttribute("Name", name),
            new XElement(Rdl + "ReportName", reportName),
            new XElement(Rdl + "Top", ToCentimeters(top)),
            new XElement(Rdl + "Left", "0cm"),
            new XElement(Rdl + "Height", ToCentimeters(Math.Max(0.4d, height))),
            new XElement(Rdl + "Width", ToCentimeters(usableWidth)),
            new XElement(Rdl + "Style",
                new XElement(Rdl + "Border",
                    new XElement(Rdl + "Style", "None"))));
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
            .Where(field => !string.IsNullOrWhiteSpace(field.AggregateFunction))
            .ToList();
        var rowCount = 1 + groups.Count * 2 + 1 + groups.Count + (aggregateFields.Count > 0 ? 1 : 0);
        return Math.Max(1.25d, rowCount * 0.6d);
    }

    private static XElement BuildTablix(DatasetConfig dataset, double usableWidth, double top, string baseFontFamily)
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
            .Where(field => !string.IsNullOrWhiteSpace(field.AggregateFunction))
            .ToList();
        var columnWidths = CalculateTablixColumnWidths(detailFields, usableWidth);
        var tablixRows = new List<XElement>
        {
            BuildTablixRow(detailFields, "Header", "0.65cm", field => field.Name, true)
        };

        foreach (var group in groups)
        {
            tablixRows.Add(BuildSpacerRow(detailFields, $"Group{group.Level}HeaderSpacer"));
            tablixRows.Add(BuildGroupHeaderRow(detailFields, group));
        }

        tablixRows.Add(BuildTablixRow(detailFields, "Detail", "0.6cm", field => $"=Fields!{field.Name}.Value", false));

        foreach (var group in groups.AsEnumerable().Reverse())
        {
            var style = GetGroupVisualStyle(group.Level);
            tablixRows.Add(BuildAggregateRow(detailFields, aggregateFields, $"sp2rdlGroup{group.Level}", BuildGroupSubtotalLabel(group), style.BackgroundColor, style.FontStyle));
        }

        if (aggregateFields.Count > 0)
        {
            tablixRows.Add(BuildAggregateRow(detailFields, aggregateFields, null, "Ukupno", GrandTotalBackgroundColor, fontStyle: null));
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
            new XElement(Rdl + "Width", ToCentimeters(usableWidth)),
            new XElement(Rdl + "Style",
                new XElement(Rdl + "Border",
                    new XElement(Rdl + "Style", "None"))));
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

    private static XElement BuildGroupHeaderRow(IReadOnlyList<DatasetField> columns, TablixGroup group)
    {
        var style = GetGroupVisualStyle(group.Level);
        var cellContents = new XElement(Rdl + "CellContents",
            BuildCellTextbox(
                $"sp2rdlGroup{group.Level}Header1",
                BuildGroupHeaderExpression(group),
                isHeader: true,
                format: null,
                textAlign: "Left",
                backgroundColor: style.BackgroundColor,
                fontWeight: "Bold",
                fontStyle: style.FontStyle,
                horizontalOnlyBorders: true,
                textAlignOverride: "Left"));

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

    private static string BuildGroupHeaderExpression(TablixGroup group)
        => "=" + string.Join(" & \"   \" & ", group.Fields.Select(field =>
            QuoteExpressionText(field.Name + ": ") + " & CStr(Fields!" + field.Name + ".Value)"));

    private static string BuildGroupSubtotalLabel(TablixGroup group)
        => "Podzbir: " + string.Join(", ", group.Fields.Select(field => field.Name));

    private static XElement BuildAggregateRow(
        IReadOnlyList<DatasetField> columns,
        IReadOnlyList<DatasetField> aggregateFields,
        string? scopeName,
        string label,
        string backgroundColor,
        string? fontStyle)
    {
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
                        fontStyle: fontStyle));
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
                        fontStyle))));
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
        var scope = string.IsNullOrWhiteSpace(scopeName) ? string.Empty : ", " + QuoteExpressionText(scopeName);
        return field.AggregateFunction switch
        {
            "Sum" => $"=Sum(Fields!{field.Name}.Value{scope})",
            "Avg" => $"=Avg(Fields!{field.Name}.Value{scope})",
            "Min" => $"=Min(Fields!{field.Name}.Value{scope})",
            "Max" => $"=Max(Fields!{field.Name}.Value{scope})",
            "Count" => $"=Count(Fields!{field.Name}.Value{scope})",
            "CountDistinct" => $"=CountDistinct(Fields!{field.Name}.Value{scope})",
            _ => string.Empty
        };
    }

    private static GroupVisualStyle GetGroupVisualStyle(int level)
        => level switch
        {
            1 => new("#D4D4D4", null),
            2 => new("#DEDEDE", "Italic"),
            3 => new("#E8E8E8", null),
            4 => new("#F2F2F2", "Italic"),
            _ => new("#F2F2F2", null)
        };

    private static IReadOnlyList<double> CalculateTablixColumnWidths(IReadOnlyList<DatasetField> fields, double usableWidth)
    {
        if (fields.Count == 0)
        {
            return [];
        }

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
        bool isHeader)
        => BuildCustomTablixRow(
            fields,
            rowName,
            height,
            (field, _) => valueFactory(field),
            isHeader,
            isHeader ? HeaderBackgroundColor : null,
            isHeader ? "Bold" : null);

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
        bool noBorders = false)
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
                            noBorders))))));

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
        string? textAlignOverride = null)
    {
        var textRunStyle = new XElement(Rdl + "Style",
            new XElement(Rdl + "FontFamily", TablixFontFamily),
            new XElement(Rdl + "FontSize", "9pt"));
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
                BuildCellBorders(horizontalOnlyBorders, noBorders),
                string.IsNullOrWhiteSpace(backgroundColor)
                    ? null
                    : new XElement(Rdl + "BackgroundColor", backgroundColor),
                new XElement(Rdl + "PaddingLeft", "2pt"),
                new XElement(Rdl + "PaddingRight", "2pt"),
                new XElement(Rdl + "PaddingTop", "2pt"),
                new XElement(Rdl + "PaddingBottom", "2pt")));
    }

    private static object[] BuildCellBorders(bool horizontalOnlyBorders, bool noBorders)
    {
        if (noBorders)
        {
            return
            [
                new XElement(Rdl + "Border",
                    new XElement(Rdl + "Style", "None"))
            ];
        }

        if (!horizontalOnlyBorders)
        {
            return
            [
                new XElement(Rdl + "Border",
                    new XElement(Rdl + "Style", "Solid"),
                    new XElement(Rdl + "Color", ReportLineColor),
                    new XElement(Rdl + "Width", ReportLineWidth))
            ];
        }

        return
        [
            new XElement(Rdl + "Border",
                new XElement(Rdl + "Style", "None")),
            new XElement(Rdl + "TopBorder",
                new XElement(Rdl + "Style", "Solid"),
                new XElement(Rdl + "Color", ReportLineColor),
                new XElement(Rdl + "Width", ReportLineWidth)),
            new XElement(Rdl + "BottomBorder",
                new XElement(Rdl + "Style", "Solid"),
                new XElement(Rdl + "Color", ReportLineColor),
                new XElement(Rdl + "Width", ReportLineWidth)),
            new XElement(Rdl + "LeftBorder",
                new XElement(Rdl + "Style", "Solid"),
                new XElement(Rdl + "Color", ReportLineColor),
                new XElement(Rdl + "Width", ReportLineWidth)),
            new XElement(Rdl + "RightBorder",
                new XElement(Rdl + "Style", "Solid"),
                new XElement(Rdl + "Color", ReportLineColor),
                new XElement(Rdl + "Width", ReportLineWidth))
        ];
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
            .Where(variable => !string.IsNullOrWhiteSpace(variable.Name))
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

        var resolved = template;
        foreach (var item in values)
        {
            resolved = resolved.Replace("{" + item.Key + "}", item.Value, StringComparison.OrdinalIgnoreCase);
        }

        return resolved;
    }

    private static List<RichTextParagraphConfig> GetMemorandumParagraphs(ReportModel model)
    {
        var paragraphs = model.Memorandum.RichTextParagraphs
            .Where(paragraph => !string.IsNullOrWhiteSpace(paragraph.Text))
            .ToList();
        if (paragraphs.Count > 0)
        {
            return paragraphs;
        }

        return string.IsNullOrWhiteSpace(model.Memorandum.TextTemplate)
            ? []
            : model.Memorandum.TextTemplate
                .Split(["\r\n", "\n"], StringSplitOptions.None)
                .Where(line => !string.IsNullOrWhiteSpace(line))
                .Select(line => new RichTextParagraphConfig { Text = line.Trim() })
                .ToList();
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

    private static XElement BuildPositionedRichTextbox(
        string name,
        IReadOnlyList<RichTextParagraphConfig> paragraphs,
        ReportModel model,
        string left,
        string top,
        string width,
        string height,
        string fontFamily)
        => new(Rdl + "Textbox",
            new XAttribute("Name", name),
            new XElement(Rdl + "CanGrow", "true"),
            new XElement(Rdl + "KeepTogether", "true"),
            new XElement(Rdl + "Paragraphs",
                paragraphs.Count == 0
                    ? [BuildRichParagraph(new RichTextParagraphConfig(), 0, model, fontFamily)]
                    : paragraphs.Select((paragraph, index) => BuildRichParagraph(paragraph, index, model, fontFamily))),
            new XElement(Rdl + "Top", top),
            new XElement(Rdl + "Left", left),
            new XElement(Rdl + "Height", height),
            new XElement(Rdl + "Width", width),
            new XElement(Rdl + "Style",
                new XElement(Rdl + "Border",
                    new XElement(Rdl + "Style", "None")),
                new XElement(Rdl + "PaddingLeft", "3pt"),
                new XElement(Rdl + "PaddingRight", "3pt"),
                new XElement(Rdl + "PaddingTop", "3pt"),
                new XElement(Rdl + "PaddingBottom", "3pt")));

    private static XElement BuildRichParagraph(RichTextParagraphConfig paragraph, int index, ReportModel model, string fontFamily)
    {
        var text = ResolveTemplateText(paragraph.Text, model);
        text = paragraph.ListStyle switch
        {
            "Bullet" => "- " + text,
            "Number" => (index + 1).ToString(CultureInfo.InvariantCulture) + ". " + text,
            _ => text
        };

        return new XElement(Rdl + "Paragraph",
            new XElement(Rdl + "TextRuns",
                new XElement(Rdl + "TextRun",
                    new XElement(Rdl + "Value", text),
                    new XElement(Rdl + "Style",
                        new XElement(Rdl + "FontFamily", fontFamily),
                        new XElement(Rdl + "FontSize", $"{Math.Max(1.0d, paragraph.FontSizeInPoints).ToString("0.#", CultureInfo.InvariantCulture)}pt"),
                        paragraph.Bold ? new XElement(Rdl + "FontWeight", "Bold") : null,
                        paragraph.Italic ? new XElement(Rdl + "FontStyle", "Italic") : null))),
            new XElement(Rdl + "Style",
                new XElement(Rdl + "TextAlign", NormalizeTextAlign(paragraph.TextAlign))));
    }

    private static string NormalizeTextAlign(string? value)
        => value switch
        {
            "Center" => "Center",
            "Right" => "Right",
            _ => "Left"
        };

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
