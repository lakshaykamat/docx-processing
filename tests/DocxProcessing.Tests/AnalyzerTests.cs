using System.Security.Cryptography;
using Xunit;

namespace DocxProcessing.Tests;

public class AnalyzerTests
{
    static IEnumerable<DocumentNode> Flatten(IEnumerable<DocumentNode> nodes) => nodes.SelectMany(n => new[] { n }.Concat(Flatten(n.Children)));
    [Fact]
    public void ReadsHierarchyStylesMixedRunsAndMergedCellsWithoutChangingBytes()
    {
        var bytes = Fixtures.Representative(); var hash = SHA256.HashData(bytes);
        var result = new DocumentAnalyzer().Analyze(bytes, "representative.docx");
        Assert.Equal(hash, SHA256.HashData(bytes));
        Assert.Empty(result.SchemaErrors);
        Assert.Empty(result.StructuralWarnings);
        var heading = result.Outline.First();
        Assert.Equal("Custom", heading.StyleId); Assert.Equal("Custom section", heading.StyleName);
        Assert.Equal(1, heading.HeadingLevel); Assert.False(heading.Inferred);
        Assert.Equal(2, heading.Children.First().HeadingLevel);
        Assert.Equal(new[] { "Custom", "Base", "Normal" }, heading.Inheritance);
        var p = Flatten(result.Outline).Single(n => n.Text == "PlainMixed");
        Assert.Contains(p.Formatting, v => v.Property == "Alignment" && v.Value == "center" && v.Source == "Direct formatting");
        Assert.Contains(p.Formatting, v => v.Property == "Spacing after" && v.Value == "160 twips" && v.Source.Contains("Normal"));
        Assert.Contains(p.Runs[0].Formatting, v => v.Property == "Font family (Latin)" && v.Value == "Calibri" && v.Source == "Document defaults");
        Assert.Contains(p.Runs[1].Formatting, v => v.Property == "Italic" && v.Value == "true" && v.Source.Contains("Character style"));
        Assert.Contains(p.Runs[1].Formatting, v => v.Property == "Font size" && v.Value == "28 half-points" && v.Source == "Direct formatting");
        Assert.Contains(p.Runs[0].Formatting, v => v.Property == "Bold" && v.Value is null);
        Assert.Contains(result.Elements, e => e.Type == "Cell" && e.Details["Grid span"] == "2" && e.Details["Vertical merge"] == "restart");
        Assert.Equal(1, result.Counts["Tables"]);
        Assert.Equal(1, result.Styles.Single(s => s.Id == "Emphasis").Usage);
        Assert.Contains(result.Styles, s => s.Type == "numbering");
    }
    [Fact]
    public void ReportsSchemaAndStructuralIssuesSeparately()
    {
        var bytes = Fixtures.Create("""
            <w:p><w:pPr><w:pStyle w:val="Missing"/><w:jc w:val="impossible"/></w:pPr>
              <w:bookmarkStart w:id="7" w:name="a"/><w:bookmarkStart w:id="7" w:name="b"/>
              <w:hyperlink w:anchor="nowhere"><w:r><w:t>Broken</w:t></w:r></w:hyperlink>
              <w:hyperlink r:id="absent"><w:r><w:t>Missing relation</w:t></w:r></w:hyperlink>
            </w:p>
            """, Fixtures.Styles);
        var result = new DocumentAnalyzer().Analyze(bytes, "inconsistent.docx");
        Assert.NotEmpty(result.SchemaErrors);
        Assert.Contains(result.StructuralWarnings, x => x.Description.Contains("Referenced style 'Missing'"));
        Assert.Contains(result.StructuralWarnings, x => x.Description.Contains("Duplicate bookmark"));
        Assert.Contains(result.StructuralWarnings, x => x.Description.Contains("missing bookmark"));
        Assert.Contains(result.StructuralWarnings, x => x.Description.Contains("Missing relationship 'absent'"));
    }
    [Fact]
    public void MissingRelationshipPartIsReportedWithoutLosingInspection()
    {
        var result = new DocumentAnalyzer().Analyze(Fixtures.Create("<w:p/>", relationships: "<Relationship Id=\"rBad\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/image\" Target=\"media/missing.png\"/>"), "broken.docx");
        Assert.Contains(result.StructuralWarnings, x => x.Description.Contains("targets missing part"));
        Assert.NotEmpty(result.Outline);
    }
    [Fact]
    public void InferredHeadingsAndCyclesAreExplicitAndSafe()
    {
        var result = new DocumentAnalyzer().Analyze(Fixtures.Create("<w:p><w:pPr><w:pStyle w:val=\"A\"/></w:pPr><w:r><w:t>Inferred</w:t></w:r></w:p>", "<w:style w:type=\"paragraph\" w:styleId=\"A\"><w:name w:val=\"Heading 3\"/><w:basedOn w:val=\"B\"/></w:style><w:style w:type=\"paragraph\" w:styleId=\"B\"><w:name w:val=\"B\"/><w:basedOn w:val=\"A\"/></w:style>"), "cycle.docx");
        Assert.True(result.Outline[0].Inferred); Assert.Equal(3, result.Outline[0].HeadingLevel);
        Assert.Contains(result.StructuralWarnings, x => x.Description.Contains("inheritance cycle"));
    }
    [Fact]
    public void ExplicitBodyOutlineLevelSuppressesNameInference()
    {
        var result = new DocumentAnalyzer().Analyze(Fixtures.Create("<w:p><w:pPr><w:pStyle w:val=\"H\"/><w:outlineLvl w:val=\"9\"/></w:pPr></w:p>", "<w:style w:type=\"paragraph\" w:styleId=\"H\"><w:name w:val=\"Heading 2\"/></w:style>"), "body.docx");
        Assert.Null(result.Outline[0].HeadingLevel); Assert.False(result.Outline[0].Inferred);
    }
    [Fact]
    public void HandlesNotesCommentsChangesImagesAndContentControls()
    {
        const string w = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
        var bytes = Fixtures.Create("""
            <w:sdt><w:sdtPr><w:tag w:val="example"/></w:sdtPr><w:sdtContent><w:p><w:ins w:id="1" w:author="Tester"><w:r><w:t>Inserted</w:t></w:r></w:ins><w:del w:id="2" w:author="Tester"><w:r><w:delText>Deleted</w:delText></w:r></w:del></w:p></w:sdtContent></w:sdt>
            <w:p><w:r><w:drawing><a:blip xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main" r:embed="missingImage"/></w:drawing></w:r></w:p>
            """, extra: new() {
                ["word/comments.xml"] = $"<w:comments xmlns:w=\"{w}\"><w:comment w:id=\"0\" w:author=\"Tester\"><w:p><w:r><w:t>Comment</w:t></w:r></w:p></w:comment></w:comments>",
                ["word/header1.xml"] = $"<w:hdr xmlns:w=\"{w}\"><w:p><w:r><w:t>Header</w:t></w:r></w:p></w:hdr>",
                ["word/footnotes.xml"] = $"<w:footnotes xmlns:w=\"{w}\"><w:footnote w:id=\"1\"><w:p><w:r><w:t>Note</w:t></w:r></w:p></w:footnote></w:footnotes>"
            });
        var result = new DocumentAnalyzer().Analyze(bytes, "elements.docx");
        Assert.Equal(1, result.Counts["Comments"]); Assert.Equal(2, result.Counts["Tracked changes"]); Assert.Equal(1, result.Counts["Images"]);
        Assert.Contains(result.Elements, e => e.Type == "Content control");
        Assert.Contains(result.Elements, e => e.Type == "hdr" && e.Text == "Header");
        Assert.Contains(result.Elements, e => e.Type == "Footnote");
        Assert.Contains(result.StructuralWarnings, e => e.Description.Contains("missingImage"));
    }
    [Fact]
    public void RejectsMalformedAndNonZipInputs()
    {
        Assert.Throws<InvalidDataException>(() => new DocumentAnalyzer().Analyze([1, 2, 3], "bad.docx"));
        Assert.Throws<System.Xml.XmlException>(() => new DocumentAnalyzer().Analyze(Fixtures.Create("<w:p>"), "malformed.docx"));
    }
    [Fact]
    public void BoldStyleTogglesAndDirectFalseAreResolved()
    {
        var result = new DocumentAnalyzer().Analyze(Fixtures.Create("<w:p><w:pPr><w:pStyle w:val=\"B\"/></w:pPr><w:r><w:t>Toggled</w:t></w:r><w:r><w:rPr><w:b w:val=\"0\"/></w:rPr><w:t>Off</w:t></w:r></w:p>", "<w:style w:type=\"paragraph\" w:styleId=\"A\"><w:name w:val=\"A\"/><w:rPr><w:b/></w:rPr></w:style><w:style w:type=\"paragraph\" w:styleId=\"B\"><w:name w:val=\"B\"/><w:basedOn w:val=\"A\"/><w:rPr><w:b/></w:rPr></w:style>"), "toggles.docx");
        Assert.All(result.Outline[0].Runs, r => Assert.Contains(r.Formatting, p => p.Property == "Bold" && p.Value == "false"));
        Assert.Contains(result.Outline[0].Runs[1].Formatting, p => p.Property == "Bold" && p.Source == "Direct formatting");
    }
}
