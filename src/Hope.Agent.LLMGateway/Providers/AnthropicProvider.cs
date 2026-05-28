using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Hope.Agent.Application.LLM;

namespace Hope.Agent.LLMGateway.Providers;

public sealed class AnthropicProvider(HttpClient http, AnthropicOptions options) : IChatCompletionProvider
{
    public string Name => "anthropic";

    public async Task<ChatResponse> CompleteAsync(ChatRequest request, CancellationToken ct)
    {
        var payload = BuildPayload(request, stream: false);
        using var resp = await http.PostAsJsonAsync("messages", payload, OpenAICompatibleProvider.JsonOpts, ct);
        resp.EnsureSuccessStatusCode();
        var json = await resp.Content.ReadFromJsonAsync<JsonElement>(OpenAICompatibleProvider.JsonOpts, ct);

        var content = string.Empty;
        var toolCalls = new List<ToolCall>();
        foreach (var block in json.GetProperty("content").EnumerateArray())
        {
            var type = block.GetProperty("type").GetString();
            if (type == "text") content += block.GetProperty("text").GetString();
            else if (type == "tool_use")
            {
                toolCalls.Add(new ToolCall(
                    block.GetProperty("id").GetString() ?? string.Empty,
                    block.GetProperty("name").GetString() ?? string.Empty,
                    block.GetProperty("input").GetRawText()));
            }
        }
        var usage = json.TryGetProperty("usage", out var u)
            ? new ChatUsage(u.GetProperty("input_tokens").GetInt32(), u.GetProperty("output_tokens").GetInt32(), u.GetProperty("input_tokens").GetInt32() + u.GetProperty("output_tokens").GetInt32())
            : new ChatUsage(0, 0, 0);
        usage = usage with { CostUsd = (usage.PromptTokens * options.CostPer1KInputTokens + usage.CompletionTokens * options.CostPer1KOutputTokens) / 1000m };
        var stop = json.TryGetProperty("stop_reason", out var s) ? s.GetString() ?? "end_turn" : "end_turn";
        return new ChatResponse(content, toolCalls, stop, usage, Name, request.Model ?? options.Model);
    }

    public async IAsyncEnumerable<string> StreamAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken ct)
    {
        var payload = BuildPayload(request, stream: true);
        using var req = new HttpRequestMessage(HttpMethod.Post, "messages")
        {
            Content = JsonContent.Create(payload, options: OpenAICompatibleProvider.JsonOpts),
        };
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
            if (evt.TryGetProperty("type", out var t) && t.GetString() == "content_block_delta"
                && evt.TryGetProperty("delta", out var d)
                && d.TryGetProperty("text", out var text))
            {
                yield return text.GetString() ?? string.Empty;
            }
        }
    }

    private object BuildPayload(ChatRequest request, bool stream)
    {
        string? system = null;
        var msgs = new List<object>();
        foreach (var m in request.Messages)
        {
            if (m.Role == "system") { system = (system is null ? "" : system + "\n") + m.Content; continue; }
            msgs.Add(new { role = m.Role == "assistant" ? "assistant" : "user", content = m.Content });
        }
        var dict = new Dictionary<string, object?>
        {
            ["model"] = request.Model ?? options.Model,
            ["messages"] = msgs,
            ["max_tokens"] = request.MaxTokens ?? 4096,
            ["temperature"] = request.Temperature,
            ["stream"] = stream,
        };
        if (system is not null) dict["system"] = system;
        if (request.Tools is { Count: > 0 })
        {
            dict["tools"] = request.Tools.Select(t => new
            {
                name = t.Name,
                description = t.Description,
                input_schema = JsonDocument.Parse(t.ParametersJsonSchema).RootElement,
            }).ToList();
        }
        return dict;
    }

    internal static HttpClient Configure(HttpClient client, AnthropicOptions opts)
    {
        client.BaseAddress = new Uri(opts.BaseUrl.TrimEnd('/') + "/");
        client.DefaultRequestHeaders.Add("x-api-key", opts.ApiKey);
        client.DefaultRequestHeaders.Add("anthropic-version", opts.Version);
        return client;
    }
}
