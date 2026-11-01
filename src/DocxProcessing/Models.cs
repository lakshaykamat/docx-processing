namespace DocxProcessing;

public record Issue(string Severity, string Description, string Location);
public record PropertyValue(string Property, string? Value, string Source);
public record RunInfo(string Text, string? StyleId, string? StyleName, List<PropertyValue> Formatting, string DirectXml);
public sealed class DocumentNode
{
    public string Id { get; set; } = "";
    public string Type { get; set; } = "";
    public string Text { get; set; } = "";
    public string Location { get; set; } = "";
    public string? StyleId { get; set; }
    public string? StyleName { get; set; }
    public int? HeadingLevel { get; set; }
    public bool Inferred { get; set; }
    public List<string> Inheritance { get; set; } = [];
    public List<PropertyValue> Formatting { get; set; } = [];
    public List<RunInfo> Runs { get; set; } = [];
    public string DirectXml { get; set; } = "";
    public List<DocumentNode> Children { get; set; } = [];
}
public sealed class StyleInfo
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Type { get; set; } = "";
    public string? BasedOn { get; set; }
    public string? Linked { get; set; }
    public bool IsDefault { get; set; }
    public int Usage { get; set; }
    public List<string> Chain { get; set; } = [];
    public string Xml { get; set; } = "";
}
public sealed class StyleSummaryInfo
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public bool IsDefault { get; set; }
    public int Usage { get; set; }
}
public sealed class StyleGroupInfo
{
    public string Type { get; set; } = "";
    public string Label { get; set; } = "";
    public List<StyleSummaryInfo> Styles { get; set; } = [];
}
public record ElementInfo(string Type, string Location, string Text, Dictionary<string, string> Details);
public sealed class AnalysisResult
{
    public string Filename { get; set; } = "";
    public long Size { get; set; }
    public Dictionary<string, int> Counts { get; set; } = [];
    public List<DocumentNode> Outline { get; set; } = [];
    public List<StyleInfo> Styles { get; set; } = [];
    public List<StyleGroupInfo> StyleGroups { get; set; } = [];
    public List<ElementInfo> Elements { get; set; } = [];
    public List<Issue> SchemaErrors { get; set; } = [];
    public List<Issue> StructuralWarnings { get; set; } = [];
    public List<string> Limitations { get; set; } = [];
}
