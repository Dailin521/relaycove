using RelayCove.Core;

namespace RelayCove.Core.Tests;

public sealed class CredentialEnvelopeTests
{
    [Fact]
    public void ToString_WhenCredentialsPresent_RedactsEverySecretAndIdentityValue()
    {
        var credential = new CredentialEnvelope(RealmEndpoint.Parse("https://secret.example/"), "person@example.test", 99, "api-secret-value");
        var value = credential.ToString();

        Assert.DoesNotContain("secret.example", value, StringComparison.Ordinal);
        Assert.DoesNotContain("person@example.test", value, StringComparison.Ordinal);
        Assert.DoesNotContain("99", value, StringComparison.Ordinal);
        Assert.DoesNotContain("api-secret-value", value, StringComparison.Ordinal);
    }

    [Fact]
    public void AuthenticationRequest_ToString_RedactsPasswordAndIdentity()
    {
        var request = new AuthenticationRequest(
            RealmEndpoint.Parse("https://secret.example/"),
            "person@example.test",
            "password-value");

        var value = request.ToString();

        Assert.DoesNotContain("secret.example", value, StringComparison.Ordinal);
        Assert.DoesNotContain("person@example.test", value, StringComparison.Ordinal);
        Assert.DoesNotContain("password-value", value, StringComparison.Ordinal);
    }

    [Fact]
    public void SendRequest_ToString_RedactsCredentialsQueueConversationAndContent()
    {
        var credential = new CredentialEnvelope(RealmEndpoint.Parse("https://secret.example/"), "person@example.test", 99, "api-secret-value");
        var request = new SendRequest(credential, "queue-secret", "local-secret", new DirectMessage([2]), "message-secret");

        var value = request.ToString();

        Assert.DoesNotContain("api-secret-value", value, StringComparison.Ordinal);
        Assert.DoesNotContain("queue-secret", value, StringComparison.Ordinal);
        Assert.DoesNotContain("local-secret", value, StringComparison.Ordinal);
        Assert.DoesNotContain("message-secret", value, StringComparison.Ordinal);
        Assert.DoesNotContain("secret.example", value, StringComparison.Ordinal);
    }

    [Fact]
    public void NetworkRequests_ToString_RedactsCredentialsAndRoutingIdentifiers()
    {
        var credential = new CredentialEnvelope(RealmEndpoint.Parse("https://secret.example/"), "person@example.test", 99, "api-secret-value");
        object[] requests =
        [
            new GetEventsRequest(credential, "queue-secret", 1234, TimeSpan.FromSeconds(90)),
            new DeleteQueueRequest(credential, "queue-secret"),
            new HistoryRequest(credential, new ChannelTopic(42, "topic-secret"), 1234),
            new RegisterRequest(credential, ["event-secret"]),
            new TopicsRequest(credential, 42),
            new MarkReadRequest(credential, new ChannelTopic(42, "topic-secret"), 1234)
        ];

        foreach (var request in requests)
        {
            var value = request.ToString()!;
            Assert.DoesNotContain("secret.example", value, StringComparison.Ordinal);
            Assert.DoesNotContain("person@example.test", value, StringComparison.Ordinal);
            Assert.DoesNotContain("api-secret-value", value, StringComparison.Ordinal);
            Assert.DoesNotContain("queue-secret", value, StringComparison.Ordinal);
            Assert.DoesNotContain("topic-secret", value, StringComparison.Ordinal);
            Assert.DoesNotContain("event-secret", value, StringComparison.Ordinal);
            Assert.DoesNotContain("1234", value, StringComparison.Ordinal);
        }
    }
}
