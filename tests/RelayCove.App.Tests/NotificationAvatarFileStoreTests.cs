using RelayCove.App.Services;
using RelayCove.Core;

namespace RelayCove.App.Tests;

public sealed class NotificationAvatarFileStoreTests
{
    [Fact]
    public void CreateFileStem_WhenAvatarUrlContainsIdentity_DoesNotExposeOriginalValue()
    {
        var account = AccountId.Create(RealmEndpoint.Parse("https://zulip.example"), 7);
        const string sourceUrl = "https://zulip.example/user_avatars/8/avatar.png?version=4";

        var stem = NotificationAvatarFileStore.CreateFileStem(account, sourceUrl);

        Assert.Equal(64, stem.Length);
        Assert.DoesNotContain("zulip", stem, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("avatar", stem, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("/", stem, StringComparison.Ordinal);
        Assert.DoesNotContain(":", stem, StringComparison.Ordinal);
    }

    [Fact]
    public void GetAccountCacheDirectory_WhenAccountVaries_IsAccountIsolatedAndOpaque()
    {
        var first = AccountId.Create(RealmEndpoint.Parse("https://zulip.example"), 7);
        var second = AccountId.Create(RealmEndpoint.Parse("https://zulip.example"), 8);

        var firstDirectory = NotificationAvatarFileStore.GetAccountCacheDirectory("C:\\cache", first);
        var secondDirectory = NotificationAvatarFileStore.GetAccountCacheDirectory("C:\\cache", second);

        Assert.NotEqual(firstDirectory, secondDirectory);
        Assert.DoesNotContain("zulip", firstDirectory, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(first.Value, firstDirectory, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith(Path.GetFullPath("C:\\cache"), firstDirectory, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("image/png", ".png")]
    [InlineData("image/jpeg; charset=binary", ".jpg")]
    [InlineData("image/svg+xml", null)]
    [InlineData("text/html", null)]
    public void GetSafeImageExtension_WhenContentTypeVaries_AllowsOnlyRasterAvatarFormats(
        string contentType,
        string? expected)
    {
        Assert.Equal(expected, NotificationAvatarFileStore.GetSafeImageExtension(contentType));
    }
}
