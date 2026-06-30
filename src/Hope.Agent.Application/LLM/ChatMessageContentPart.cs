using System.Text.Json.Serialization;

namespace Hope.Agent.Application.LLM;

/// <summary>
/// Union type for multimodal chat message content parts.
/// Closes gap M-2. Supports text, image URL, and tool_use content blocks
/// following the Anthropic/OpenAI multimodal format. Provider routing
/// auto-detects multimodal capability and falls back to vision-capable
/// providers (Gemini, GPT-4V) when images are present.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(TextContentPart), "text")]
[JsonDerivedType(typeof(ImageContentPart), "image_url")]
[JsonDerivedType(typeof(ToolUseContentPart), "tool_use")]
[JsonDerivedType(typeof(ToolResultContentPart), "tool_result")]
public abstract record ChatMessageContentPart;

/// <summary>Plain text content block.</summary>
public sealed record TextContentPart(string Text) : ChatMessageContentPart;

/// <summary>
/// Image content block for vision-capable models.
/// Supports base64 data URLs and HTTPS URLs.
/// Supported formats: PNG, JPEG, GIF, WebP. Max size: 20MB.
/// </summary>
public sealed record ImageContentPart(
    string ImageUrl,
    ImageDetail? Detail = null
) : ChatMessageContentPart
{
    /// <summary>Validate that the image URL is a supported format.</summary>
    public bool IsValidFormat()
    {
        if (ImageUrl.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase))
        {
            var mimeType = ImageUrl.Split(';')[0].Split(':')[1];
            return mimeType switch
            {
                "image/png" => true,
                "image/jpeg" => true,
                "image/gif" => true,
                "image/webp" => true,
                _ => false
            };
        }
        return ImageUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>Resolution detail hint for image processing.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ImageDetail
{
    Auto,
    Low,
    High
}

/// <summary>Tool use content block (Anthropic format).</summary>
public sealed record ToolUseContentPart(
    string Id,
    string Name,
    string Input
) : ChatMessageContentPart;

/// <summary>Tool result content block (Anthropic format).</summary>
public sealed record ToolResultContentPart(
    string ToolUseId,
    string Content,
    bool IsError = false
) : ChatMessageContentPart;

/// <summary>Multimodal chat message supporting content parts alongside string Content.</summary>
public sealed record MultimodalChatMessage(
    string Role,
    string? Content = null,
    IReadOnlyList<ChatMessageContentPart>? ContentParts = null,
    string? Name = null,
    string? ToolCallId = null,
    string? ToolCallsJson = null)
{
    /// <summary>Returns true if this message contains image content parts.</summary>
    public bool HasImages => ContentParts?.Any(p => p is ImageContentPart) == true;
}
