using System.Globalization;
using DocxProcessing;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;

namespace DocxProcessing.Services;

internal sealed class DocxStyleApplier
{
    public StyleApplicationResult Apply(
        byte[] input,
        AnalysisResult analysis,
        IReadOnlyCollection<SuggestedStyleAssignment> assignments)
    {
        var nodesById = Flatten(analysis.Outline).ToDictionary(node => node.Id, StringComparer.Ordinal);
        using var output = new MemoryStream();
        output.Write(input);
        output.Position = 0;

        int updatedCount;
        using (var document = WordprocessingDocument.Open(output, true))
        {
            var root = document.MainDocumentPart?.Document;
            if (root is null)
                return StyleApplicationResult.Failed("This document has no editable main body.");

            updatedCount = 0;
            foreach (var assignment in assignments)
            {
                if (!nodesById.TryGetValue(assignment.NodeId, out var node)) continue;
                var target = ResolveElement(root, node.Location);
                if (node.Type == "Table" && target is Table table)
                {
                    var properties = table.GetFirstChild<TableProperties>();
                    if (properties is null)
                    {
                        properties = new TableProperties();
                        table.PrependChild(properties);
                    }
                    properties.TableStyle = new TableStyle { Val = assignment.StyleId };
                    updatedCount++;
                }
                else if ((node.Type is "Heading" or "Title" or "Caption" or "Paragraph") && target is Paragraph paragraph)
                {
                    paragraph.ParagraphProperties ??= new ParagraphProperties();
                    paragraph.ParagraphProperties.ParagraphStyleId = new ParagraphStyleId { Val = assignment.StyleId };
                    updatedCount++;
                }
            }

            if (updatedCount > 0) root.Save();
        }

        return StyleApplicationResult.Success(output.ToArray(), updatedCount);
    }

    static IEnumerable<DocumentNode> Flatten(IEnumerable<DocumentNode> nodes)
    {
        foreach (var node in nodes)
        {
            yield return node;
            foreach (var child in Flatten(node.Children)) yield return child;
        }
    }

    static OpenXmlElement? ResolveElement(OpenXmlElement root, string location)
    {
        var separator = location.IndexOf(":/", StringComparison.Ordinal);
        if (separator < 0) return null;

        var segments = location[(separator + 2)..].Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0) return null;

        var first = ParseSegment(segments[0]);
        if (first is null || first.Value.Name != root.LocalName || first.Value.Index != 1) return null;

        OpenXmlElement current = root;
        foreach (var segment in segments.Skip(1))
        {
            var parsed = ParseSegment(segment);
            if (parsed is null) return null;

            var next = current.ChildElements
                .Where(child => child.LocalName == parsed.Value.Name && child.NamespaceUri == DocumentAnalyzer.W.NamespaceName)
                .Skip(parsed.Value.Index - 1)
                .FirstOrDefault();
            if (next is null) return null;
            current = next;
        }

        return current;
    }

    static (string Name, int Index)? ParseSegment(string segment)
    {
        var bracket = segment.LastIndexOf('[');
        if (bracket <= 0 || !segment.EndsWith(']') ||
            !int.TryParse(
                segment.AsSpan(bracket + 1, segment.Length - bracket - 2),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var index) || index < 1)
        {
            return null;
        }

        return (segment[..bracket], index);
    }
}

internal sealed record StyleApplicationResult(byte[] Bytes, int UpdatedCount, string? Error)
{
    public static StyleApplicationResult Success(byte[] bytes, int count) => new(bytes, count, null);
    public static StyleApplicationResult Failed(string message) => new([], 0, message);
}
