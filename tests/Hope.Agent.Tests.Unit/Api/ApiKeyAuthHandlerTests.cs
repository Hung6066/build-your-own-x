using System.Text.Encodings.Web;
using FluentAssertions;
using Hope.Agent.Api.Security;
using Hope.Agent.Application.Security;
using Hope.Agent.Tools.Mcp;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

namespace Hope.Agent.Tests.Unit.Api;

public sealed class ApiKeyAuthHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 6, 10, 12, 0, 0, TimeSpan.Zero);

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static async Task<AuthenticateResult> AuthenticateAsync(McpOptions mcpOptions, string? apiKey)
    {
        var schemeOptions = Substitute.For<IOptionsMonitor<ApiKeyAuthOptions>>();
        schemeOptions.Get(ApiKeyAuthHandler.SchemeName).Returns(new ApiKeyAuthOptions());

        var mcpMonitor = Substitute.For<IOptionsMonitor<McpOptions>>();
        mcpMonitor.CurrentValue.Returns(mcpOptions);
        var lifecycle = Substitute.For<IApiKeyLifecycleStore>();
        lifecycle.FindValidAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<Hope.Agent.Domain.Security.ApiKeyRecord?>(null));

        var handler = new ApiKeyAuthHandler(
            schemeOptions,
            NullLoggerFactory.Instance,
            UrlEncoder.Default,
            mcpMonitor,
            lifecycle,
            new FixedTimeProvider(Now));

        var context = new DefaultHttpContext();
        if (apiKey is not null)
            context.Request.Headers["X-Api-Key"] = apiKey;

        await handler.InitializeAsync(
            new AuthenticationScheme(ApiKeyAuthHandler.SchemeName, null, typeof(ApiKeyAuthHandler)),
            context);
        return await handler.AuthenticateAsync();
    }

    [Fact]
    public void HashKey_produces_lowercase_sha256_hex()
    {
        var hash = ApiKeyAuthHandler.HashKey("secret");

        hash.Should().HaveLength(64);
        hash.Should().MatchRegex("^[0-9a-f]{64}$");
    }

    [Fact]
    public async Task Missing_header_returns_no_result()
    {
        var result = await AuthenticateAsync(new McpOptions(), apiKey: null);

        result.None.Should().BeTrue();
    }

    [Fact]
    public async Task Valid_lifecycle_key_succeeds_and_carries_key_name()
    {
        var opts = new McpOptions
        {
            ApiKeys = [new ApiKeyEntry { Name = "partner-his", Hash = ApiKeyAuthHandler.HashKey("k1") }],
        };

        var result = await AuthenticateAsync(opts, "k1");

        result.Succeeded.Should().BeTrue();
        result.Principal!.Identity!.Name.Should().Be("partner-his");
    }

    [Fact]
    public async Task Revoked_key_is_rejected()
    {
        var opts = new McpOptions
        {
            ApiKeys = [new ApiKeyEntry { Name = "old", Hash = ApiKeyAuthHandler.HashKey("k1"), Revoked = true }],
        };

        var result = await AuthenticateAsync(opts, "k1");

        result.Succeeded.Should().BeFalse();
        result.Failure!.Message.Should().Contain("revoked");
    }

    [Fact]
    public async Task Expired_key_is_rejected()
    {
        var opts = new McpOptions
        {
            ApiKeys =
            [
                new ApiKeyEntry
                {
                    Name = "old",
                    Hash = ApiKeyAuthHandler.HashKey("k1"),
                    ExpiresAt = Now.AddMinutes(-1),
                },
            ],
        };

        var result = await AuthenticateAsync(opts, "k1");

        result.Succeeded.Should().BeFalse();
        result.Failure!.Message.Should().Contain("expired");
    }

    [Fact]
    public async Task Key_expiring_in_future_is_accepted()
    {
        var opts = new McpOptions
        {
            ApiKeys =
            [
                new ApiKeyEntry
                {
                    Name = "rotating",
                    Hash = ApiKeyAuthHandler.HashKey("k1"),
                    ExpiresAt = Now.AddDays(30),
                },
            ],
        };

        var result = await AuthenticateAsync(opts, "k1");

        result.Succeeded.Should().BeTrue();
    }

    [Fact]
    public async Task Legacy_flat_hash_list_still_works()
    {
        var opts = new McpOptions { ApiKeyHashes = [ApiKeyAuthHandler.HashKey("legacy")] };

        var result = await AuthenticateAsync(opts, "legacy");

        result.Succeeded.Should().BeTrue();
        result.Principal!.Identity!.Name.Should().Be("mcp-api-key-client");
    }

    [Fact]
    public async Task Invalid_key_is_rejected()
    {
        var opts = new McpOptions { ApiKeyHashes = [ApiKeyAuthHandler.HashKey("right")] };

        var result = await AuthenticateAsync(opts, "wrong");

        result.Succeeded.Should().BeFalse();
    }

    [Fact]
    public async Task No_keys_configured_rejects_everything()
    {
        var result = await AuthenticateAsync(new McpOptions(), "anything");

        result.Succeeded.Should().BeFalse();
        result.Failure!.Message.Should().Contain("No API keys configured");
    }

    [Fact]
    public async Task Rotation_old_key_expired_new_key_active()
    {
        var opts = new McpOptions
        {
            ApiKeys =
            [
                new ApiKeyEntry { Name = "v1", Hash = ApiKeyAuthHandler.HashKey("old-key"), ExpiresAt = Now.AddMinutes(-5) },
                new ApiKeyEntry { Name = "v2", Hash = ApiKeyAuthHandler.HashKey("new-key") },
            ],
        };

        (await AuthenticateAsync(opts, "old-key")).Succeeded.Should().BeFalse();
        var fresh = await AuthenticateAsync(opts, "new-key");
        fresh.Succeeded.Should().BeTrue();
        fresh.Principal!.Identity!.Name.Should().Be("v2");
    }
}
