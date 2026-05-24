namespace Hope.Agent.Application.Voice;

public sealed record TranscriptionResult(string Text, string Language, TimeSpan Duration);

public interface ISpeechToText
{
    Task<TranscriptionResult> TranscribeAsync(Stream audio, string mimeType, string? languageHint, CancellationToken ct);
}

public interface ITextToSpeech
{
    /// <summary>Produces audio bytes (typically MP3) for the given text.</summary>
    Task<ReadOnlyMemory<byte>> SynthesizeAsync(string text, string? voice, CancellationToken ct);
}

public sealed class SpeechOptions
{
    public const string Section = "Speech";
    public bool Enabled { get; set; }
    public string Provider { get; set; } = "openai";
    public string BaseUrl { get; set; } = "https://api.openai.com/v1";
    public string ApiKey { get; set; } = string.Empty;
    public string SttModel { get; set; } = "whisper-1";
    public string TtsModel { get; set; } = "tts-1";
    public string TtsVoice { get; set; } = "alloy";
    public string TtsFormat { get; set; } = "mp3";
    public int TimeoutSeconds { get; set; } = 60;
}
