using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using sp2rdlGenExtension.Model;

namespace sp2rdlGenExtension.Persistence;

internal static class SpRdlJsonStore
{
    private static readonly JsonSerializerOptions Options = CreateOptions();

    public static void Save(string path, ReportModel model)
    {
        using var stream = File.Create(path);
        JsonSerializer.Serialize(stream, model, Options);
    }

    public static ReportModel Load(string path)
    {
        using var stream = File.OpenRead(path);
        return JsonSerializer.Deserialize<ReportModel>(stream, Options)
            ?? throw new InvalidOperationException($"Could not deserialize report model from '{path}'.");
    }

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }
}
