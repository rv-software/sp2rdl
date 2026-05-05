using System.IO;
using System.Xml.Linq;
using sp2rdlGenExtension.Model;
using sp2rdlGenExtension.Persistence;

namespace sp2rdlGenExtension.Generation;

internal sealed class ReportOutputWriter
{
    private readonly RdlBuilder rdlBuilder;

    public ReportOutputWriter(RdlBuilder rdlBuilder)
    {
        this.rdlBuilder = rdlBuilder;
    }

    public ReportOutputResult Write(string reportPath, ReportModel model)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reportPath);
        ArgumentNullException.ThrowIfNull(model);

        var normalizedReportPath = NormalizeReportPath(reportPath, model.OutputMode);
        Directory.CreateDirectory(Path.GetDirectoryName(normalizedReportPath) ?? ".");

        var rdlDocument = this.rdlBuilder.Build(model);
        var outputDocument = model.OutputMode == OutputMode.Rdlc
            ? RdlcConverter.Convert(rdlDocument)
            : rdlDocument;

        SaveXml(normalizedReportPath, outputDocument);
        var modelPath = GetModelPath(normalizedReportPath);
        SpRdlJsonStore.Save(modelPath, model);
        var seedPath = SaveLocalizationSeedSql(normalizedReportPath, model);
        return new ReportOutputResult(normalizedReportPath, modelPath, seedPath);
    }

    public static string GetModelPath(string reportPath)
        => Path.ChangeExtension(reportPath, ".sp2rdl.json");

    private static string NormalizeReportPath(string reportPath, OutputMode outputMode)
    {
        var expectedExtension = outputMode == OutputMode.Rdlc ? ".rdlc" : ".rdl";
        var extension = Path.GetExtension(reportPath);

        return string.IsNullOrWhiteSpace(extension)
            ? reportPath + expectedExtension
            : Path.ChangeExtension(reportPath, expectedExtension);
    }

    private static void SaveXml(string reportPath, XDocument document)
    {
        using var stream = File.Create(reportPath);
        document.Save(stream);
    }

    public static string GetLocalizationSeedPath(string reportPath)
        => Path.ChangeExtension(reportPath, ".translations.sql");

    private static string? SaveLocalizationSeedSql(string reportPath, ReportModel model)
    {
        var labels = LocalizationLabelCollector.Collect(model);
        var sql = LocalizationSeedSqlBuilder.Build(model, labels);
        if (string.IsNullOrWhiteSpace(sql))
        {
            return null;
        }

        var seedPath = GetLocalizationSeedPath(reportPath);
        File.WriteAllText(seedPath, sql);
        return seedPath;
    }
}
