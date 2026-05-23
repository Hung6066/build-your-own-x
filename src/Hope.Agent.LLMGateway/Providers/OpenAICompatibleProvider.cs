using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Hope.Agent.Application.LLM;

namespace Hope.Agent.LLMGateway.Providers;

/// <summary>OpenAI-compatible provider — works for OpenAI, vLLM, Qwen-server, Ollama (/v1), LM Studio.</summary>
public sealed class OpenAICompatibleProvider(HttpClient http, OpenAICompatibleOptions options, string name)
    : IChatCompletionProvider, IEmbeddingProvider
{
    public string Name => name;

    public async Task<ChatResponse> CompleteAsync(ChatRequest request, CancellationToken ct)
    {
        var payload = BuildPayload(request, stream: false);
        using var resp = await http.PostAsJsonAsync("chat/completions", payload, JsonOpts, ct);
        resp.EnsureSuccessStatusCode();
        var json = await resp.Content.ReadFromJsonAsync<JsonElement>(JsonOpts, ct);
        var choice = json.GetProperty("choices")[0];
        var msg = choice.GetProperty("message");
        var content = msg.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() ?? string.Empty : string.Empty;
        var toolCalls = new List<ToolCall>();
        if (msg.TryGetProperty("tool_calls", out var tc) && tc.ValueKind == JsonValueKind.Array)
        {
            foreach (var t in tc.EnumerateArray())
            {
                toolCalls.Add(new ToolCall(
                    t.GetProperty("id").GetString() ?? string.Empty,
                    t.GetProperty("function").GetProperty("name").GetString() ?? string.Empty,
                    t.GetProperty("function").GetProperty("arguments").GetString() ?? "{}"));
            }
        }
        var usage = json.TryGetProperty("usage", out var u)
            ? new ChatUsage(u.GetProperty("prompt_tokens").GetInt32(), u.GetProperty("completion_tokens").GetInt32(), u.GetProperty("total_tokens").GetInt32())
            : new ChatUsage(0, 0, 0);
        var finish = choice.TryGetProperty("finish_reason", out var f) ? f.GetString() ?? "stop" : "stop";
        return new ChatResponse(content, toolCalls, finish, usage, Name, request.Model ?? options.Model);
    }

    public async IAsyncEnumerable<string> StreamAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken ct)
    {
        var payload = BuildPayload(request, stream: true);
        using var req = new HttpRequestMessage(HttpMethod.Post, "chat/completions")
        {
            Content = JsonContent.Create(payload, options: JsonOpts),
        };
        using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);
        while (!reader.EndOfStream)
        {
            var line = await reader.ReadLineAsync(ct);
            if (string.IsNullOrWhiteSpace(line) || !line.StartsWith("data:", StringComparison.Ordinal)) continue;
            var data = line[5..].Trim();
            if (data == "[DONE]") yield break;
            JsonElement evt;
            try { evt = JsonDocument.Parse(data).RootElement; } catch { continue; }
            if (!evt.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0) continue;
            var delta = choices[0].GetProperty("delta");
            if (delta.TryGetProperty("content", out var dc) && dc.ValueKind == JsonValueKind.String)
            {
                yield return dc.GetString() ?? string.Empty;
            }
        }
    }

    public async Task<EmbeddingResponse> EmbedAsync(EmbeddingRequest request, CancellationToken ct)
    {
        var payload = new { model = request.Model ?? options.EmbeddingModel, input = request.Inputs };
        using var resp = await http.PostAsJsonAsync("embeddings", payload, JsonOpts, ct);
        resp.EnsureSuccessStatusCode();
        var json = await resp.Content.ReadFromJsonAsync<JsonElement>(JsonOpts, ct);
        var vectors = new List<ReadOnlyMemory<float>>();
        foreach (var d in json.GetProperty("data").EnumerateArray())
        {
            var arr = d.GetProperty("embedding");
            var vec = new float[arr.GetArrayLength()];
            int i = 0;
            foreach (var v in arr.EnumerateArray()) vec[i++] = v.GetSingle();
            vectors.Add(vec);
        }
        var total = json.TryGetProperty("usage", out var u) && u.TryGetProperty("total_tokens", out var t) ? t.GetInt32() : 0;
        return new EmbeddingResponse(vectors, Name, payload.model, total);
    }

    private object BuildPayload(ChatRequest request, bool stream)
    {
        var messages = request.Messages.Select(m => (object)new
        {
            role = m.Role,
            content = m.Content,
            name = m.Name,
            tool_call_id = m.ToolCallId,
        }).ToList();

        var dict = new Dictionary<string, object?>
        {
            ["model"] = request.Model ?? options.Model,
            ["messages"] = messages,
            ["temperature"] = request.Temperature,
            ["stream"] = stream,
        };
        if (request.MaxTokens is { } mt) dict["max_tokens"] = mt;
        if (request.Tools is { Count: > 0 })
        {
            dict["tools"] = request.Tools.Select(t => new
            {
                type = "function",
                function = new
                {
                    name = t.Name,
                    description = t.Description,
                    parameters = JsonDocument.Parse(t.ParametersJsonSchema).RootElement,
                },
            }).ToList();
            if (!string.IsNullOrEmpty(request.ToolChoice)) dict["tool_choice"] = request.ToolChoice;
        }
        return dict;
    }

    internal static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    internal static HttpClient Configure(HttpClient client, OpenAICompatibleOptions opts)
    {
        client.BaseAddress = new Uri(opts.BaseUrl.TrimEnd('/') + "/");
        client.Timeout = TimeSpan.FromSeconds(opts.TimeoutSeconds);
        if (!string.IsNullOrEmpty(opts.ApiKey))
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", opts.ApiKey);
        }
        return client;
    }
}
