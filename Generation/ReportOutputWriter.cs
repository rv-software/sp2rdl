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

    public void Write(string reportPath, ReportModel model)
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
        SpRdlJsonStore.Save(GetModelPath(normalizedReportPath), model);
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
}
