using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Hope.Agent.Application.Observability;
using Hope.Agent.Application.Voice;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Hope.Agent.LLMGateway.Voice;

internal sealed class OpenAiSpeechServices(
    HttpClient http,
    IOptions<SpeechOptions> opts,
    ILogger<OpenAiSpeechServices> log) : ISpeechToText, ITextToSpeech
{
    public async Task<TranscriptionResult> TranscribeAsync(Stream audio, string mimeType, string? languageHint, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var o = opts.Value;
        using var form = new MultipartFormDataContent();
        var audioContent = new StreamContent(audio);
        audioContent.Headers.ContentType = new MediaTypeHeaderValue(string.IsNullOrWhiteSpace(mimeType) ? "audio/ogg" : mimeType);
        var ext = MimeToExtension(mimeType);
        form.Add(audioContent, "file", $"audio.{ext}");
        form.Add(new StringContent(o.SttModel), "model");
        form.Add(new StringContent("verbose_json"), "response_format");
        if (!string.IsNullOrWhiteSpace(languageHint))
            form.Add(new StringContent(languageHint), "language");

        using var resp = await http.PostAsync("audio/transcriptions", form, ct);
        resp.EnsureSuccessStatusCode();
        var json = await resp.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);
        var text = json.TryGetProperty("text", out var t) ? t.GetString() ?? string.Empty : string.Empty;
        var lang = json.TryGetProperty("language", out var l) ? l.GetString() ?? string.Empty : languageHint ?? string.Empty;
        HopeMeters.SpeechTranscribed.Add(1);
        log.LogDebug("STT transcribed {Bytes} chars in {Ms} ms", text.Length, sw.ElapsedMilliseconds);
        return new TranscriptionResult(text, lang, sw.Elapsed);
    }

    public async Task<ReadOnlyMemory<byte>> SynthesizeAsync(string text, string? voice, CancellationToken ct)
    {
        var o = opts.Value;
        var payload = new
        {
            model = o.TtsModel,
            input = text,
            voice = string.IsNullOrWhiteSpace(voice) ? o.TtsVoice : voice,
            response_format = o.TtsFormat,
        };
        using var resp = await http.PostAsJsonAsync("audio/speech", payload, ct);
        resp.EnsureSuccessStatusCode();
        var bytes = await resp.Content.ReadAsByteArrayAsync(ct);
        HopeMeters.SpeechSynthesized.Add(1);
        return bytes;
    }

    private static string MimeToExtension(string mime) => mime?.ToLowerInvariant() switch
    {
        "audio/ogg" => "ogg",
        "audio/oga" => "oga",
        "audio/opus" => "opus",
        "audio/mpeg" => "mp3",
        "audio/wav" or "audio/x-wav" => "wav",
        "audio/webm" => "webm",
        "audio/mp4" or "audio/m4a" => "m4a",
        "audio/flac" => "flac",
        _ => "ogg",
    };

    public static void Configure(HttpClient client, SpeechOptions opts)
    {
        client.BaseAddress = new Uri(opts.BaseUrl.TrimEnd('/') + "/");
        client.Timeout = TimeSpan.FromSeconds(opts.TimeoutSeconds);
        if (!string.IsNullOrWhiteSpace(opts.ApiKey))
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", opts.ApiKey);
    }
}
