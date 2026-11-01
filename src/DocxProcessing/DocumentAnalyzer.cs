using System.IO.Compression;
using System.Xml;
using System.Xml.Linq;
using System.Text.RegularExpressions;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Validation;

namespace DocxProcessing;

public sealed class DocumentAnalyzer
{
    internal static readonly XNamespace W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    static readonly XNamespace R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    static readonly XNamespace Rel = "http://schemas.openxmlformats.org/package/2006/relationships";
    readonly Dictionary<string, XElement> styles = new(StringComparer.Ordinal);
    readonly Dictionary<string, XDocument> parts = new(StringComparer.Ordinal);
    AnalysisResult result = new();
    XElement? defaults;
    int nodeId;
    static string? Val(XElement? e) => (string?)e?.Attribute(W + "val");
    static int StyleTypeOrder(string type) => type switch
    {
        "paragraph" => 0,
        "character" => 1,
        "table" => 2,
        "numbering" => 3,
        _ => 4
    };
    static string StyleTypeLabel(string type) => type switch
    {
        "paragraph" => "Paragraph styles",
        "character" => "Character styles",
        "table" => "Table styles",
        "numbering" => "Numbering styles",
        "unspecified" => "Other styles",
        _ => string.Concat(char.ToUpperInvariant(type[0]), type[1..], " styles")
    };
    static string Text(XElement e) => string.Concat(e.Descendants().Where(x => x.Name == W + "t" || x.Name == W + "delText" || x.Name.LocalName == "t" && x.Name.NamespaceName.Contains("math")).Select(x => x.Value));
    static string Location(string part, XElement e) => "/" + part + ":/" + string.Join("/", e.AncestorsAndSelf().Reverse().Select(x => x.Name.LocalName + "[" + (x.ElementsBeforeSelf(x.Name).Count() + 1) + "]"));
    void Warn(string message, string location) => result.StructuralWarnings.Add(new("Warning", message, location));

    public AnalysisResult Analyze(byte[] bytes, string filename)
    {
        styles.Clear(); parts.Clear(); nodeId = 0; defaults = null;
        result = new() { Filename = filename, Size = bytes.LongLength };
        using var stream = new MemoryStream(bytes, writable: false);
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true))
        {
            if (zip.Entries.Count > 5000 || zip.Entries.Sum(x => x.Length) > 100L * 1024 * 1024)
                throw new InvalidDataException("Package exceeds inspection limits.");
            foreach (var entry in zip.Entries.Where(x => x.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase) || x.FullName.EndsWith(".rels", StringComparison.OrdinalIgnoreCase)))
            {
                if (parts.ContainsKey(entry.FullName)) throw new InvalidDataException("Duplicate package part.");
                using var data = entry.Open();
                using var reader = XmlReader.Create(data, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 30_000_000 });
                parts.Add(entry.FullName, XDocument.Load(reader));
            }
            CheckRelationships(zip.Entries.Select(x => x.FullName).ToHashSet(StringComparer.Ordinal));
        }
        // Follow the package relationship instead of assuming a hard-coded main-part name.
        var office = parts.GetValueOrDefault("_rels/.rels")?.Root?.Elements(Rel + "Relationship")
            .FirstOrDefault(x => ((string?)x.Attribute("Type"))?.EndsWith("/officeDocument") == true);
        var mainPath = Resolve("", (string?)office?.Attribute("Target") ?? "word/document.xml");
        if (!parts.TryGetValue(mainPath, out var main) || main.Root?.Name != W + "document")
            throw new InvalidDataException("Missing Word document part.");
        var stylePath = RelatedPart(mainPath, "styles");
        var styleRoot = stylePath is null ? null : parts.GetValueOrDefault(stylePath)?.Root;
        defaults = styleRoot?.Element(W + "docDefaults");
        foreach (var style in styleRoot?.Elements(W + "style") ?? [])
        {
            var id = (string?)style.Attribute(W + "styleId");
            if (id is null) continue;
            if (!styles.TryAdd(id, style)) Warn($"Duplicate style ID '{id}'.", stylePath!);
        }
        foreach (var (id, s) in styles)
        {
            var chain = Chain(id);
            result.Styles.Add(new() { Id = id, Name = Val(s.Element(W + "name")) ?? id, Type = (string?)s.Attribute(W + "type") ?? "unspecified", BasedOn = Val(s.Element(W + "basedOn")), Linked = Val(s.Element(W + "link")), IsDefault = (string?)s.Attribute(W + "default") is "1" or "true" or "on", Chain = chain, Xml = s.ToString() });
        }
        var content = parts.Where(x => x.Value.Root is not null && new[] { "document", "hdr", "ftr", "footnotes", "endnotes", "comments" }.Contains(x.Value.Root.Name.LocalName)).ToList();
        foreach (var (path, doc) in content)
        {
            foreach (var reference in doc.Descendants().Where(x => x.Name == W + "pStyle" || x.Name == W + "rStyle" || x.Name == W + "tblStyle"))
            {
                var id = Val(reference);
                if (id is null || !styles.ContainsKey(id)) Warn($"Referenced style '{id ?? "(missing ID)"}' is not defined.", Location(path, reference));
            }
            foreach (var p in doc.Descendants(W + "p").Where(x => x.Element(W + "pPr")?.Element(W + "pStyle") is null)) UseStyle(DefaultStyle("paragraph"));
            foreach (var t in doc.Descendants(W + "tbl").Where(x => x.Element(W + "tblPr")?.Element(W + "tblStyle") is null)) UseStyle(DefaultStyle("table"));
            foreach (var reference in doc.Descendants().Where(x => x.Name == W + "pStyle" || x.Name == W + "rStyle" || x.Name == W + "tblStyle")) UseStyle(Val(reference));
            InspectElements(path, doc);
        }
        foreach (var s in result.Styles)
        {
            if (s.BasedOn is not null && !styles.ContainsKey(s.BasedOn)) Warn($"Style '{s.Id}' inherits missing style '{s.BasedOn}'.", stylePath!);
            if (s.Linked is not null && !styles.ContainsKey(s.Linked)) Warn($"Style '{s.Id}' links missing style '{s.Linked}'.", stylePath!);
            if (s.Chain.LastOrDefault()?.StartsWith("[cycle:") == true) Warn($"Style inheritance cycle at '{s.Id}'.", stylePath!);
        }
        var bookmarks = content.SelectMany(x => x.Value.Descendants(W + "bookmarkStart").Select(e => (x.Key, Element: e))).ToList();
        foreach (var group in bookmarks.GroupBy(x => (string?)x.Element.Attribute(W + "id")).Where(x => x.Count() > 1))
            Warn($"Duplicate bookmark identifier '{group.Key}'.", string.Join(", ", group.Select(x => Location(x.Key, x.Element))));
        var names = bookmarks.Select(x => (string?)x.Element.Attribute(W + "name")).ToHashSet();
        foreach (var (path, doc) in content)
            foreach (var h in doc.Descendants(W + "hyperlink").Where(x => x.Attribute(W + "anchor") is not null))
                if (!names.Contains((string?)h.Attribute(W + "anchor"))) Warn($"Internal hyperlink targets missing bookmark '{(string?)h.Attribute(W + "anchor")}'.", Location(path, h));
        var body = main.Root.Element(W + "body") ?? throw new InvalidDataException("Missing body.");
        var stack = new Stack<DocumentNode>();
        foreach (var child in body.Elements())
        {
            var node = BuildNode(mainPath, child);
            if (node.HeadingLevel is int level)
            {
                while (stack.Count > 0 && stack.Peek().HeadingLevel >= level) stack.Pop();
                if (stack.Count > 0) stack.Peek().Children.Add(node); else result.Outline.Add(node);
                stack.Push(node);
            }
            else if (stack.Count > 0) stack.Peek().Children.Add(node); else result.Outline.Add(node);
        }
        result.StyleGroups = result.Styles
            .GroupBy(style => string.IsNullOrWhiteSpace(style.Type) ? "unspecified" : style.Type.Trim().ToLowerInvariant())
            .OrderBy(group => StyleTypeOrder(group.Key))
            .ThenBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => new StyleGroupInfo
            {
                Type = group.Key,
                Label = StyleTypeLabel(group.Key),
                Styles = group.OrderBy(style => style.Name, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(style => style.Id, StringComparer.Ordinal)
                    .Select(style => new StyleSummaryInfo
                    {
                        Id = style.Id,
                        Name = style.Name,
                        IsDefault = style.IsDefault,
                        Usage = style.Usage
                    })
                    .ToList()
            })
            .ToList();
        int Count(string name) => content.Sum(x => x.Value.Descendants(W + name).Count());
        result.Counts = new() { ["Paragraphs"] = Count("p"), ["Styles defined"] = styles.Count, ["Styles used"] = result.Styles.Count(x => x.Usage > 0), ["Tables"] = Count("tbl"), ["Images"] = result.Elements.Count(x => x.Type == "Image"), ["Comments"] = Count("comment"), ["Bookmarks"] = bookmarks.Count, ["Tracked changes"] = result.Elements.Count(x => x.Type == "Tracked change") };
        // Validation uses the SDK in read-only mode. Structural inspection remains available
        // even when a broken relationship prevents the SDK from opening the package.
        try
        {
            using var sdkStream = new MemoryStream(bytes, writable: false);
            using var document = WordprocessingDocument.Open(sdkStream, false);
            var validator = new OpenXmlValidator(DocumentFormat.OpenXml.FileFormatVersions.Microsoft365) { MaxNumberOfErrors = 500 };
            result.SchemaErrors = validator.Validate(document).Select(e => new Issue("Error", e.Description, (e.Part?.Uri.ToString() ?? "package") + (e.Path?.XPath ?? ""))).ToList();
            if (result.SchemaErrors.Count == 500) result.Limitations.Add("Schema validation stopped at 500 errors; additional errors may exist.");
        }
        catch (Exception ex) when (ex is OpenXmlPackageException or InvalidDataException or IOException or ArgumentException or InvalidOperationException or XmlException)
        {
            result.SchemaErrors.Add(new("Error", "SDK validation could not complete because the package is damaged or unsupported.", "package"));
        }
        result.Limitations.AddRange([
            "Passing schema and structural checks does not guarantee identical rendering in Microsoft Word.",
            "Formatting resolves document defaults, paragraph style inheritance, character styles and direct properties. Theme fonts/colors remain symbolic; table/numbering style effects, conditional formatting and full Word layout are not calculated.",
            "Counts cover the main document, headers, footers, notes and comments. Style usage counts direct references plus implicit default paragraph/table applications, not descendants through inheritance.",
            "Main-body headings use explicit or inherited outline levels; heading names are only a labeled fallback. Other parts are listed in Elements. Fields, alternate content and unsupported structures are exposed as XML where possible.",
            "Inspection limits: 20 MB upload, 100 MB expanded package, 5,000 entries, 30 million XML characters per part. No uploads are stored."
        ]);
        return result;
    }

    void UseStyle(string? id) { var style = result.Styles.FirstOrDefault(x => x.Id == id); if (style is not null) style.Usage++; }
    string? DefaultStyle(string type) => styles.FirstOrDefault(x => (string?)x.Value.Attribute(W + "type") == type && (string?)x.Value.Attribute(W + "default") is "1" or "true" or "on").Key;
    string? StyleName(string? id) => id is not null && styles.TryGetValue(id, out var s) ? Val(s.Element(W + "name")) ?? id : id;
    List<string> Chain(string? id)
    {
        var chain = new List<string>(); var seen = new HashSet<string>();
        while (id is not null)
        {
            if (!seen.Add(id)) { chain.Add($"[cycle:{id}]"); break; }
            chain.Add(id);
            if (!styles.TryGetValue(id, out var s)) break;
            id = Val(s.Element(W + "basedOn"));
        }
        return chain;
    }
    List<(XElement? Element, string Source)> Layers(string? paragraphStyle, string group, XElement? direct, string? runStyle = null)
    {
        var layers = new List<(XElement?, string)> { (defaults?.Element(W + (group + "Default"))?.Element(W + group), "Document defaults") };
        foreach (var id in Chain(paragraphStyle).AsEnumerable().Reverse())
            if (styles.TryGetValue(id, out var s)) layers.Add((s.Element(W + group), $"Paragraph style: {StyleName(id)} ({id})"));
        if (runStyle is not null)
            foreach (var id in Chain(runStyle).AsEnumerable().Reverse())
                if (styles.TryGetValue(id, out var s)) layers.Add((s.Element(W + group), $"Character style: {StyleName(id)} ({id})"));
        layers.Add((direct, "Direct formatting")); return layers;
    }
    static readonly (string Label, string Element, string? Attribute, string Unit)[] RunProperties = [
        ("Font family (Latin)", "rFonts", "ascii", ""), ("Font family (high ANSI)", "rFonts", "hAnsi", ""), ("Font family (East Asia)", "rFonts", "eastAsia", ""), ("Font family (complex script)", "rFonts", "cs", ""),
        ("Theme font", "rFonts", "asciiTheme", " (theme reference)"), ("Font size", "sz", "val", " half-points"), ("Bold", "b", null, ""), ("Italic", "i", null, ""), ("Underline", "u", "val", ""), ("Color", "color", "val", ""), ("Theme color", "color", "themeColor", " (theme reference)")];
    static readonly (string Label, string Element, string? Attribute, string Unit)[] ParagraphProperties = [
        ("Alignment", "jc", "val", ""), ("Indent left", "ind", "left", " twips"), ("Indent start", "ind", "start", " twips"), ("Indent right", "ind", "right", " twips"), ("First line indent", "ind", "firstLine", " twips"), ("Hanging indent", "ind", "hanging", " twips"),
        ("Spacing before", "spacing", "before", " twips"), ("Spacing after", "spacing", "after", " twips"), ("Line spacing", "spacing", "line", " (raw OOXML units)"), ("Line spacing rule", "spacing", "lineRule", ""), ("Outline level", "outlineLvl", "val", " (0-based)"), ("List ID", "numPr/numId", "val", ""), ("List level", "numPr/ilvl", "val", " (0-based)")];
    List<PropertyValue> Formatting(List<(XElement? Element, string Source)> layers, bool run)
    {
        var values = new List<PropertyValue>();
        foreach (var spec in run ? RunProperties : ParagraphProperties)
        {
            string? value = null; string source = "Not explicitly specified";
            foreach (var (root, origin) in layers)
            {
                XElement? e = root;
                foreach (var segment in spec.Element.Split('/')) e = e?.Element(W + segment);
                if (e is null) continue;
                var raw = spec.Attribute is null ? Val(e) ?? "true" : (string?)e.Attribute(W + spec.Attribute);
                if (raw is null && spec.Element == "u") raw = "single";
                if (raw is null) continue;
                if (spec.Attribute is null)
                {
                    bool enabled = raw is not ("0" or "false" or "off");
                    // OOXML bold/italic are toggles in styles; direct values are absolute.
                    if (origin.Contains("style:") && enabled) value = value == "true" ? "false" : "true";
                    else if (!origin.Contains("style:")) value = enabled ? "true" : "false";
                    else continue;
                }
                else value = raw + spec.Unit;
                source = origin;
            }
            values.Add(new(spec.Label, value, source));
        }
        return values;
    }
    DocumentNode BuildNode(string path, XElement e)
    {
        var node = new DocumentNode { Id = "node-" + ++nodeId, Type = e.Name.LocalName, Text = Text(e), Location = Location(path, e), DirectXml = e.ToString() };
        if (e.Name == W + "p")
        {
            var pr = e.Element(W + "pPr");
            node.StyleId = Val(pr?.Element(W + "pStyle")) ?? DefaultStyle("paragraph"); node.StyleName = StyleName(node.StyleId);
            node.Inheritance = Chain(node.StyleId);
            node.Formatting = Formatting(Layers(node.StyleId, "pPr", pr), false);
            node.Formatting.AddRange(Formatting(Layers(node.StyleId, "rPr", null), true));
            var outline = node.Formatting.First(x => x.Property == "Outline level").Value?.Split(' ')[0];
            if (int.TryParse(outline, out var level) && level is >= 0 and <= 8) node.HeadingLevel = level + 1;
            else if (outline is null)
            {
                foreach (var id in node.Inheritance)
                {
                    var name = StyleName(id);
                    var match = HeadingLevelFromStyle(name) ?? HeadingLevelFromStyle(id);
                    if (match is null) continue;
                    node.HeadingLevel = match;
                    node.Inferred = true;
                    break;
                }
            }
            node.Type = node.HeadingLevel is not null ? "Heading" : (node.StyleName?.Equals("Title", StringComparison.OrdinalIgnoreCase) == true ? "Title" : node.StyleName?.Contains("caption", StringComparison.OrdinalIgnoreCase) == true ? "Caption" : "Paragraph");
            node.DirectXml = pr?.ToString() ?? "No direct paragraph properties.";
            foreach (var run in e.Descendants(W + "r").Where(r => r.Ancestors(W + "p").FirstOrDefault() == e))
            {
                var rp = run.Element(W + "rPr"); var rs = Val(rp?.Element(W + "rStyle"));
                node.Runs.Add(new(Text(run), rs, StyleName(rs), Formatting(Layers(node.StyleId, "rPr", rp, rs), true), rp?.ToString() ?? "No direct run properties."));
            }
        }
        else
        {
            if (e.Name == W + "tbl") { node.Type = "Table"; node.StyleId = Val(e.Element(W + "tblPr")?.Element(W + "tblStyle")) ?? DefaultStyle("table"); node.StyleName = StyleName(node.StyleId); node.Inheritance = Chain(node.StyleId); }
            foreach (var child in e.Elements().Where(x => !new[] { "tblPr", "tblGrid", "trPr", "tcPr" }.Contains(x.Name.LocalName)))
                if (child.Name == W + "p" || child.Name == W + "tbl" || child.Descendants(W + "p").Any()) node.Children.Add(BuildNode(path, child));
        }
        return node;
    }

    static int? HeadingLevelFromStyle(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var match = Regex.Match(name.Trim(), @"(?:heading|head)\s*[_-]?\s*([1-9])$", RegexOptions.IgnoreCase);
        if (!match.Success) match = Regex.Match(name.Trim(), @"^h\s*[_-]?\s*([1-9])$", RegexOptions.IgnoreCase);
        return match.Success ? int.Parse(match.Groups[1].Value) : null;
    }

    void InspectElements(string path, XDocument doc)
    {
        if (doc.Root?.Name.LocalName is "hdr" or "ftr" or "footnotes" or "endnotes")
            result.Elements.Add(new(doc.Root.Name.LocalName, "/" + path, Text(doc.Root), []));
        foreach (var e in doc.Descendants())
        {
            var type = e.Name.Namespace == W ? e.Name.LocalName switch {
                "tbl" => "Table", "tr" => "Row", "tc" => "Cell", "bookmarkStart" => "Bookmark", "hyperlink" => "Hyperlink", "comment" => "Comment", "footnote" => "Footnote", "endnote" => "Endnote", "ins" or "del" or "moveFrom" or "moveTo" or "pPrChange" or "rPrChange" or "tblPrChange" or "trPrChange" or "tcPrChange" or "sectPrChange" or "numberingChange" or "tblGridChange" or "tblPrExChange" or "cellIns" or "cellDel" or "cellMerge" => "Tracked change", "sdt" => "Content control", "altChunk" => "Unsupported external content", _ => null
            } : e.Name.LocalName switch { "blip" or "imagedata" => "Image", "oMath" => "Equation", _ => null };
            if (type is null) continue;
            var details = e.Attributes().Where(a => !a.IsNamespaceDeclaration).ToDictionary(a => a.Name.ToString(), a => a.Value);
            details["OOXML element"] = e.Name.ToString();
            if (type == "Table") { details["Rows"] = e.Elements(W + "tr").Count().ToString(); details["Cells"] = e.Descendants(W + "tc").Count().ToString(); }
            if (type == "Row") details["Cells"] = e.Elements(W + "tc").Count().ToString();
            if (type == "Cell")
            {
                var cp = e.Element(W + "tcPr");
                details["Grid span"] = Val(cp?.Element(W + "gridSpan")) ?? "1";
                details["Vertical merge"] = cp?.Element(W + "vMerge") is XElement vm ? Val(vm) ?? "continue" : "none";
                details["Horizontal merge (legacy)"] = cp?.Element(W + "hMerge") is XElement hm ? Val(hm) ?? "continue" : "none";
            }
            if (type == "Image" || type == "Hyperlink")
            {
                var id = (string?)e.Attribute(R + "embed") ?? (string?)e.Attribute(R + "link") ?? (string?)e.Attribute(R + "id");
                var rel = Relationships(path).FirstOrDefault(x => (string?)x.Attribute("Id") == id);
                if (rel is not null) { details["Target"] = (string?)rel.Attribute("Target") ?? ""; details["Target mode"] = (string?)rel.Attribute("TargetMode") ?? "Internal"; }
            }
            if (type == "Content control") details["Properties"] = e.Element(W + "sdtPr")?.ToString() ?? "unspecified";
            result.Elements.Add(new(type, Location(path, e), Text(e), details));
        }
    }
    static string RelPath(string part) => part.Length == 0 ? "_rels/.rels" : (part.Contains('/') ? part[..(part.LastIndexOf('/') + 1)] : "") + "_rels/" + part[(part.LastIndexOf('/') + 1)..] + ".rels";
    IEnumerable<XElement> Relationships(string part) => parts.GetValueOrDefault(RelPath(part))?.Root?.Elements(Rel + "Relationship") ?? [];
    string? RelatedPart(string part, string kind)
    {
        var relationship = Relationships(part).FirstOrDefault(x => ((string?)x.Attribute("Type"))?.EndsWith("/" + kind) == true && (string?)x.Attribute("TargetMode") != "External");
        return relationship?.Attribute("Target") is XAttribute target ? Resolve(part, target.Value) : null;
    }
    static string Resolve(string source, string target)
    {
        var baseUri = new Uri("http://package/" + source);
        return Uri.UnescapeDataString(new Uri(baseUri, target).AbsolutePath.TrimStart('/'));
    }
    void CheckRelationships(HashSet<string> entries)
    {
        foreach (var (path, doc) in parts.Where(x => x.Key.EndsWith(".rels", StringComparison.OrdinalIgnoreCase)))
        {
            var source = path == "_rels/.rels" ? "" : path.Replace("_rels/", "")[..^5];
            var ids = new HashSet<string>();
            foreach (var rel in doc.Root?.Elements(Rel + "Relationship") ?? [])
            {
                var id = (string?)rel.Attribute("Id") ?? "";
                if (!ids.Add(id)) Warn($"Duplicate relationship ID '{id}'.", path);
                if ((string?)rel.Attribute("TargetMode") == "External") continue;
                var target = (string?)rel.Attribute("Target");
                try { if (target is null || !entries.Contains(Resolve(source, target))) Warn($"Relationship '{id}' targets missing part '{target}'.", path); }
                catch (UriFormatException) { Warn($"Relationship '{id}' has an invalid target.", path); }
            }
        }
        foreach (var (path, doc) in parts.Where(x => !x.Key.EndsWith(".rels", StringComparison.OrdinalIgnoreCase)))
        {
            var ids = Relationships(path).Select(x => (string?)x.Attribute("Id")).ToHashSet();
            foreach (var e in doc.Descendants())
                foreach (var a in e.Attributes().Where(x => x.Name.Namespace == R && x.Name.LocalName is "id" or "embed" or "link"))
                    if (!ids.Contains(a.Value)) Warn($"Missing relationship '{a.Value}'.", Location(path, e));
        }
    }
}
