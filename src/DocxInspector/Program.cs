using DocxProcessing;
using Microsoft.AspNetCore.Http.Features;

LoadDotEnv();
var builder = WebApplication.CreateBuilder(args);
builder.Services.Configure<FormOptions>(o => o.MultipartBodyLengthLimit = 21 * 1024 * 1024);
builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = 21 * 1024 * 1024);
builder.Services.AddHttpClient<OpenAiStyleEvaluator>();
var app = builder.Build();
app.UseDefaultFiles();
app.UseStaticFiles();
app.MapPost("/api/inspect", async (HttpRequest request) =>
{
    try
    {
        if (!request.HasFormContentType) return Results.BadRequest(new { error = "Upload a DOCX using the file field." });
        var form = await request.ReadFormAsync(request.HttpContext.RequestAborted);
        var file = form.Files.GetFile("file");
        if (file is null || !Path.GetExtension(file.FileName).Equals(".docx", StringComparison.OrdinalIgnoreCase))
            return Results.BadRequest(new { error = "Choose a .docx file." });
        if (file.Length is 0 or > 20 * 1024 * 1024)
            return Results.BadRequest(new { error = "Files must be between 1 byte and 20 MB." });
        using var stream = new MemoryStream();
        await file.CopyToAsync(stream, request.HttpContext.RequestAborted);
        return Results.Ok(new DocumentAnalyzer().Analyze(stream.ToArray(), Path.GetFileName(file.FileName)));
    }
    catch (Exception ex) when (ex is InvalidDataException or System.Xml.XmlException or DocumentFormat.OpenXml.Packaging.OpenXmlPackageException or ArgumentException or IOException or InvalidOperationException or FormatException or NotSupportedException)
    {
        return Results.BadRequest(new { error = "This file cannot be inspected. It may be damaged, encrypted, or an unsupported DOCX package." });
    }
});
app.MapPost("/api/evaluate-style", async (HttpRequest request, OpenAiStyleEvaluator evaluator) =>
{
    try
    {
        if (!request.HasFormContentType) return Results.BadRequest(new { error = "Upload a DOCX using the file field." });
        var form = await request.ReadFormAsync(request.HttpContext.RequestAborted);
        var file = form.Files.GetFile("file");
        if (file is null || !Path.GetExtension(file.FileName).Equals(".docx", StringComparison.OrdinalIgnoreCase))
            return Results.BadRequest(new { error = "Choose a .docx file." });
        if (file.Length is 0 or > 20 * 1024 * 1024)
            return Results.BadRequest(new { error = "Files must be between 1 byte and 20 MB." });
        using var stream = new MemoryStream();
        await file.CopyToAsync(stream, request.HttpContext.RequestAborted);
        var analysis = new DocumentAnalyzer().Analyze(stream.ToArray(), Path.GetFileName(file.FileName));
        var plan = StyleEvaluationInputBuilder.Build(analysis);
        if (plan.TotalBlocks == 0)
            return Results.Ok(new StyleEvaluation { OverallStatus = "insufficient_evidence", Summary = "No main-body text was available to evaluate.", CoverageNote = "Included 0 of 0 main-body text blocks." });
        return Results.Ok(await evaluator.EvaluateAsync(plan, request.HttpContext.RequestAborted));
    }
    catch (StyleEvaluationException ex)
    {
        return Results.Json(new { error = ex.Message }, statusCode: ex.StatusCode);
    }
    catch (Exception ex) when (ex is InvalidDataException or System.Xml.XmlException or DocumentFormat.OpenXml.Packaging.OpenXmlPackageException or ArgumentException or IOException or InvalidOperationException or FormatException or NotSupportedException)
    {
        return Results.BadRequest(new { error = "This file cannot be inspected. It may be damaged, encrypted, or an unsupported DOCX package." });
    }
    catch (System.Text.Json.JsonException)
    {
        return Results.Json(new { error = "OpenAI returned an unreadable response. Please try again." }, statusCode: 502);
    }
});
app.Run();

static void LoadDotEnv()
{
    var starts = new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory }.Distinct(StringComparer.OrdinalIgnoreCase);
    foreach (var start in starts)
    {
        var isStart = true;
        for (DirectoryInfo? directory = new(start); directory is not null; directory = directory.Parent)
        {
            var isRepositoryRoot = File.Exists(Path.Combine(directory.FullName, "DocxProcessing.slnx"));
            var path = Path.Combine(directory.FullName, ".env");
            if ((isStart || isRepositoryRoot) && File.Exists(path))
            {
                foreach (var line in File.ReadLines(path))
                {
                    var entry = line.Trim();
                    if (entry.Length == 0 || entry.StartsWith('#')) continue;
                    if (entry.StartsWith("export ", StringComparison.Ordinal)) entry = entry[7..].TrimStart();
                    var separator = entry.IndexOf('=');
                    if (separator <= 0) continue;
                    var name = entry[..separator].Trim();
                    if (name.Length == 0 || !name.All(c => char.IsAsciiLetterOrDigit(c) || c == '_') || char.IsDigit(name[0])) continue;
                    if (Environment.GetEnvironmentVariable(name) is not null) continue;
                    var value = entry[(separator + 1)..].Trim();
                    if (value.Length >= 2 && ((value[0] == '"' && value[^1] == '"') || (value[0] == '\'' && value[^1] == '\'')))
                        value = value[1..^1];
                    Environment.SetEnvironmentVariable(name, value);
                }
            }
            if (isRepositoryRoot) return;
            isStart = false;
        }
    }
}
