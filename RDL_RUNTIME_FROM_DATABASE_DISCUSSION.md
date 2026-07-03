# Runtime ucitavanje RDL-a iz baze u ASP.NET Core aplikaciji

## Ideja

Moguce je cuvati RDL kao XML u bazi i u runtime-u ga ucitati u .NET aplikaciji bez lokalnog `.rdl` fajla na disku.

Aplikacija moze primiti npr. `reportId` i `datum`, procitati RDL XML iz baze, ucitati ga kao `Stream`, procitati dataset definicije iz RDL-a, izvrsiti SQL komande preko connection stringa iz konfiguracije i dodati rezultate u report engine kao `ReportDataSource`.

Ovaj dokument je koncept za diskusiju, ne gotova implementacija.

## Predlozeni tok

1. Web aplikacija primi zahtjev, npr. `reportId` i `datum`.
2. Iz baze se ucita RDL XML za taj `reportId`.
3. RDL XML se ucita u `LocalReport` preko `MemoryStream`.
4. Isti RDL XML se parsira kao XML dokument.
5. Iz RDL-a se procitaju svi `DataSet` elementi.
6. Za svaki dataset se procita:
   - `Name`
   - `Query/CommandText`
   - `Query/CommandType`
   - `Query/QueryParameters`
   - po potrebi `Fields`
7. Connection string se uzme iz konfiguracije aplikacije.
8. Za svaki dataset aplikacija izvrsi SQL query ili stored procedure.
9. Rezultat se napuni u `DataTable`.
10. `DataTable` se doda u report pod istim imenom kao dataset u RDL-u.
11. Report se renderuje, npr. kao PDF, Excel ili Word.

## Primjer glavnog servisa

```csharp
using Microsoft.Reporting.NETCore;
using Microsoft.Reporting.WinForms;
using System.Data;
using System.Text;

public sealed class RuntimeReportService
{
    private readonly IReportRepository reportRepository;
    private readonly IConfiguration configuration;

    public RuntimeReportService(
        IReportRepository reportRepository,
        IConfiguration configuration)
    {
        this.reportRepository = reportRepository;
        this.configuration = configuration;
    }

    public async Task<byte[]> RenderPdfAsync(int reportId, DateTime datum)
    {
        var rdlXml = await this.reportRepository.GetRdlXmlAsync(reportId);
        var connectionString = this.configuration.GetConnectionString("Reports");

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException("Missing Reports connection string.");
        }

        using var reportStream = new MemoryStream(Encoding.UTF8.GetBytes(rdlXml));

        var report = new LocalReport();
        report.LoadReportDefinition(reportStream);

        var datasets = RdlDatasetReader.ReadDatasets(rdlXml);
        var runtimeValues = new Dictionary<string, object?>
        {
            ["ReportId"] = reportId,
            ["Datum"] = datum
        };

        foreach (var dataset in datasets)
        {
            var table = await RdlDatasetExecutor.ExecuteDatasetAsync(
                connectionString,
                dataset,
                runtimeValues);

            report.DataSources.Add(new ReportDataSource(dataset.Name, table));
        }

        return report.Render("PDF");
    }
}
```

Napomena: u praksi treba koristiti namespace koji odgovara report biblioteci. Za ASP.NET Core cesto se koristi `Microsoft.Reporting.NETCore`, dok stariji desktop primjeri koriste `Microsoft.Reporting.WinForms`.

## Primjer controller metode

```csharp
[ApiController]
[Route("reports")]
public sealed class ReportsController : ControllerBase
{
    private readonly RuntimeReportService reportService;

    public ReportsController(RuntimeReportService reportService)
    {
        this.reportService = reportService;
    }

    [HttpGet("{reportId:int}/pdf")]
    public async Task<IActionResult> GetPdf(int reportId, DateTime datum)
    {
        var pdfBytes = await this.reportService.RenderPdfAsync(reportId, datum);

        return this.File(pdfBytes, "application/pdf", $"report-{reportId}.pdf");
    }
}
```

## Model dataset definicije

```csharp
public sealed record RdlDatasetDefinition(
    string Name,
    string CommandText,
    string? CommandType,
    IReadOnlyList<RdlQueryParameter> Parameters);

public sealed record RdlQueryParameter(
    string Name,
    string ValueExpression);
```

## Citanje datasetova iz RDL XML-a

```csharp
using System.Xml.Linq;

public static class RdlDatasetReader
{
    public static List<RdlDatasetDefinition> ReadDatasets(string rdlXml)
    {
        var document = XDocument.Parse(rdlXml);
        var ns = document.Root?.Name.Namespace
            ?? throw new InvalidOperationException("Invalid RDL XML.");

        return document
            .Descendants(ns + "DataSet")
            .Select(dataset =>
            {
                var query = dataset.Element(ns + "Query");

                var parameters = query?
                    .Element(ns + "QueryParameters")?
                    .Elements(ns + "QueryParameter")
                    .Select(parameter => new RdlQueryParameter(
                        parameter.Attribute("Name")?.Value ?? string.Empty,
                        parameter.Element(ns + "Value")?.Value ?? string.Empty))
                    .Where(parameter => !string.IsNullOrWhiteSpace(parameter.Name))
                    .ToList()
                    ?? new List<RdlQueryParameter>();

                return new RdlDatasetDefinition(
                    dataset.Attribute("Name")?.Value ?? string.Empty,
                    query?.Element(ns + "CommandText")?.Value ?? string.Empty,
                    query?.Element(ns + "CommandType")?.Value,
                    parameters);
            })
            .Where(dataset =>
                !string.IsNullOrWhiteSpace(dataset.Name)
                && !string.IsNullOrWhiteSpace(dataset.CommandText))
            .ToList();
    }
}
```

## Izvrsavanje dataset query-ja

```csharp
using Microsoft.Data.SqlClient;
using System.Data;

public static class RdlDatasetExecutor
{
    public static async Task<DataTable> ExecuteDatasetAsync(
        string connectionString,
        RdlDatasetDefinition dataset,
        IReadOnlyDictionary<string, object?> runtimeValues)
    {
        using var connection = new SqlConnection(connectionString);
        using var command = connection.CreateCommand();

        command.CommandText = dataset.CommandText;
        command.CommandType = string.Equals(
            dataset.CommandType,
            "StoredProcedure",
            StringComparison.OrdinalIgnoreCase)
                ? CommandType.StoredProcedure
                : CommandType.Text;

        foreach (var parameter in dataset.Parameters)
        {
            var value = ResolveParameterValue(parameter.ValueExpression, runtimeValues);
            command.Parameters.AddWithValue(parameter.Name, value ?? DBNull.Value);
        }

        var table = new DataTable(dataset.Name);

        await connection.OpenAsync();

        using var reader = await command.ExecuteReaderAsync();
        table.Load(reader);

        return table;
    }

    private static object? ResolveParameterValue(
        string expression,
        IReadOnlyDictionary<string, object?> runtimeValues)
    {
        const string prefix = "=Parameters!";
        const string suffix = ".Value";

        if (expression.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            && expression.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
        {
            var name = expression[prefix.Length..^suffix.Length];

            return runtimeValues.TryGetValue(name, out var value)
                ? value
                : null;
        }

        return expression;
    }
}
```

## Cuvanje RDL-a u bazi

RDL se moze cuvati kao tekst ili kao bytes:

- `nvarchar(max)` ili `xml`: jednostavno za pregled i administraciju u SQL Serveru.
- `varbinary(max)`: dobro ako se zeli sacuvati tacan byte sadrzaj fajla.

Ako se cuva kao tekst, treba paziti na encoding. Ako se cuva kao `varbinary(max)`, stream se moze napraviti direktno iz `byte[]`.

```csharp
byte[] rdlBytes = await repository.GetRdlBytesAsync(reportId);

using var reportStream = new MemoryStream(rdlBytes);

var report = new LocalReport();
report.LoadReportDefinition(reportStream);
```

## Vazne napomene

- Dataset ime u kodu mora odgovarati dataset imenu u RDL-u.
- Ako se RDL izvrsava kroz `LocalReport`, aplikacija najcesce sama puni podatke i dodaje `ReportDataSource`.
- Connection string iz RDL-a se moze ignorisati i zamijeniti connection stringom iz konfiguracije aplikacije.
- Subreporti se moraju posebno obraditi, najcesce kroz `SubreportProcessing` event.
- Embedded slike su jednostavnije od eksternih fajlova, jer nema lokalnog foldera pored RDL-a.
- Ako RDL sadrzi SQL tekst, aplikacija izvrsava SQL koji dolazi iz baze. Zbog toga RDL treba tretirati kao trusted administratorski sadrzaj, ne kao slobodan korisnicki upload.
- Za Linux hosting treba testirati report biblioteku, fontove i native dependencies.
- Za pravi interaktivni web viewer sa pagingom, drilldownom i parametrima treba razmotriti SSRS server ili komercijalni reporting viewer.

## Zakljucak

Tehnicki je izvodljivo napraviti genericki runtime mehanizam:

- RDL definicija se cita iz baze.
- RDL se ucitava iz `MemoryStream`.
- Datasetovi se citaju iz RDL XML-a.
- Query-ji se izvrsavaju preko connection stringa iz konfiguracije.
- Rezultati se dodaju u report engine po imenima datasetova.
- Web aplikacija vraca renderovani report, najcesce kao PDF ili Excel.

Najveci prakticni izazovi nisu samo ucitavanje RDL-a, nego sigurnost SQL-a iz RDL-a, mapiranje parametara, subreporti, slike i deployment okruzenje.
