using System.Net.Http;
using Microsoft.Extensions.Logging;
using RelayCove.Shared.Auth;

namespace RelayCove.Client.Auth;

internal sealed class PersistentClientAuthentication : IClientPersistentAuthentication
{
    private readonly SemaphoreSlim operationGate = new(1, 1);
    private readonly HttpClient httpClient;
    private readonly ILogger<ClientAuthenticationClient> authenticationLogger;
    private readonly ClientCredentialStore credentialStore;
    private readonly ILogger<PersistentClientAuthentication> logger;
    private readonly TimeProvider timeProvider;
    private ClientAuthenticationSession? activeSession;

    public PersistentClientAuthentication(
        HttpClient httpClient,
        ClientCredentialStore credentialStore,
        ILogger<ClientAuthenticationClient> authenticationLogger,
        ILogger<PersistentClientAuthentication> logger,
        TimeProvider? timeProvider = null)
    {
        this.httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        this.credentialStore = credentialStore ??
            throw new ArgumentNullException(nameof(credentialStore));
        this.authenticationLogger = authenticationLogger ??
            throw new ArgumentNullException(nameof(authenticationLogger));
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    public override string ToString() =>
        $"{nameof(PersistentClientAuthentication)} {{ AuthenticationClient = [REDACTED], " +
        "CredentialStore = [REDACTED] }";

    public async Task<PersistentClientAuthenticationOutcome> LoginAsync(
        Uri serverBaseUri,
        LoginRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(serverBaseUri);
        ArgumentNullException.ThrowIfNull(request);
        await operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (TryGetActiveSessionOutcome("Login", out var activeOutcome))
            {
                return activeOutcome!;
            }

            var authenticationClient = CreateClient(serverBaseUri);
            var result = await authenticationClient
                .LoginAsync(request, cancellationToken)
                .ConfigureAwait(false);
            return await CompleteAsync(result, operation: "Login").ConfigureAwait(false);
        }
        finally
        {
            operationGate.Release();
        }
    }

    public async Task<PersistentClientAuthenticationOutcome> RestoreAsync(
        CancellationToken cancellationToken = default)
    {
        await operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (TryGetActiveSessionOutcome("Restore", out var activeOutcome))
            {
                return activeOutcome!;
            }

            var stored = await credentialStore.LoadAsync(cancellationToken)
                .ConfigureAwait(false);
            if (stored.Status != ClientCredentialReadStatus.Loaded)
            {
                var missingOutcome = PersistentClientAuthenticationOutcome.Failure(
                    stored.Status switch
                    {
                        ClientCredentialReadStatus.NotFound =>
                            PersistentClientAuthenticationStatus.NoStoredCredential,
                        ClientCredentialReadStatus.Corrupt =>
                            PersistentClientAuthenticationStatus.CredentialCorrupt,
                        _ => PersistentClientAuthenticationStatus.CredentialUnavailable,
                    });
                LogCompletion("Restore", missingOutcome);
                return missingOutcome;
            }

            var authenticationClient = CreateClient(stored.Credential!.ServerBaseUri);
            var result = await authenticationClient
                .RestoreAsync(stored.Credential!, cancellationToken)
                .ConfigureAwait(false);
            if (result.Status is ClientLoginStatus.AuthenticationFailed or
                ClientLoginStatus.ProtocolError or
                ClientLoginStatus.StoredIdentityMismatch)
            {
                _ = await credentialStore.ClearAsync(CancellationToken.None)
                    .ConfigureAwait(false);
            }

            return await CompleteAsync(result, operation: "Restore").ConfigureAwait(false);
        }
        finally
        {
            operationGate.Release();
        }
    }

    private async Task<PersistentClientAuthenticationOutcome> CompleteAsync(
        ClientLoginOutcome result,
        string operation)
    {
        PersistentClientAuthenticationOutcome outcome;
        if (result.Status == ClientLoginStatus.Authenticated && result.Session is not null)
        {
            var persisted = await result.Session
                .AttachCredentialStoreAsync(credentialStore)
                .ConfigureAwait(false);
            outcome = PersistentClientAuthenticationOutcome.Authenticated(
                result.Session,
                persisted);
            activeSession = result.Session;
        }
        else
        {
            outcome = PersistentClientAuthenticationOutcome.Failure(
                MapStatus(result.Status),
                result.RetryAfter);
        }

        LogCompletion(operation, outcome);
        return outcome;
    }

    private void LogCompletion(
        string operation,
        PersistentClientAuthenticationOutcome outcome)
    {
        logger.LogInformation(
            "Client authentication completed; operation={Operation}; status={Status}; " +
            "credentialPersisted={CredentialPersisted}.",
            operation,
            outcome.Status,
            outcome.IsCredentialPersisted);
    }

    private ClientAuthenticationClient CreateClient(Uri serverBaseUri) =>
        new(
            serverBaseUri,
            httpClient,
            authenticationLogger,
            timeProvider);

    private bool TryGetActiveSessionOutcome(
        string operation,
        out PersistentClientAuthenticationOutcome? outcome)
    {
        if (activeSession?.IsDisposeCompleted == true)
        {
            activeSession = null;
        }

        if (activeSession is null)
        {
            outcome = null;
            return false;
        }

        outcome = PersistentClientAuthenticationOutcome.Failure(
            PersistentClientAuthenticationStatus.SessionAlreadyActive);
        LogCompletion(operation, outcome);
        return true;
    }

    private static PersistentClientAuthenticationStatus MapStatus(ClientLoginStatus status) =>
        status switch
        {
            ClientLoginStatus.ValidationFailed =>
                PersistentClientAuthenticationStatus.ValidationFailed,
            ClientLoginStatus.AuthenticationFailed =>
                PersistentClientAuthenticationStatus.AuthenticationFailed,
            ClientLoginStatus.RateLimited =>
                PersistentClientAuthenticationStatus.RateLimited,
            ClientLoginStatus.ServiceUnavailable =>
                PersistentClientAuthenticationStatus.ServiceUnavailable,
            ClientLoginStatus.ProtocolError or ClientLoginStatus.StoredIdentityMismatch =>
                PersistentClientAuthenticationStatus.ProtocolError,
            _ => PersistentClientAuthenticationStatus.RemoteFailure,
        };
}
