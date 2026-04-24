using System.Xml.Linq;

namespace sp2rdlGenExtension.Generation;

internal static class RdlcConverter
{
    private static readonly XNamespace Rdl = "http://schemas.microsoft.com/sqlserver/reporting/2016/01/reportdefinition";
    private static readonly XNamespace Rd = "http://schemas.microsoft.com/SQLServer/reporting/reportdesigner";

    public static XDocument Convert(XDocument rdlDocument)
    {
        ArgumentNullException.ThrowIfNull(rdlDocument);

        var document = new XDocument(rdlDocument);
        document.Root?.Element(Rdl + "DataSources")?.Remove();

        foreach (var dataSet in document.Descendants(Rdl + "DataSet"))
        {
            dataSet.Element(Rdl + "Query")?.Remove();
            dataSet.AddFirst(
                new XElement(Rd + "DataSetInfo",
                    new XElement(Rd + "DataSetName", dataSet.Attribute("Name")?.Value ?? "DataSet"),
                    new XElement(Rd + "TableName", dataSet.Attribute("Name")?.Value ?? "DataSet")));
        }

        return document;
    }
}
