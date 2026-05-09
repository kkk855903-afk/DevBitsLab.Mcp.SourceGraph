namespace DevBitsLab.Mcp.SourceGraph.Indexing.Xaml;

/// <summary>
/// One <c>x:Key</c>-bearing resource discovered during the project's resource-cascade walk. Carries
/// enough position info that the indexer can emit a resolved <c>uses-resource</c> edge pointing
/// back at the declaration site.
/// </summary>
public sealed class ResourceDefinition
{
    public ResourceDefinition(string key, string filePath, int line, int column, string elementName)
    {
        Key = key;
        FilePath = filePath;
        Line = line;
        Column = column;
        ElementName = elementName;
    }

    /// <summary>The resource <c>x:Key</c> as written.</summary>
    public string Key { get; }

    /// <summary>Absolute path to the file the resource was declared in.</summary>
    public string FilePath { get; }

    /// <summary>1-based line of the declaring element.</summary>
    public int Line { get; }

    /// <summary>1-based column of the declaring element.</summary>
    public int Column { get; }

    /// <summary>Local name of the element that carries the <c>x:Key</c> (e.g. <c>SolidColorBrush</c>, <c>Style</c>).</summary>
    public string ElementName { get; }
}
