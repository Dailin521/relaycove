using System.Security.Cryptography;
using System.Text;
using RelayCove.Client.Storage;

namespace RelayCove.Client.Tests.Storage;

public sealed class AccountScopeIdentityTests
{
    private static readonly Guid UserId = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");

    [Fact]
    public void Create_WhenUrisAreCanonicallyEquivalent_ReturnsStableScope()
    {
        var root = Path.Combine(Path.GetTempPath(), "RelayCoveScopeTests");
        var first = AccountScopeIdentity.Create(
            new Uri("HTTPS://BÜCHER.example:443/team/../relay"),
            UserId,
            root);
        var second = AccountScopeIdentity.Create(
            new Uri("https://xn--bcher-kva.example/relay/"),
            UserId,
            root + Path.DirectorySeparatorChar);
        var expectedInput = "https://xn--bcher-kva.example/relay/\n" +
            UserId.ToString("D").ToLowerInvariant();
        var expectedId = Convert.ToBase64String(
                SHA256.HashData(Encoding.UTF8.GetBytes(expectedInput)))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

        Assert.Equal(expectedId, first.Id);
        Assert.Equal(first, second);
        Assert.Equal("https://xn--bcher-kva.example/relay/", first.CanonicalServerBaseUri.AbsoluteUri);
        Assert.Equal(Path.Combine(Path.GetFullPath(root), first.Id), first.ScopeDirectory);
        Assert.Equal(Path.Combine(first.ScopeDirectory, "relaycove.db"), first.DatabasePath);
        Assert.DoesNotContain("=", first.Id, StringComparison.Ordinal);
        Assert.DoesNotContain(first.Id, first.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(UserId.ToString(), first.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(root, first.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Create_WhenServerPathPortOrUserDiffers_IsolatesScopes()
    {
        var root = Path.Combine(Path.GetTempPath(), "RelayCoveScopeTests");
        var baseline = AccountScopeIdentity.Create(
            new Uri("https://relaycove.example/team/"),
            UserId,
            root);

        Assert.NotEqual(
            baseline.Id,
            AccountScopeIdentity.Create(
                new Uri("https://relaycove.example/other/"),
                UserId,
                root).Id);
        Assert.NotEqual(
            baseline.Id,
            AccountScopeIdentity.Create(
                new Uri("https://relaycove.example:8443/team/"),
                UserId,
                root).Id);
        Assert.NotEqual(
            baseline.Id,
            AccountScopeIdentity.Create(
                new Uri("https://relaycove.example/team/"),
                Guid.NewGuid(),
                root).Id);
    }

    [Theory]
    [InlineData("relative/path")]
    [InlineData("ftp://relaycove.example/")]
    [InlineData("https://user:password@relaycove.example/")]
    [InlineData("https://relaycove.example/?token=secret")]
    [InlineData("https://relaycove.example/#fragment")]
    public void Create_WhenServerUriIsUnsafe_RejectsIt(string serverUri)
    {
        Assert.Throws<ArgumentException>(() => AccountScopeIdentity.Create(
            new Uri(serverUri, UriKind.RelativeOrAbsolute),
            UserId,
            Path.GetTempPath()));
    }

    [Fact]
    public void Create_WhenUserOrRootIsInvalid_RejectsIt()
    {
        var serverUri = new Uri("https://relaycove.example/");

        Assert.Throws<ArgumentException>(() => AccountScopeIdentity.Create(
            serverUri,
            Guid.Empty,
            Path.GetTempPath()));
        Assert.Throws<ArgumentException>(() => AccountScopeIdentity.Create(
            serverUri,
            UserId,
            "relative-root"));
    }

    [Fact]
    public void Create_WhenRootIsDriveRoot_KeepsDatabaseInsideRoot()
    {
        var root = Path.GetPathRoot(Path.GetTempPath())!;

        var identity = AccountScopeIdentity.Create(
            new Uri("https://relaycove.example/"),
            UserId,
            root);

        Assert.Equal(identity.Id, Path.GetRelativePath(root, identity.ScopeDirectory));
        Assert.Equal("relaycove.db", Path.GetRelativePath(identity.ScopeDirectory, identity.DatabasePath));
    }
}
