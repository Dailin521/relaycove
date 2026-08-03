using RelayCove.Server.Services;
using RelayCove.Shared.Conversations;

namespace RelayCove.Server.Tests.Services;

public sealed class ConversationRequestValidatorTests
{
    private readonly ConversationRequestValidator validator = new();

    [Fact]
    public void ValidateCreate_WhenChannelShapeIsInvalid_ReturnsCamelCaseFieldErrors()
    {
        var errors = validator.ValidateCreate(
            new CreateConversationRequest(
                ConversationType.PrivateChannel,
                Name: null,
                ParticipantUserId: Guid.NewGuid()),
            Guid.NewGuid());

        Assert.Equal(["name", "participantUserId"], errors.Keys.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void ValidateCreate_WhenDirectNamesSelf_ReturnsDiscriminatorFieldErrors()
    {
        var actorUserId = Guid.NewGuid();

        var errors = validator.ValidateCreate(
            new CreateConversationRequest(
                ConversationType.Direct,
                Name: "not allowed",
                ParticipantUserId: actorUserId),
            actorUserId);

        Assert.Equal(["name", "participantUserId"], errors.Keys.Order(StringComparer.Ordinal));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("line\nbreak")]
    public void ValidateCreate_WhenChannelNameIsInvalid_ReturnsNameError(string name)
    {
        var errors = validator.ValidateCreate(
            new CreateConversationRequest(ConversationType.PublicChannel, name),
            Guid.NewGuid());

        Assert.Equal(["name"], errors.Keys);
    }

    [Fact]
    public void ValidateCreate_WhenChannelNameContainsMalformedUtf16_ReturnsNameError()
    {
        const string malformed = "\uD800";

        var errors = validator.ValidateCreate(
            new CreateConversationRequest(ConversationType.PublicChannel, malformed),
            Guid.NewGuid());

        Assert.Equal(["name"], errors.Keys);
    }

    [Fact]
    public void ValidateMember_WhenIdentityAndRoleAreInvalid_ReturnsBothFields()
    {
        var errors = validator.ValidateMember(
            new UpsertConversationMemberRequest(Guid.Empty, (ConversationMemberRole)99));

        Assert.Equal(["role", "userId"], errors.Keys.Order(StringComparer.Ordinal));
    }
}
