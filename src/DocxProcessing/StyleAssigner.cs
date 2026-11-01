using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;

namespace DocxProcessing;

internal sealed record SuggestedStyleAssignment(string NodeId, string StyleId, string Reason);
internal sealed record StyleAssignmentResult(List<SuggestedStyleAssignment> Assignments, int TotalBlocks);

internal sealed class ModelStyleAssignmentResponse
{
    public List<ModelStyleAssignment> Assignments { get; set; } = [];
}

internal sealed class ModelStyleAssignment
{
    public string NodeId { get; set; } = "";
    public string StyleId { get; set; } = "";
    public string Reason { get; set; } = "";
}

internal sealed class IncompleteStyleAssignmentsException : Exception { }

public sealed class OpenAiStyleAssigner(HttpClient httpClient, IWebHostEnvironment environment)
{
    const string Endpoint = "https://api.openai.com/v1/responses";
    const int MaxConcurrentBatches = 4;
    static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { PropertyNameCaseInsensitive = true };
    static readonly JsonElement OutputSchema = JsonDocument.Parse("""
        {
          "type":"object",
          "additionalProperties":false,
          "properties":{"assignments":{"type":"array","items":{
            "type":"object",
            "additionalProperties":false,
            "properties":{
              "nodeId":{"type":"string"},
              "styleId":{"type":"string"},
              "reason":{"type":"string"}
            },
            "required":["nodeId","styleId","reason"]
          }}},
          "required":["assignments"]
        }
        """).RootElement.Clone();

    internal async Task<StyleAssignmentResult> AssignAsync(EvaluationPlan plan, CancellationToken cancellationToken)
    {
        if (plan.TotalBlocks == 0) return new([], 0);

        var apiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY")?.Trim();
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new StyleEvaluationException("OpenAI is not configured. Add OPENAI_API_KEY to the repository .env file and restart the app.", 503);

        var model = Environment.GetEnvironmentVariable("OPENAI_MODEL")?.Trim();
        if (string.IsNullOrWhiteSpace(model)) model = "gpt-5-nano";

        string systemPrompt;
        try
        {
            systemPrompt = await File.ReadAllTextAsync(Path.Combine(environment.ContentRootPath, "Prompts", "style-assignment.txt"), cancellationToken);
        }
        catch (IOException)
        {
            throw new StyleEvaluationException("The style assignment prompt file is missing or cannot be read.", 500);
        }
        if (string.IsNullOrWhiteSpace(systemPrompt))
            throw new StyleEvaluationException("The style assignment prompt file is empty.", 500);

        using var concurrencyLimit = new SemaphoreSlim(MaxConcurrentBatches);
        var batchTasks = plan.Batches
            .Select((batch, index) => AssignIndexedBatchAsync(
                batch,
                index,
                plan.Batches.Count,
                apiKey,
                model,
                systemPrompt,
                concurrencyLimit,
                cancellationToken))
            .ToArray();
        var results = await Task.WhenAll(batchTasks);
        var assignments = results.SelectMany(result => result).DistinctBy(x => x.NodeId).ToList();
        return new(assignments, plan.TotalBlocks);
    }

    async Task<List<SuggestedStyleAssignment>> AssignIndexedBatchAsync(
        EvaluationInput input,
        int batchIndex,
        int totalBatches,
        string apiKey,
        string model,
        string systemPrompt,
        SemaphoreSlim concurrencyLimit,
        CancellationToken cancellationToken)
    {
        var entered = false;
        try
        {
            await concurrencyLimit.WaitAsync(cancellationToken);
            entered = true;
            return await AssignBatchWithRecoveryAsync(input, apiKey, model, systemPrompt, cancellationToken);
        }
        catch (StyleEvaluationException exception)
        {
            throw new StyleEvaluationException(
                $"Style assignment stopped at batch {batchIndex + 1} of {totalBatches}. {exception.Message}",
                exception.StatusCode);
        }
        finally
        {
            if (entered) concurrencyLimit.Release();
        }
    }

    async Task<List<SuggestedStyleAssignment>> AssignBatchWithRecoveryAsync(
        EvaluationInput input,
        string apiKey,
        string model,
        string systemPrompt,
        CancellationToken cancellationToken)
    {
        try
        {
            return await AssignBatchAsync(input, apiKey, model, systemPrompt, cancellationToken);
        }
        catch (IncompleteStyleAssignmentsException) when (input.Items.Count > 1)
        {
            var midpoint = input.Items.Count / 2;
            var firstHalf = new EvaluationInput(
                input.Styles,
                input.Items.Take(midpoint).ToList(),
                input.CoverageNote,
                input.Document);
            var secondHalf = new EvaluationInput(
                input.Styles,
                input.Items.Skip(midpoint).ToList(),
                input.CoverageNote,
                input.Document);

            var firstResults = await AssignBatchWithRecoveryAsync(
                firstHalf, apiKey, model, systemPrompt, cancellationToken);
            var secondResults = await AssignBatchWithRecoveryAsync(
                secondHalf, apiKey, model, systemPrompt, cancellationToken);
            firstResults.AddRange(secondResults);
            return firstResults;
        }
        catch (IncompleteStyleAssignmentsException)
        {
            throw new StyleEvaluationException(
                "OpenAI omitted a document item even after the request was split into smaller batches. Please try again.");
        }
    }

    async Task<List<SuggestedStyleAssignment>> AssignBatchAsync(
        EvaluationInput input,
        string apiKey,
        string model,
        string systemPrompt,
        CancellationToken cancellationToken)
    {
        var userContext = JsonSerializer.Serialize(StyleEvaluationInputBuilder.AssignmentPromptItems(input), JsonOptions);
        var payload = new
        {
            model,
            store = false,
            max_output_tokens = 12_000,
            input = new object[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user", content = userContext }
            },
            text = new
            {
                format = new { type = "json_schema", name = "docx_style_assignments", strict = true, schema = OutputSchema }
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
                    _ => $"OpenAI could not assign styles for this document (HTTP {(int)response.StatusCode})."
                };
                throw new StyleEvaluationException(message);
            }

            using var body = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
            var root = body.RootElement;
            if (!root.TryGetProperty("output", out var output))
                throw new StyleEvaluationException("OpenAI returned an unexpected style assignment response. Please try again.");

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
            if (refused) throw new StyleEvaluationException("OpenAI could not assign styles for this document content.");
            if (string.IsNullOrWhiteSpace(json))
            {
                var reason = root.TryGetProperty("incomplete_details", out var details) && details.ValueKind == JsonValueKind.Object &&
                    details.TryGetProperty("reason", out var reasonValue) ? reasonValue.GetString() : null;
                if (reason == "max_output_tokens")
                    throw new StyleEvaluationException("OpenAI reached the response token limit before returning this batch.");
                throw new StyleEvaluationException("OpenAI returned no style assignments. Please try again.");
            }

            var modelResult = JsonSerializer.Deserialize<ModelStyleAssignmentResponse>(json, JsonOptions)
                ?? throw new StyleEvaluationException("OpenAI returned unreadable style assignments. Please try again.");
            var blocksById = input.Items.ToDictionary(x => x.NodeId, StringComparer.Ordinal);
            var stylesById = input.Styles.ToDictionary(x => x.Id, StringComparer.Ordinal);
            if (modelResult.Assignments.Count != input.Items.Count ||
                modelResult.Assignments.GroupBy(x => x.NodeId, StringComparer.Ordinal).Any(group => group.Count() != 1 || !blocksById.ContainsKey(group.Key)) ||
                input.Items.Any(block => modelResult.Assignments.All(x => x.NodeId != block.NodeId)))
                throw new IncompleteStyleAssignmentsException();

            var validAssignments = new List<SuggestedStyleAssignment>();
            foreach (var assignment in modelResult.Assignments)
            {
                if (string.IsNullOrEmpty(assignment.StyleId)) continue;
                var block = blocksById[assignment.NodeId];
                if (!stylesById.TryGetValue(assignment.StyleId, out var style))
                    throw new StyleEvaluationException("OpenAI selected a style that is not defined in the document. Please try again.");
                var expectedType = block.Type == "Table" ? "table" : "paragraph";
                if (!style.Type.Equals(expectedType, StringComparison.OrdinalIgnoreCase))
                    throw new StyleEvaluationException("OpenAI selected a style with the wrong type for a document item. Please try again.");
                if (assignment.StyleId != block.AppliedStyleId)
                    validAssignments.Add(new(assignment.NodeId, assignment.StyleId, assignment.Reason));
            }
            return validAssignments;
        }
    }
}
