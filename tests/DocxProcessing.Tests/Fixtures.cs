using System.IO.Compression;
using System.Text;

namespace DocxProcessing.Tests;

// These minimal packages are test data only. The application has no document-writing API.
internal static class Fixtures
{
    const string W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    const string R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    public static byte[] Create(string body, string styles = "", Dictionary<string, string>? extra = null, string relationships = "")
    {
        var files = new Dictionary<string, string>
        {
            ["[Content_Types].xml"] = """
            <Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
              <Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>
              <Default Extension="xml" ContentType="application/xml"/>
              <Override PartName="/word/document.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml"/>
              <Override PartName="/word/styles.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.styles+xml"/>
            </Types>
            """,
            ["_rels/.rels"] = $"<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"{R}/officeDocument\" Target=\"word/document.xml\"/></Relationships>",
            ["word/_rels/document.xml.rels"] = $"<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rStyles\" Type=\"{R}/styles\" Target=\"styles.xml\"/>{relationships}</Relationships>",
            ["word/document.xml"] = $"<w:document xmlns:w=\"{W}\" xmlns:r=\"{R}\"><w:body>{body}<w:sectPr/></w:body></w:document>",
            ["word/styles.xml"] = $"<w:styles xmlns:w=\"{W}\">{styles}</w:styles>"
        };
        if (extra is not null) foreach (var (path, xml) in extra) files[path] = xml;
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, true))
            foreach (var (path, xml) in files)
            {
                var entry = zip.CreateEntry(path);
                using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
                writer.Write(xml);
            }
        return stream.ToArray();
    }
    public const string Styles = """
        <w:docDefaults><w:rPrDefault><w:rPr><w:rFonts w:ascii="Calibri"/><w:sz w:val="22"/></w:rPr></w:rPrDefault></w:docDefaults>
        <w:style w:type="paragraph" w:default="1" w:styleId="Normal"><w:name w:val="Normal"/><w:pPr><w:spacing w:after="160"/></w:pPr></w:style>
        <w:style w:type="paragraph" w:styleId="Base"><w:name w:val="Section base"/><w:basedOn w:val="Normal"/><w:pPr><w:outlineLvl w:val="0"/></w:pPr><w:rPr><w:b/></w:rPr></w:style>
        <w:style w:type="paragraph" w:styleId="Custom"><w:name w:val="Custom section"/><w:basedOn w:val="Base"/><w:rPr><w:color w:val="123456"/></w:rPr></w:style>
        <w:style w:type="paragraph" w:styleId="Sub"><w:name w:val="Subsection"/><w:basedOn w:val="Normal"/><w:pPr><w:outlineLvl w:val="1"/></w:pPr></w:style>
        <w:style w:type="character" w:styleId="Emphasis"><w:name w:val="Emphasis"/><w:rPr><w:i/></w:rPr></w:style>
        <w:style w:type="table" w:styleId="TableCustom"><w:name w:val="Custom table"/></w:style>
        <w:style w:type="numbering" w:styleId="ListCustom"><w:name w:val="Custom list"/></w:style>
        """;
    public static byte[] Representative() => Create("""
        <w:p><w:pPr><w:pStyle w:val="Custom"/></w:pPr><w:r><w:t>Section one</w:t></w:r></w:p>
        <w:p><w:pPr><w:pStyle w:val="Sub"/></w:pPr><w:r><w:t>Subsection</w:t></w:r></w:p>
        <w:p><w:pPr><w:jc w:val="center"/><w:spacing w:before="120"/></w:pPr><w:r><w:t>Plain</w:t></w:r><w:r><w:rPr><w:rStyle w:val="Emphasis"/><w:b/><w:sz w:val="28"/></w:rPr><w:t>Mixed</w:t></w:r></w:p>
        <w:tbl><w:tblPr><w:tblStyle w:val="TableCustom"/></w:tblPr><w:tblGrid><w:gridCol w:w="2000"/><w:gridCol w:w="2000"/></w:tblGrid><w:tr><w:tc><w:tcPr><w:gridSpan w:val="2"/><w:vMerge w:val="restart"/></w:tcPr><w:p><w:r><w:t>Merged</w:t></w:r></w:p></w:tc></w:tr></w:tbl>
        <w:p><w:bookmarkStart w:id="0" w:name="here"/><w:r><w:t>Anchor</w:t></w:r><w:bookmarkEnd w:id="0"/><w:hyperlink w:anchor="here"><w:r><w:t>Go</w:t></w:r></w:hyperlink></w:p>
        """, Styles);
}
