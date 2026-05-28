using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Hope.Agent.Application.LLM;

namespace Hope.Agent.LLMGateway.Providers;

public sealed class GeminiProvider(HttpClient http, GeminiOptions options) : IChatCompletionProvider, IEmbeddingProvider
{
    public string Name => "gemini";

    public async Task<ChatResponse> CompleteAsync(ChatRequest request, CancellationToken ct)
    {
        var model = request.Model ?? options.Model;
        var payload = BuildPayload(request);
        var url = $"models/{model}:generateContent?key={options.ApiKey}";
        using var resp = await http.PostAsJsonAsync(url, payload, OpenAICompatibleProvider.JsonOpts, ct);
        resp.EnsureSuccessStatusCode();
        var json = await resp.Content.ReadFromJsonAsync<JsonElement>(OpenAICompatibleProvider.JsonOpts, ct);

        var content = string.Empty;
        var toolCalls = new List<ToolCall>();
        if (json.TryGetProperty("candidates", out var cands) && cands.GetArrayLength() > 0)
        {
            var parts = cands[0].GetProperty("content").GetProperty("parts");
            foreach (var p in parts.EnumerateArray())
            {
                if (p.TryGetProperty("text", out var t)) content += t.GetString();
                else if (p.TryGetProperty("functionCall", out var fc))
                {
                    toolCalls.Add(new ToolCall(
                        Guid.NewGuid().ToString("N"),
                        fc.GetProperty("name").GetString() ?? string.Empty,
                        fc.GetProperty("args").GetRawText()));
                }
            }
        }
        var usage = json.TryGetProperty("usageMetadata", out var u)
            ? new ChatUsage(
                u.TryGetProperty("promptTokenCount", out var pt) ? pt.GetInt32() : 0,
                u.TryGetProperty("candidatesTokenCount", out var ct2) ? ct2.GetInt32() : 0,
                u.TryGetProperty("totalTokenCount", out var tt) ? tt.GetInt32() : 0)
            : new ChatUsage(0, 0, 0);
        usage = usage with { CostUsd = (usage.PromptTokens * options.CostPer1KInputTokens + usage.CompletionTokens * options.CostPer1KOutputTokens) / 1000m };
        return new ChatResponse(content, toolCalls, "stop", usage, Name, model);
    }

    public async IAsyncEnumerable<string> StreamAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken ct)
    {
        var model = request.Model ?? options.Model;
        var url = $"models/{model}:streamGenerateContent?alt=sse&key={options.ApiKey}";
        var payload = BuildPayload(request);
        using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(payload, options: OpenAICompatibleProvider.JsonOpts) };
        using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);
        while (!reader.EndOfStream)
        {
            var line = await reader.ReadLineAsync(ct);
            if (line is null || !line.StartsWith("data:", StringComparison.Ordinal)) continue;
            var data = line[5..].Trim();
            if (string.IsNullOrEmpty(data)) continue;
            JsonElement evt;
            try { evt = JsonDocument.Parse(data).RootElement; } catch { continue; }
            if (evt.TryGetProperty("candidates", out var cands) && cands.GetArrayLength() > 0)
            {
                foreach (var p in cands[0].GetProperty("content").GetProperty("parts").EnumerateArray())
                {
                    if (p.TryGetProperty("text", out var t)) yield return t.GetString() ?? string.Empty;
                }
            }
        }
    }

    public async Task<EmbeddingResponse> EmbedAsync(EmbeddingRequest request, CancellationToken ct)
    {
        var model = request.Model ?? options.EmbeddingModel;
        var vectors = new List<ReadOnlyMemory<float>>();
        foreach (var input in request.Inputs)
        {
            var url = $"models/{model}:embedContent?key={options.ApiKey}";
            var payload = new { model = $"models/{model}", content = new { parts = new[] { new { text = input } } } };
            using var resp = await http.PostAsJsonAsync(url, payload, OpenAICompatibleProvider.JsonOpts, ct);
            resp.EnsureSuccessStatusCode();
            var json = await resp.Content.ReadFromJsonAsync<JsonElement>(OpenAICompatibleProvider.JsonOpts, ct);
            var arr = json.GetProperty("embedding").GetProperty("values");
            var vec = new float[arr.GetArrayLength()];
            int i = 0;
            foreach (var v in arr.EnumerateArray()) vec[i++] = v.GetSingle();
            vectors.Add(vec);
        }
        return new EmbeddingResponse(vectors, Name, model, 0);
    }

    private static object BuildPayload(ChatRequest request)
    {
        var contents = request.Messages
            .Where(m => m.Role != "system")
            .Select(m => new
            {
                role = m.Role == "assistant" ? "model" : "user",
                parts = new[] { new { text = m.Content } },
            }).ToList();
        var system = request.Messages.Where(m => m.Role == "system").Select(m => m.Content).ToArray();
        var generationConfig = new Dictionary<string, object?>
        {
            ["temperature"] = request.Temperature,
            ["maxOutputTokens"] = request.MaxTokens,
        };
        if (request.ResponseFormat is { Type: "json_object" or "json_schema" } rf)
        {
            generationConfig["responseMimeType"] = "application/json";
            if (rf.Type == "json_schema" && !string.IsNullOrEmpty(rf.JsonSchema))
            {
                generationConfig["responseSchema"] = JsonDocument.Parse(rf.JsonSchema!).RootElement;
            }
        }
        var dict = new Dictionary<string, object?>
        {
            ["contents"] = contents,
            ["generationConfig"] = generationConfig,
        };
        if (system.Length > 0) dict["systemInstruction"] = new { parts = system.Select(s => new { text = s }).ToArray() };
        if (request.Tools is { Count: > 0 })
        {
            dict["tools"] = new[]
            {
                new
                {
                    functionDeclarations = request.Tools.Select(t => new
                    {
                        name = t.Name,
                        description = t.Description,
                        parameters = JsonDocument.Parse(t.ParametersJsonSchema).RootElement,
                    }).ToList(),
                },
            };
        }
        return dict;
    }

    internal static HttpClient Configure(HttpClient client, GeminiOptions opts)
    {
        client.BaseAddress = new Uri(opts.BaseUrl.TrimEnd('/') + "/");
        return client;
    }
}
