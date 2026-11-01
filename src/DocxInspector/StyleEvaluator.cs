using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;

namespace DocxProcessing;

public sealed class StyleEvaluation
{
    public string OverallStatus { get; set; } = "likely_correct";
    public string Summary { get; set; } = "";
    public int CheckedBlocks { get; set; }
    public List<StyleEvaluationFinding> Findings { get; set; } = [];
    public string CoverageNote { get; set; } = "";
}

public sealed class StyleEvaluationFinding
{
    public string NodeId { get; set; } = "";
    public string Location { get; set; } = "";
    public string ContentExcerpt { get; set; } = "";
    public string AppliedStyle { get; set; } = "";
    public string Assessment { get; set; } = "review";
    public string Reasoning { get; set; } = "";
}

public sealed class StyleEvaluationException(string message, int statusCode = 502) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
}

internal sealed record EvaluationStyleOption(string Id, string Name, string Type);
internal sealed record EvaluationTableCellContext(int RowNumber, int CellNumber, int FirstCellNumber, List<string> RowCells, List<string> NearbyCellsAtSamePosition);
internal sealed record EvaluationBlockContext(string? SectionHeading, EvaluationTableCellContext? TableCell);
internal sealed record ContextualNode(DocumentNode Node, EvaluationBlockContext Context);
internal sealed record EvaluationBlock(string NodeId, string Location, string Type, string Text, string AppliedStyle, string AppliedStyleType, EvaluationBlockContext Context);
internal sealed record EvaluationInput(List<EvaluationStyleOption> Styles, List<EvaluationBlock> Items, string CoverageNote);
internal sealed record EvaluationPlan(List<EvaluationInput> Batches, int TotalBlocks);

internal sealed class ModelEvaluation
{
    public List<ModelFinding> Findings { get; set; } = [];
}

internal sealed class ModelFinding
{
    public string NodeId { get; set; } = "";
    public string Status { get; set; } = "";
    public string Reason { get; set; } = "";
}

internal static class StyleEvaluationInputBuilder
{
    const int MaxBlocksPerBatch = 40;
    const int MaxJsonCharacters = 256_000;
    const int MaxTextCharacters = 600;
    const int MaxContextCells = 8;
    const int MaxContextCellCharacters = 100;
    const int MaxNearbyCellSamples = 3;
    static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static EvaluationPlan Build(AnalysisResult analysis)
    {
        var styles = analysis.Styles
            .Select(x => new EvaluationStyleOption(x.Id, x.Name, x.Type))
            .ToList();
        var stylesById = analysis.Styles.ToDictionary(x => x.Id, StringComparer.Ordinal);
        var allBlocks = FlattenWithContext(analysis.Outline)
            .Where(x => x.Node.Type is "Heading" or "Title" or "Caption" or "Paragraph" or "Table")
            .Select(x => ToBlock(x, stylesById))
            .ToList();
        if (allBlocks.Count == 0)
            return new([], 0);

        if (SerializedSize(styles, []) > MaxJsonCharacters)
            throw new StyleEvaluationException("This document defines too many styles to include in one evaluation request.", 413);

        var batches = new List<List<EvaluationBlock>>();
        var current = new List<EvaluationBlock>();
        foreach (var block in allBlocks)
        {
            var wouldExceedSize = current.Count > 0 && SerializedSize(styles, current.Append(block).ToList()) > MaxJsonCharacters;
            if (current.Count >= MaxBlocksPerBatch || wouldExceedSize)
            {
                batches.Add(current);
                current = [];
            }

            current.Add(block);
            if (SerializedSize(styles, current) > MaxJsonCharacters)
                throw new StyleEvaluationException("A style-review item leaves too little room for one evaluation request.", 413);
        }
        if (current.Count > 0) batches.Add(current);

        var totalBatches = batches.Count;
        var inputs = batches
            .Select((items, index) => new EvaluationInput(styles, items, $"Batch {index + 1} of {totalBatches}."))
            .ToList();
        return new(inputs, allBlocks.Count);
    }

    static IEnumerable<ContextualNode> FlattenWithContext(
        IEnumerable<DocumentNode> nodes,
        string? sectionHeading = null,
        EvaluationTableCellContext? tableCell = null)
    {
        foreach (var node in nodes)
        {
            var activeHeading = node.Type == "Heading" && !string.IsNullOrWhiteSpace(node.Text)
                ? Clip(node.Text, MaxContextCellCharacters)
                : sectionHeading;
            yield return new(node, new(activeHeading, tableCell));

            if (node.Type == "Table")
            {
                var rows = node.Children.Where(x => x.Type == "tr").ToList();
                var rowCellsByRow = rows
                    .Select(row => row.Children.Where(x => x.Type == "tc").ToList())
                    .ToList();
                for (var rowIndex = 0; rowIndex < rows.Count; rowIndex++)
                {
                    var cells = rowCellsByRow[rowIndex];
                    for (var cellIndex = 0; cellIndex < cells.Count; cellIndex++)
                    {
                        var firstCellIndex = Math.Max(0, Math.Min(
                            cellIndex - MaxContextCells / 2,
                            cells.Count - MaxContextCells));
                        var rowCells = cells
                            .Skip(firstCellIndex)
                            .Take(MaxContextCells)
                            .Select(x => Clip(x.Text, MaxContextCellCharacters))
                            .ToList();
                        var nearbyStart = Math.Max(0, rowIndex - MaxNearbyCellSamples);
                        var nearbyEnd = Math.Min(rows.Count - 1, rowIndex + MaxNearbyCellSamples);
                        var nearbyCells = Enumerable.Range(nearbyStart, nearbyEnd - nearbyStart + 1)
                            .Where(index => index != rowIndex)
                            .OrderBy(index => Math.Abs(index - rowIndex))
                            .ThenBy(index => index)
                            .Select(index => rowCellsByRow[index])
                            .Where(otherCells => otherCells.Count > cellIndex)
                            .Select(otherCells => Clip(otherCells[cellIndex].Text, MaxContextCellCharacters))
                            .Where(text => !string.IsNullOrWhiteSpace(text))
                            .Take(MaxNearbyCellSamples)
                            .ToList();
                        var cellContext = new EvaluationTableCellContext(
                            rowIndex + 1,
                            cellIndex + 1,
                            firstCellIndex + 1,
                            rowCells,
                            nearbyCells);
                        foreach (var item in FlattenWithContext(cells[cellIndex].Children, activeHeading, cellContext))
                            yield return item;
                    }
                }
                continue;
            }

            foreach (var item in FlattenWithContext(node.Children, activeHeading, tableCell))
                yield return item;
        }
    }

    static EvaluationBlock ToBlock(ContextualNode contextualNode, Dictionary<string, StyleInfo> stylesById)
    {
        var node = contextualNode.Node;
        var style = node.StyleName is null ? "(no named style)" : $"{node.StyleName} ({node.StyleId})";
        var styleType = node.StyleId is not null && stylesById.TryGetValue(node.StyleId, out var info) ? info.Type : "unknown";
        return new(node.Id, Clip(node.Location, 300), node.Type, Clip(node.Text, MaxTextCharacters), style, styleType, contextualNode.Context);
    }

    public static object PromptItems(EvaluationInput input) => new
    {
        styles = input.Styles,
        items = input.Items.Select(x => new { x.NodeId, x.Location, x.Type, x.Text, x.AppliedStyle, x.AppliedStyleType, x.Context })
    };

    static int SerializedSize(List<EvaluationStyleOption> styles, List<EvaluationBlock> items) =>
        JsonSerializer.Serialize(new EvaluationInput(styles, items, ""), JsonOptions).Length;

    static string Clip(string value, int length) => value.Length <= length ? value : value[..length] + "…";
}

public sealed class OpenAiStyleEvaluator(HttpClient httpClient, IWebHostEnvironment environment)
{
    const string Endpoint = "https://api.openai.com/v1/responses";
    static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { PropertyNameCaseInsensitive = true };
    static readonly JsonElement OutputSchema = JsonDocument.Parse("""
        {
          "type":"object",
          "additionalProperties":false,
          "properties":{"findings":{"type":"array","items":{
            "type":"object",
            "additionalProperties":false,
            "properties":{
              "nodeId":{"type":"string"},
              "status":{"type":"string","enum":["mismatch","uncertain"]},
              "reason":{"type":"string"}
            },
            "required":["nodeId","status","reason"]
          }}},
          "required":["findings"]
        }
        """).RootElement.Clone();

    internal async Task<StyleEvaluation> EvaluateAsync(EvaluationPlan plan, CancellationToken cancellationToken)
    {
        var apiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY")?.Trim();
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new StyleEvaluationException("OpenAI is not configured. Add OPENAI_API_KEY to the repository .env file and restart the app.", 503);

        var model = Environment.GetEnvironmentVariable("OPENAI_MODEL")?.Trim();
        if (string.IsNullOrWhiteSpace(model)) model = "gpt-5-nano";

        string systemPrompt;
        try
        {
            systemPrompt = await File.ReadAllTextAsync(Path.Combine(environment.ContentRootPath, "Prompts", "style-evaluation.txt"), cancellationToken);
        }
        catch (IOException)
        {
            throw new StyleEvaluationException("The style evaluation prompt file is missing or cannot be read.", 500);
        }
        if (string.IsNullOrWhiteSpace(systemPrompt))
            throw new StyleEvaluationException("The style evaluation prompt file is empty.", 500);

        var findings = new List<StyleEvaluationFinding>();
        for (var batchIndex = 0; batchIndex < plan.Batches.Count; batchIndex++)
        {
            try
            {
                findings.AddRange(await EvaluateBatchAsync(plan.Batches[batchIndex], apiKey, model, systemPrompt, cancellationToken));
            }
            catch (StyleEvaluationException ex)
            {
                throw new StyleEvaluationException(
                    $"Style review stopped at batch {batchIndex + 1} of {plan.Batches.Count}. {ex.Message}",
                    ex.StatusCode);
            }
        }

        findings = findings.DistinctBy(x => x.NodeId).ToList();
        var mismatchCount = findings.Count(x => x.Assessment == "likely_mismatch");
        var unclearCount = findings.Count - mismatchCount;
        var summary = findings.Count == 0
            ? $"No likely style/text mismatches were flagged in {plan.TotalBlocks} evaluated items."
            : mismatchCount > 0 && unclearCount > 0
                ? $"Flagged {mismatchCount} likely mismatch(es) and {unclearCount} unclear item(s)."
                : mismatchCount > 0
                    ? $"Flagged {mismatchCount} likely style/text mismatch(es)."
                    : $"No likely mismatch was flagged; {unclearCount} item(s) were unclear. Check those items manually.";

        return new StyleEvaluation
        {
            OverallStatus = findings.Count == 0 ? "likely_correct" : "review_recommended",
            Summary = summary,
            CheckedBlocks = plan.TotalBlocks,
            Findings = findings,
            CoverageNote = $"Evaluated all {plan.TotalBlocks} main-body text blocks in {plan.Batches.Count} request(s)."
        };
    }

    async Task<List<StyleEvaluationFinding>> EvaluateBatchAsync(
        EvaluationInput input,
        string apiKey,
        string model,
        string systemPrompt,
        CancellationToken cancellationToken)
    {
        var userContext = JsonSerializer.Serialize(StyleEvaluationInputBuilder.PromptItems(input), JsonOptions);
        var payload = new
        {
            model,
            store = false,
            max_output_tokens = 25_000,
            input = new object[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user", content = userContext }
            },
            text = new
            {
                format = new { type = "json_schema", name = "docx_style_evaluation", strict = true, schema = OutputSchema }
            }
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        request.Content = new StringContent(JsonSerializer.Serialize(payload, JsonOptions), Encoding.UTF8, "application/json");

        HttpResponseMessage response;
        try { response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken); }
        catch (HttpRequestException) { throw new StyleEvaluationException("Could not reach the OpenAI API. Check the network connection and try again."); }
        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                var message = response.StatusCode switch
                {
                    System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden => "OpenAI rejected the API credentials. Check OPENAI_API_KEY in .env.",
                    System.Net.HttpStatusCode.TooManyRequests => "The OpenAI API rate limit or account quota was reached. Try again later or check the API account.",
                    _ => $"OpenAI could not evaluate this document (HTTP {(int)response.StatusCode})."
                };
                throw new StyleEvaluationException(message);
            }

            using var body = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
            var root = body.RootElement;
            if (!root.TryGetProperty("output", out var output))
                throw new StyleEvaluationException("OpenAI returned an unexpected response. Please try again.");

            string? json = null;
            var refused = false;
            foreach (var item in output.EnumerateArray())
            {
                if (!item.TryGetProperty("content", out var content)) continue;
                foreach (var part in content.EnumerateArray())
                {
                    if (part.TryGetProperty("type", out var type) && type.GetString() == "refusal") refused = true;
                    if (part.TryGetProperty("type", out type) && type.GetString() == "output_text" && part.TryGetProperty("text", out var text))
                        json = text.GetString();
                }
            }
            if (refused) throw new StyleEvaluationException("OpenAI could not evaluate this document content.");
            if (string.IsNullOrWhiteSpace(json))
            {
                var reason = root.TryGetProperty("incomplete_details", out var details) && details.ValueKind == JsonValueKind.Object &&
                    details.TryGetProperty("reason", out var reasonValue) ? reasonValue.GetString() : null;
                if (reason == "max_output_tokens")
                    throw new StyleEvaluationException("OpenAI reached the response token limit before returning this batch.");
                throw new StyleEvaluationException("OpenAI returned no text evaluation. Please try again.");
            }

            var modelResult = JsonSerializer.Deserialize<ModelEvaluation>(json, JsonOptions)
                ?? throw new StyleEvaluationException("OpenAI returned an unreadable evaluation. Please try again.");
            var blocksById = input.Items.ToDictionary(x => x.NodeId, StringComparer.Ordinal);
            var findings = modelResult.Findings
                .Where(x => blocksById.ContainsKey(x.NodeId) && (x.Status is "mismatch" or "uncertain"))
                .DistinctBy(x => x.NodeId)
                .Select(x =>
                {
                    var block = blocksById[x.NodeId];
                    return new StyleEvaluationFinding
                    {
                        NodeId = block.NodeId,
                        Location = block.Location,
                        ContentExcerpt = block.Text,
                        AppliedStyle = block.AppliedStyle,
                        Assessment = x.Status == "mismatch" ? "likely_mismatch" : "review",
                        Reasoning = x.Reason
                    };
                })
                .ToList();
            return findings;
        }
    }
}
