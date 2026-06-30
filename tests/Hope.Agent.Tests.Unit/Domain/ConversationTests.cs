using FluentAssertions;
using Hope.Agent.Domain.Conversations;
using Xunit;

namespace Hope.Agent.Tests.Unit.Domain;

public sealed class ConversationTests
{
    private static readonly DateTimeOffset Now = new(2026, 6, 10, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Create_initializes_identity_and_timestamps()
    {
        var userId = Guid.NewGuid();

        var convo = Conversation.Create(userId, "Triage", Now);

        convo.Id.Should().NotBeEmpty();
        convo.UserId.Should().Be(userId);
        convo.Title.Should().Be("Triage");
        convo.CreatedAt.Should().Be(Now);
        convo.UpdatedAt.Should().Be(Now);
        convo.Messages.Should().BeEmpty();
    }

    [Fact]
    public void AddMessage_appends_and_bumps_UpdatedAt()
    {
        var convo = Conversation.Create(Guid.NewGuid(), "t", Now);
        var later = Now.AddMinutes(5);

        var msg = convo.AddMessage(MessageRole.User, "hello", later);

        convo.Messages.Should().ContainSingle().Which.Should().BeSameAs(msg);
        convo.UpdatedAt.Should().Be(later);
        msg.ConversationId.Should().Be(convo.Id);
        msg.Role.Should().Be(MessageRole.User);
        msg.Content.Should().Be("hello");
    }

    [Fact]
    public void AddMessage_preserves_tool_metadata()
    {
        var convo = Conversation.Create(Guid.NewGuid(), "t", Now);

        var msg = convo.AddMessage(MessageRole.Tool, "{}", Now, toolName: "patient_lookup", toolCallId: "call_1");

        msg.ToolName.Should().Be("patient_lookup");
        msg.ToolCallId.Should().Be("call_1");
    }

    [Fact]
    public void Messages_are_ordered_by_insertion()
    {
        var convo = Conversation.Create(Guid.NewGuid(), "t", Now);
        convo.AddMessage(MessageRole.User, "first", Now);
        convo.AddMessage(MessageRole.Assistant, "second", Now.AddSeconds(1));

        convo.Messages.Select(m => m.Content).Should().ContainInOrder("first", "second");
    }
}
