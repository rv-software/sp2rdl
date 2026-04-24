using System.IO;

namespace sp2rdlGenExtension.Model;

internal sealed class DiscoveredConnectionString
{
    public required string Name { get; init; }

    public required string Value { get; init; }

    public required string SourceFile { get; init; }

    public string DisplayName => $"{Name} ({Path.GetFileName(SourceFile)})";
}
