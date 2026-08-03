using RelayCove.Server.Data.Entities;
using RelayCove.Shared.Conversations;

namespace RelayCove.Server.Tests.Data;

public sealed class ConversationTests
{
    private static readonly DateTime CreatedAt = new(2026, 8, 3, 6, 0, 0, DateTimeKind.Utc);
    private static readonly Guid FirstUserId = Guid.Parse("0f913478-c469-49dd-ae88-bd553035ca29");
    private static readonly Guid SecondUserId = Guid.Parse("eb855fba-e75c-4b85-aa21-7c559d6fc97b");

    [Fact]
    public void CreateDirect_WhenParticipantsAreReversed_UsesSameCanonicalKeyAndEmptyName()
    {
        var first = Conversation.CreateDirect(Guid.NewGuid(), FirstUserId, SecondUserId, FirstUserId, CreatedAt);
        var reversed = Conversation.CreateDirect(Guid.NewGuid(), SecondUserId, FirstUserId, SecondUserId, CreatedAt);

        Assert.Equal(first.DirectParticipantKey, reversed.DirectParticipantKey);
        Assert.Equal(
            "0f913478-c469-49dd-ae88-bd553035ca29:eb855fba-e75c-4b85-aa21-7c559d6fc97b",
            first.DirectParticipantKey);
        Assert.Empty(first.Name);
        Assert.Equal(ConversationType.Direct, first.Type);
    }

    [Fact]
    public void CreateDirect_WhenParticipantsOrCreatorAreInvalid_Throws()
    {
        Assert.Throws<ArgumentException>(() => Conversation.CreateDirect(
            Guid.NewGuid(), FirstUserId, FirstUserId, FirstUserId, CreatedAt));
        Assert.Throws<ArgumentException>(() => Conversation.CreateDirect(
            Guid.NewGuid(), FirstUserId, SecondUserId, Guid.NewGuid(), CreatedAt));
    }

    [Fact]
    public void CreateChannel_WhenNameContainsUnicodeScalarBoundary_PreservesName()
    {
        var name = string.Concat(Enumerable.Repeat("🛰️", 30));

        var conversation = Conversation.CreateChannel(
            Guid.NewGuid(), ConversationType.PrivateChannel, name, FirstUserId, CreatedAt.AddTicks(9999));

        Assert.Equal(name, conversation.Name);
        Assert.Equal(CreatedAt, conversation.CreatedAt);
        Assert.Null(conversation.DirectParticipantKey);
    }

    [Theory]
    [InlineData(ConversationType.Direct, "channel")]
    [InlineData(ConversationType.PublicChannel, "")]
    [InlineData(ConversationType.PrivateChannel, "   ")]
    [InlineData(ConversationType.PublicChannel, "line\nbreak")]
    public void CreateChannel_WhenTypeOrNameIsInvalid_Throws(ConversationType type, string name)
    {
        Assert.ThrowsAny<ArgumentException>(() => Conversation.CreateChannel(
            Guid.NewGuid(), type, name, FirstUserId, CreatedAt));
    }

    [Fact]
    public void CreateChannel_WhenNameExceedsScalarLimitOrHasMalformedUnicode_Throws()
    {
        var tooLong = new string('a', Conversation.MaximumNameLength + 1);
        const string malformed = "\uD800";

        Assert.Throws<ArgumentOutOfRangeException>(() => Conversation.CreateChannel(
            Guid.NewGuid(), ConversationType.PublicChannel, tooLong, FirstUserId, CreatedAt));
        Assert.Throws<ArgumentException>(() => Conversation.CreateChannel(
            Guid.NewGuid(), ConversationType.PublicChannel, malformed, FirstUserId, CreatedAt));
    }

    [Fact]
    public void RenameAndDeletion_WhenTimeMovesBackward_DoNotPartiallyMutate()
    {
        var conversation = Conversation.CreateChannel(
            Guid.NewGuid(), ConversationType.PublicChannel, "General", FirstUserId, CreatedAt);
        conversation.Rename("Announcements", CreatedAt.AddMinutes(2).AddTicks(10));

        Assert.Throws<ArgumentOutOfRangeException>(() => conversation.Rename("Regressed", CreatedAt.AddMinutes(1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => conversation.MarkDeleted(CreatedAt.AddMinutes(1)));

        Assert.Equal("Announcements", conversation.Name);
        Assert.False(conversation.IsDeleted);
        Assert.Equal(CreatedAt.AddMinutes(2), conversation.UpdatedAt);

        conversation.MarkDeleted(CreatedAt.AddMinutes(3));
        conversation.Restore(CreatedAt.AddMinutes(4));
        Assert.False(conversation.IsDeleted);
        Assert.Equal(CreatedAt.AddMinutes(4), conversation.UpdatedAt);
    }

    [Fact]
    public void AdvanceLastReadMessageId_WhenValueRegresses_ThrowsWithoutMutation()
    {
        var member = new ConversationMember(
            Guid.NewGuid(), FirstUserId, ConversationMemberRole.Member, CreatedAt, lastReadMessageId: 10);

        member.AdvanceLastReadMessageId(15);
        Assert.Throws<ArgumentOutOfRangeException>(() => member.AdvanceLastReadMessageId(14));

        Assert.Equal(15, member.LastReadMessageId);
    }

    [Fact]
    public void ConversationMember_WhenInputsAreInvalid_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ConversationMember(
            Guid.NewGuid(), FirstUserId, (ConversationMemberRole)99, CreatedAt));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ConversationMember(
            Guid.NewGuid(), FirstUserId, ConversationMemberRole.Member, CreatedAt, lastReadMessageId: -1));
        Assert.Throws<ArgumentException>(() => new ConversationMember(
            Guid.NewGuid(), FirstUserId, ConversationMemberRole.Member, DateTime.SpecifyKind(CreatedAt, DateTimeKind.Local)));
    }
}
