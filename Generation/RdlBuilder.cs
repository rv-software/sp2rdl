using System.Globalization;
using System.Reflection;
using System.Xml.Linq;
using sp2rdlGenExtension.Model;

namespace sp2rdlGenExtension.Generation;

internal sealed class RdlBuilder
{
    private static readonly XNamespace Rdl = "http://schemas.microsoft.com/sqlserver/reporting/2016/01/reportdefinition";
    private static readonly XNamespace Rd = "http://schemas.microsoft.com/SQLServer/reporting/reportdesigner";

    public XDocument Build(ReportModel model)
    {
        ArgumentNullException.ThrowIfNull(model);

        var document = LoadSkeleton();
        var report = document.Root ?? throw new InvalidOperationException("RDL skeleton is missing the Report root element.");

        ReplaceTopLevelElement(report, BuildDataSources(model));
        ReplaceTopLevelElement(report, BuildDataSets(model));
        ReplaceTopLevelElement(report, BuildReportParameters(model));
        ReplaceTopLevelElement(report, BuildReportParametersLayout(model));
        ApplyPageSetup(report, model.PageSetup);
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
        => new(Rdl + "DataSources",
            new XElement(Rdl + "DataSource",
                new XAttribute("Name", model.SharedDataSourceName),
                new XElement(Rdl + "DataSourceReference", model.SharedDataSourceName),
                new XElement(Rd + "SecurityType", "None"),
                new XElement(Rd + "DataSourceID", Guid.NewGuid().ToString())));

    private static XElement BuildDataSets(ReportModel model)
        => new(Rdl + "DataSets",
            model.Datasets.Select(dataset =>
                new XElement(Rdl + "DataSet",
                    new XAttribute("Name", dataset.Name),
                    BuildQuery(model, dataset),
                    BuildFields(dataset))));

    private static XElement BuildQuery(ReportModel model, DatasetConfig dataset)
        => new(Rdl + "Query",
            new XElement(Rdl + "DataSourceName", model.SharedDataSourceName),
            new XElement(Rdl + "CommandType", dataset.CommandKind == CommandKind.StoredProcedure ? "StoredProcedure" : "Text"),
            new XElement(Rdl + "CommandText", dataset.Command),
            BuildQueryParameters(dataset));

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
        => new(Rdl + "ReportParameters",
            model.Parameters.Select(BuildReportParameter));

    private static XElement BuildReportParameter(ReportParameter parameter)
    {
        var element = new XElement(Rdl + "ReportParameter",
            new XAttribute("Name", parameter.Name),
            new XElement(Rdl + "DataType", MapReportParameterType(parameter)),
            new XElement(Rdl + "Prompt", parameter.Prompt));

        if (parameter.Nullable)
        {
            element.Add(new XElement(Rdl + "Nullable", "true"));
        }

        if (parameter.AllowBlank)
        {
            element.Add(new XElement(Rdl + "AllowBlank", "true"));
        }

        if (parameter.MultiValue)
        {
            element.Add(new XElement(Rdl + "MultiValue", "true"));
        }

        if (parameter.Hidden)
        {
            element.Add(new XElement(Rdl + "Hidden", "true"));
        }

        if (!string.IsNullOrWhiteSpace(parameter.DefaultValueExpression))
        {
            element.Add(
                new XElement(Rdl + "DefaultValue",
                    new XElement(Rdl + "Values",
                        new XElement(Rdl + "Value", parameter.DefaultValueExpression))));
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
        var rows = Math.Max(1, model.Parameters.Select(parameter => parameter.LayoutRow).DefaultIfEmpty(0).Max() + 1);
        var columns = Math.Max(1, model.Parameters.Select(parameter => parameter.LayoutColumn).DefaultIfEmpty(0).Max() + 1);

        return new XElement(Rdl + "ReportParametersLayout",
            new XElement(Rdl + "GridLayoutDefinition",
                new XElement(Rdl + "NumberOfColumns", columns.ToString(CultureInfo.InvariantCulture)),
                new XElement(Rdl + "NumberOfRows", rows.ToString(CultureInfo.InvariantCulture)),
                model.Parameters.Select(parameter =>
                    new XElement(Rdl + "CellDefinitions",
                        new XElement(Rdl + "CellDefinition",
                            new XElement(Rdl + "ColumnIndex", parameter.LayoutColumn.ToString(CultureInfo.InvariantCulture)),
                            new XElement(Rdl + "RowIndex", parameter.LayoutRow.ToString(CultureInfo.InvariantCulture)),
                            new XElement(Rdl + "ParameterName", parameter.Name))))));
    }

    private static void ApplyHeaderFooter(XElement report, ReportModel model)
    {
        var page = report.Descendants(Rdl + "Page").FirstOrDefault()
            ?? throw new InvalidOperationException("RDL skeleton is missing Page element.");

        page.Element(Rdl + "PageHeader")?.Remove();
        page.Element(Rdl + "PageFooter")?.Remove();

        if (model.PageHeader.Enabled)
        {
            page.AddFirst(BuildPageHeader(model.PageHeader));
        }

        if (model.PageFooter.Enabled)
        {
            page.Add(BuildPageFooter(model.PageFooter));
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

    private static XElement BuildPageHeader(PageHeaderConfig header)
        => new(Rdl + "PageHeader",
            new XElement(Rdl + "Height", ToCentimeters(header.HeightInCentimeters)),
            new XElement(Rdl + "PrintOnFirstPage", header.PrintOnFirstPage.ToString().ToLowerInvariant()),
            new XElement(Rdl + "PrintOnLastPage", header.PrintOnLastPage.ToString().ToLowerInvariant()),
            new XElement(Rdl + "ReportItems",
                BuildTextbox("sp2rdlHeaderLeft", header.LeftText, "0cm", "0cm", "9cm", "0.6cm", "Left"),
                BuildTextbox("sp2rdlHeaderRight", header.RightText, "9.5cm", "0cm", "9cm", "0.6cm", "Right")),
            new XElement(Rdl + "Style",
                new XElement(Rdl + "Border",
                    new XElement(Rdl + "Style", "None"))));

    private static XElement BuildPageFooter(PageFooterConfig footer)
        => new(Rdl + "PageFooter",
            new XElement(Rdl + "Height", ToCentimeters(footer.HeightInCentimeters)),
            new XElement(Rdl + "PrintOnFirstPage", footer.PrintOnFirstPage.ToString().ToLowerInvariant()),
            new XElement(Rdl + "PrintOnLastPage", footer.PrintOnLastPage.ToString().ToLowerInvariant()),
            new XElement(Rdl + "ReportItems",
                footer.ShowPageNumber
                    ? BuildTextbox("sp2rdlFooterPageNumber", "=\"Page \" & Globals!PageNumber & \" of \" & Globals!TotalPages", "13cm", "0cm", "5.5cm", "0.6cm", "Right")
                    : null),
            new XElement(Rdl + "Style",
                new XElement(Rdl + "Border",
                    new XElement(Rdl + "Style", "None"))));

    private static XElement BuildTextbox(
        string name,
        string value,
        string left,
        string top,
        string width,
        string height,
        string textAlign)
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
                                new XElement(Rdl + "FontSize", "9pt")))),
                    new XElement(Rdl + "Style",
                        new XElement(Rdl + "TextAlign", textAlign)))),
            new XElement(Rdl + "Top", top),
            new XElement(Rdl + "Left", left),
            new XElement(Rdl + "Height", height),
            new XElement(Rdl + "Width", width),
            new XElement(Rdl + "Style",
                new XElement(Rdl + "Border",
                    new XElement(Rdl + "Style", "None"))));

    private static void ReplaceTopLevelElement(XElement report, XElement replacement)
    {
        report.Element(replacement.Name)?.Remove();
        var insertAfter = report.Elements()
            .LastOrDefault(element => element.Name == Rdl + "AutoRefresh" || element.Name == Rdl + "DataSources");

        if (insertAfter is null)
        {
            report.AddFirst(replacement);
        }
        else
        {
            insertAfter.AddAfterSelf(replacement);
        }
    }

    private static string EnsureAtPrefix(string value)
        => value.StartsWith('@') ? value : $"@{value}";

    private static string ToCentimeters(double value)
        => $"{value.ToString("0.###", CultureInfo.InvariantCulture)}cm";

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
