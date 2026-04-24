using System.Data.SqlClient;
using System.IO;
using System.Text.Json;
using System.Xml.Linq;
using sp2rdlGenExtension.Model;

namespace sp2rdlGenExtension.Services;

#pragma warning disable CS0618 // Project decision: use System.Data.SqlClient for VSIX compatibility.
internal static class ConnectionStringDiscoveryService
{
    private static readonly string[] JsonFileNames =
    [
        "appsettings*.json",
        "local.settings.json"
    ];

    private static readonly string[] XmlFileNames =
    [
        "app.config",
        "web.config"
    ];

    internal static IReadOnlyList<DiscoveredConnectionString> Discover(string solutionDirectory)
    {
        if (string.IsNullOrWhiteSpace(solutionDirectory) || !Directory.Exists(solutionDirectory))
        {
            return [];
        }

        var discovered = new List<DiscoveredConnectionString>();
        foreach (var filePath in EnumerateCandidateFiles(solutionDirectory))
        {
            if (filePath.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            {
                discovered.AddRange(ReadJsonConnectionStrings(filePath));
            }
            else if (filePath.EndsWith(".config", StringComparison.OrdinalIgnoreCase))
            {
                discovered.AddRange(ReadXmlConnectionStrings(filePath));
            }
        }

        return discovered
            .Where(item => IsSqlServerConnectionString(item.Value))
            .GroupBy(item => item.Value, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static IEnumerable<string> EnumerateCandidateFiles(string solutionDirectory)
    {
        foreach (var pattern in JsonFileNames.Concat(XmlFileNames))
        {
            foreach (var filePath in Directory.EnumerateFiles(solutionDirectory, pattern, SearchOption.AllDirectories))
            {
                if (!IsIgnoredPath(filePath))
                {
                    yield return filePath;
                }
            }
        }
    }

    private static IEnumerable<DiscoveredConnectionString> ReadJsonConnectionStrings(string filePath)
    {
        using var stream = File.OpenRead(filePath);
        using var document = JsonDocument.Parse(stream, new JsonDocumentOptions { AllowTrailingCommas = true });
        return ReadJsonConnectionStrings(document.RootElement, filePath).ToList();
    }

    private static IEnumerable<DiscoveredConnectionString> ReadJsonConnectionStrings(JsonElement root, string filePath)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            yield break;
        }

        foreach (var property in root.EnumerateObject())
        {
            if (string.Equals(property.Name, "ConnectionStrings", StringComparison.OrdinalIgnoreCase)
                && property.Value.ValueKind == JsonValueKind.Object)
            {
                foreach (var connection in property.Value.EnumerateObject())
                {
                    if (connection.Value.ValueKind == JsonValueKind.String
                        && !string.IsNullOrWhiteSpace(connection.Value.GetString()))
                    {
                        yield return new DiscoveredConnectionString
                        {
                            Name = connection.Name,
                            Value = connection.Value.GetString()!,
                            SourceFile = filePath
                        };
                    }
                }
            }

            if (property.Value.ValueKind == JsonValueKind.String
                && property.Name.EndsWith("ConnectionString", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(property.Value.GetString()))
            {
                yield return new DiscoveredConnectionString
                {
                    Name = property.Name,
                    Value = property.Value.GetString()!,
                    SourceFile = filePath
                };
            }

            if (property.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
            {
                foreach (var nested in ReadNestedJsonConnectionStrings(property.Value, filePath))
                {
                    yield return nested;
                }
            }
        }
    }

    private static IEnumerable<DiscoveredConnectionString> ReadNestedJsonConnectionStrings(JsonElement element, string filePath)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var item in ReadJsonConnectionStrings(element, filePath))
            {
                yield return item;
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                foreach (var nested in ReadNestedJsonConnectionStrings(item, filePath))
                {
                    yield return nested;
                }
            }
        }
    }

    private static IEnumerable<DiscoveredConnectionString> ReadXmlConnectionStrings(string filePath)
    {
        var document = XDocument.Load(filePath);
        return document
            .Descendants()
            .Where(element => string.Equals(element.Name.LocalName, "add", StringComparison.OrdinalIgnoreCase))
            .Select(element => new
            {
                Name = element.Attribute("name")?.Value,
                Value = element.Attribute("connectionString")?.Value
            })
            .Where(item => !string.IsNullOrWhiteSpace(item.Name) && !string.IsNullOrWhiteSpace(item.Value))
            .Select(item => new DiscoveredConnectionString
            {
                Name = item.Name!,
                Value = item.Value!,
                SourceFile = filePath
            })
            .ToList();
    }

    private static bool IsIgnoredPath(string path)
    {
        var segments = path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return segments.Any(segment =>
            string.Equals(segment, "bin", StringComparison.OrdinalIgnoreCase)
            || string.Equals(segment, "obj", StringComparison.OrdinalIgnoreCase)
            || string.Equals(segment, ".vs", StringComparison.OrdinalIgnoreCase)
            || string.Equals(segment, ".git", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsSqlServerConnectionString(string value)
    {
        try
        {
            var builder = new SqlConnectionStringBuilder(value);
            return !string.IsNullOrWhiteSpace(builder.DataSource);
        }
        catch
        {
            return false;
        }
    }
}
#pragma warning restore CS0618
