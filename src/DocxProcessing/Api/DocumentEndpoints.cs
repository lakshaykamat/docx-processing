using System.Text.Json;
using DocxProcessing;
using DocxProcessing.Services;
using DocumentFormat.OpenXml.Packaging;

namespace DocxProcessing.Api;

internal static class DocumentEndpoints
{
    const long MaxUploadBytes = 20 * 1024 * 1024;

    public static void Map(WebApplication app)
    {
        app.MapPost("/api/inspect", InspectAsync);
        app.MapPost("/api/add-styles", AddStylesAsync);
        app.MapPost("/api/evaluate-style", EvaluateStyleAsync);
    }

    static async Task<IResult> InspectAsync(HttpRequest request)
    {
        try
        {
            var upload = await ReadUploadAsync(request);
            if (upload.Error is not null) return upload.Error;

            var analysis = new DocumentAnalyzer().Analyze(upload.Bytes!, upload.Filename!);
            return Results.Ok(analysis);
        }
        catch (Exception exception) when (IsDocumentException(exception))
        {
            return Results.BadRequest(new { error = "This file cannot be inspected. It may be damaged, encrypted, or an unsupported DOCX package." });
        }
    }

    static async Task<IResult> AddStylesAsync(HttpRequest request, OpenAiStyleAssigner assigner)
    {
        try
        {
            var upload = await ReadUploadAsync(request);
            if (upload.Error is not null) return upload.Error;

            var input = upload.Bytes!;
            var analysis = new DocumentAnalyzer().Analyze(input, upload.Filename!);
            var plan = StyleEvaluationInputBuilder.Build(
                analysis,
                StyleEvaluationInputBuilder.StyleAssignmentMaxBlocksPerBatch);
            if (plan.TotalBlocks == 0)
                return Results.BadRequest(new { error = "No main-body text or tables were available for style assignment." });

            var assignments = await assigner.AssignAsync(plan, request.HttpContext.RequestAborted);
            var output = new DocxStyleApplier().Apply(input, analysis, assignments.Assignments);
            if (output.Error is not null) return Results.BadRequest(new { error = output.Error });

            request.HttpContext.Response.Headers["X-Styles-Assigned"] = output.UpdatedCount.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var downloadName = Path.GetFileNameWithoutExtension(upload.Filename) + "-styled.docx";
            return Results.File(
                output.Bytes,
                "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
                downloadName);
        }
        catch (StyleEvaluationException exception)
        {
            return Results.Json(new { error = exception.Message }, statusCode: exception.StatusCode);
        }
        catch (Exception exception) when (IsDocumentException(exception))
        {
            return Results.BadRequest(new { error = "This file cannot be styled. It may be damaged, encrypted, or an unsupported DOCX package." });
        }
        catch (JsonException)
        {
            return Results.Json(new { error = "OpenAI returned unreadable style assignments. Please try again." }, statusCode: 502);
        }
    }

    static async Task<IResult> EvaluateStyleAsync(HttpRequest request, OpenAiStyleEvaluator evaluator)
    {
        try
        {
            var upload = await ReadUploadAsync(request);
            if (upload.Error is not null) return upload.Error;

            var analysis = new DocumentAnalyzer().Analyze(upload.Bytes!, upload.Filename!);
            var plan = StyleEvaluationInputBuilder.Build(analysis);
            if (plan.TotalBlocks == 0)
            {
                return Results.Ok(new StyleEvaluation
                {
                    OverallStatus = "insufficient_evidence",
                    Summary = "No main-body text was available to evaluate.",
                    CoverageNote = "Included 0 of 0 main-body text blocks."
                });
            }

            var result = await evaluator.EvaluateAsync(plan, request.HttpContext.RequestAborted);
            return Results.Ok(result);
        }
        catch (StyleEvaluationException exception)
        {
            return Results.Json(new { error = exception.Message }, statusCode: exception.StatusCode);
        }
        catch (Exception exception) when (IsDocumentException(exception))
        {
            return Results.BadRequest(new { error = "This file cannot be inspected. It may be damaged, encrypted, or an unsupported DOCX package." });
        }
        catch (JsonException)
        {
            return Results.Json(new { error = "OpenAI returned an unreadable response. Please try again." }, statusCode: 502);
        }
    }

    static async Task<UploadReadResult> ReadUploadAsync(HttpRequest request)
    {
        if (!request.HasFormContentType)
            return UploadReadResult.Failed("Upload a DOCX using the file field.");

        var form = await request.ReadFormAsync(request.HttpContext.RequestAborted);
        var file = form.Files.GetFile("file");
        if (file is null || !Path.GetExtension(file.FileName).Equals(".docx", StringComparison.OrdinalIgnoreCase))
            return UploadReadResult.Failed("Choose a .docx file.");
        if (file.Length is 0 or > MaxUploadBytes)
            return UploadReadResult.Failed("Files must be between 1 byte and 20 MB.");

        await using var stream = new MemoryStream();
        await file.CopyToAsync(stream, request.HttpContext.RequestAborted);
        return UploadReadResult.Success(stream.ToArray(), Path.GetFileName(file.FileName));
    }

    static bool IsDocumentException(Exception exception) => exception is
        InvalidDataException or
        System.Xml.XmlException or
        OpenXmlPackageException or
        ArgumentException or
        IOException or
        InvalidOperationException or
        FormatException or
        NotSupportedException;

    sealed record UploadReadResult(byte[]? Bytes, string? Filename, IResult? Error)
    {
        public static UploadReadResult Success(byte[] bytes, string filename) => new(bytes, filename, null);
        public static UploadReadResult Failed(string message) => new(null, null, Results.BadRequest(new { error = message }));
    }
}
