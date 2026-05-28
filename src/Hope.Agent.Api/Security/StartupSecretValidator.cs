namespace Hope.Agent.Api.Security;

/// <summary>
/// Validates that all secrets required for the current configuration are present and
/// are not left at their development-placeholder values.
/// Called once at startup (before <c>app.RunAsync()</c>) so the process crashes
/// fast with a clear message instead of failing at runtime with a cryptic auth error.
/// Validation is skipped in the Development environment where Key Vault is not wired
/// and placeholder secrets are intentional.
/// </summary>
internal static class StartupSecretValidator
{
    // Well-known dev-placeholder prefixes that must never reach production.
    private static readonly string[] DangerousPrefixes =
    [
        "dev-secret",
        "changeme",
        "change-me",
        "your-secret",
        "todo",
        "placeholder",
        "example",
        "test-secret",
        "sk-your",     // OpenAI key placeholder
        "sk-ant-your", // Anthropic placeholder
    ];

    /// <summary>
    /// Throws <see cref="InvalidOperationException"/> if any mandatory secret is
    /// missing or unsafe in a non-Development environment.
    /// </summary>
    internal static void Validate(IConfiguration cfg, IWebHostEnvironment env, ILogger logger)
    {
        if (env.IsDevelopment())
        {
            logger.LogInformation(
                "StartupSecretValidator: skipping checks in Development environment.");
            return;
        }

        var errors = new List<string>();

        // ── JWT ──────────────────────────────────────────────────────────────
        // Required in every environment. Key Vault must supply it if KeyVault:Enabled=true.
        CheckRequired(cfg, "Jwt:Secret", errors, minLength: 32);

        // ── Database connections ──────────────────────────────────────────────
        CheckRequired(cfg, "ConnectionStrings:Postgres", errors);
        CheckRequired(cfg, "ConnectionStrings:Redis", errors);

        // ── Webhook ───────────────────────────────────────────────────────────
        // Only required when the webhook endpoint is reachable (always enabled; no feature flag).
        CheckRequired(cfg, "Webhook:Secret", errors, minLength: 32);

        // ── LLM API keys (active provider only) ───────────────────────────────
        var chatProvider = (cfg["LLM:DefaultChatProvider"] ?? "openai").ToLowerInvariant();
        CheckLlmProvider(cfg, chatProvider, errors);

        var embedProvider = (cfg["LLM:DefaultEmbeddingProvider"] ?? "openai").ToLowerInvariant();
        if (embedProvider != chatProvider)
            CheckLlmProvider(cfg, embedProvider, errors);

        // ── Channels (feature-flagged) ─────────────────────────────────────────
        if (cfg.GetValue<bool>("Telegram:Enabled"))
            CheckRequired(cfg, "Telegram:BotToken", errors);

        if (cfg.GetValue<bool>("Channels:Zalo:Enabled"))
        {
            CheckRequired(cfg, "Channels:Zalo:AppSecret", errors);
            CheckRequired(cfg, "Channels:Zalo:OaAccessToken", errors);
        }

        if (cfg.GetValue<bool>("Channels:Slack:Enabled"))
        {
            CheckRequired(cfg, "Channels:Slack:SigningSecret", errors, minLength: 16);
            CheckRequired(cfg, "Channels:Slack:BotToken", errors);
        }

        if (errors.Count == 0)
        {
            logger.LogInformation(
                "StartupSecretValidator: all required secrets are present.");
            return;
        }

        // Log each failure individually so structured-log aggregators can surface them.
        foreach (var error in errors)
            logger.LogCritical("StartupSecretValidator: {SecretError}", error);

        throw new InvalidOperationException(
            $"Startup aborted — {errors.Count} secret validation failure(s). " +
            "Ensure all required secrets are provisioned (e.g. via Key Vault) before starting " +
            "the application in a non-Development environment. " +
            "See preceding CRITICAL log entries for details.");
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static void CheckRequired(
        IConfiguration cfg,
        string key,
        List<string> errors,
        int minLength = 1)
    {
        var value = cfg[key];

        if (string.IsNullOrWhiteSpace(value))
        {
            errors.Add($"'{key}' is missing or empty.");
            return;
        }

        if (value.Length < minLength)
        {
            errors.Add($"'{key}' is too short (minimum {minLength} characters).");
            return;
        }

        if (IsDangerousPlaceholder(value))
            errors.Add($"'{key}' appears to contain a dev placeholder — replace with a real secret.");
    }

    private static void CheckLlmProvider(
        IConfiguration cfg,
        string provider,
        List<string> errors)
    {
        // Local/open-source providers do not require an API key.
        if (provider is "ollama" or "local" or "llamacpp")
            return;

        var apiKeyPath = provider switch
        {
            "openai" => "LLM:OpenAI:ApiKey",
            "anthropic" => "LLM:Anthropic:ApiKey",
            "gemini" => "LLM:Gemini:ApiKey",
            "qwen" => "LLM:Qwen:ApiKey",
            _ => $"LLM:{provider}:ApiKey",
        };

        CheckRequired(cfg, apiKeyPath, errors);
    }

    private static bool IsDangerousPlaceholder(string value)
    {
        var lower = value.ToLowerInvariant();
        foreach (var prefix in DangerousPrefixes)
        {
            if (lower.StartsWith(prefix, StringComparison.Ordinal))
                return true;
        }

        return false;
    }
}
