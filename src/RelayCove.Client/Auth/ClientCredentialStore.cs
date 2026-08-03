using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace RelayCove.Client.Auth;

internal sealed class ClientCredentialStore
{
    internal const string CredentialFileName = "relaycove-credential.v1.bin";
    private const string TemporaryFileSuffix = ".tmp";
    private const int SchemaVersion = 1;
    private const int MaximumCiphertextLength = 64 * 1024;
    private const int MaximumPlaintextLength = 32 * 1024;
    private const int MaximumServerBaseUriLength = 2 * 1024;
    private static readonly byte[] OptionalEntropy =
        Encoding.UTF8.GetBytes("RelayCove.Client.CredentialStore.v1");
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        MaxDepth = 8,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    private readonly SemaphoreSlim operationGate = new(1, 1);
    private readonly ILogger<ClientCredentialStore> logger;
    private readonly string credentialPath;
    private readonly string temporaryPath;

    public ClientCredentialStore(
        string rootDirectory,
        ILogger<ClientCredentialStore> logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        if (!Path.IsPathFullyQualified(rootDirectory))
        {
            throw new ArgumentException(
                "Credential root directory must be an absolute path.",
                nameof(rootDirectory));
        }

        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
        RootDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootDirectory));
        credentialPath = ResolveChildPath(RootDirectory, CredentialFileName);
        temporaryPath = ResolveChildPath(RootDirectory, CredentialFileName + TemporaryFileSuffix);
    }

    public string RootDirectory { get; }

    internal string CredentialPath => credentialPath;

    public override string ToString() =>
        $"{nameof(ClientCredentialStore)} {{ RootDirectory = [REDACTED], " +
        "CredentialPath = [REDACTED] }";

    public async Task<bool> SaveAsync(
        Uri serverBaseUri,
        Guid userId,
        string refreshToken,
        CancellationToken cancellationToken = default)
    {
        var canonicalServerBaseUri =
            ClientAuthenticationUri.CanonicalizeServerBaseUri(serverBaseUri);
        if (canonicalServerBaseUri.AbsoluteUri.Length > MaximumServerBaseUriLength)
        {
            throw new ArgumentException(
                "Server base URI exceeds the supported length.",
                nameof(serverBaseUri));
        }

        if (userId == Guid.Empty)
        {
            throw new ArgumentException("User ID must not be empty.", nameof(userId));
        }

        if (!ClientAuthenticationResponseValidator.IsValidRefreshToken(refreshToken))
        {
            throw new ArgumentException(
                "Refresh token is not valid for storage.",
                nameof(refreshToken));
        }

        await operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        byte[]? plaintext = null;
        byte[]? ciphertext = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var payload = new CredentialPayload(
                SchemaVersion,
                canonicalServerBaseUri.AbsoluteUri,
                userId,
                refreshToken);
            plaintext = JsonSerializer.SerializeToUtf8Bytes(payload, JsonOptions);
            if (plaintext.Length > MaximumPlaintextLength)
            {
                throw new ArgumentException(
                    "Credential payload exceeds the supported length.",
                    nameof(refreshToken));
            }

            ciphertext = ProtectedData.Protect(
                plaintext,
                OptionalEntropy,
                DataProtectionScope.CurrentUser);
            if (ciphertext.Length is <= 0 or > MaximumCiphertextLength)
            {
                logger.LogWarning(
                    "Credential store write failed; category={Category}.",
                    "CiphertextLength");
                return false;
            }

            Directory.CreateDirectory(RootDirectory);
            if (!TryDeleteTemporaryFile())
            {
                return false;
            }

            await WriteTemporaryFileAsync(ciphertext, cancellationToken).ConfigureAwait(false);
            PublishTemporaryFile();
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is CryptographicException or PlatformNotSupportedException or
            IOException or UnauthorizedAccessException)
        {
            LogFailure("Write", exception);
            return false;
        }
        finally
        {
            if (plaintext is not null)
            {
                CryptographicOperations.ZeroMemory(plaintext);
            }

            if (ciphertext is not null)
            {
                CryptographicOperations.ZeroMemory(ciphertext);
            }

            _ = TryDeleteTemporaryFile();
            operationGate.Release();
        }
    }

    public async Task<ClientCredentialReadOutcome> LoadAsync(
        CancellationToken cancellationToken = default)
    {
        await operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        byte[]? ciphertext = null;
        byte[]? plaintext = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            await using var stream = new FileStream(
                credentialPath,
                new FileStreamOptions
                {
                    Mode = FileMode.Open,
                    Access = FileAccess.Read,
                    Share = FileShare.Read,
                    Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
                });
            if (stream.Length is <= 0 or > MaximumCiphertextLength)
            {
                LogCorrupt("CiphertextLength");
                return ClientCredentialReadOutcome.Failure(
                    ClientCredentialReadStatus.Corrupt);
            }

            ciphertext = new byte[(int)stream.Length];
            await stream.ReadExactlyAsync(ciphertext, cancellationToken).ConfigureAwait(false);
            plaintext = ProtectedData.Unprotect(
                ciphertext,
                OptionalEntropy,
                DataProtectionScope.CurrentUser);
            if (plaintext.Length is <= 0 or > MaximumPlaintextLength)
            {
                LogCorrupt("PlaintextLength");
                return ClientCredentialReadOutcome.Failure(
                    ClientCredentialReadStatus.Corrupt);
            }

            CredentialPayload? payload;
            try
            {
                payload = JsonSerializer.Deserialize<CredentialPayload>(plaintext, JsonOptions);
            }
            catch (JsonException exception)
            {
                LogFailure("ReadFormat", exception);
                return ClientCredentialReadOutcome.Failure(
                    ClientCredentialReadStatus.Corrupt);
            }

            if (!TryCreateCredential(payload, out var credential))
            {
                LogCorrupt("PayloadValidation");
                return ClientCredentialReadOutcome.Failure(
                    ClientCredentialReadStatus.Corrupt);
            }

            return ClientCredentialReadOutcome.Loaded(credential!);
        }
        catch (FileNotFoundException)
        {
            return ClientCredentialReadOutcome.Failure(
                ClientCredentialReadStatus.NotFound);
        }
        catch (DirectoryNotFoundException)
        {
            return ClientCredentialReadOutcome.Failure(
                ClientCredentialReadStatus.NotFound);
        }
        catch (CryptographicException exception)
        {
            LogFailure("ReadProtection", exception);
            return ClientCredentialReadOutcome.Failure(
                ClientCredentialReadStatus.Corrupt);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is PlatformNotSupportedException or IOException or UnauthorizedAccessException)
        {
            LogFailure("Read", exception);
            return ClientCredentialReadOutcome.Failure(
                ClientCredentialReadStatus.Unavailable);
        }
        finally
        {
            if (plaintext is not null)
            {
                CryptographicOperations.ZeroMemory(plaintext);
            }

            if (ciphertext is not null)
            {
                CryptographicOperations.ZeroMemory(ciphertext);
            }

            operationGate.Release();
        }
    }

    public async Task<bool> ClearAsync(CancellationToken cancellationToken = default)
    {
        await operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            File.Delete(credentialPath);
            return TryDeleteTemporaryFile();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            LogFailure("Clear", exception);
            return false;
        }
        finally
        {
            operationGate.Release();
        }
    }

    private async Task WriteTemporaryFileAsync(
        byte[] ciphertext,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            temporaryPath,
            new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None,
                BufferSize = 4096,
                Options = FileOptions.Asynchronous | FileOptions.WriteThrough,
            });
        await stream.WriteAsync(ciphertext, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private void PublishTemporaryFile()
    {
        if (File.Exists(credentialPath))
        {
            File.Replace(
                temporaryPath,
                credentialPath,
                destinationBackupFileName: null,
                ignoreMetadataErrors: true);
            return;
        }

        File.Move(temporaryPath, credentialPath);
    }

    private static bool TryCreateCredential(
        CredentialPayload? payload,
        out StoredClientCredential? credential)
    {
        credential = null;
        if (payload is null ||
            payload.SchemaVersion != SchemaVersion ||
            payload.UserId == Guid.Empty ||
            string.IsNullOrEmpty(payload.ServerBaseUri) ||
            payload.ServerBaseUri.Length > MaximumServerBaseUriLength ||
            !ClientAuthenticationResponseValidator.IsValidRefreshToken(payload.RefreshToken) ||
            !Uri.TryCreate(payload.ServerBaseUri, UriKind.Absolute, out var parsedUri))
        {
            return false;
        }

        Uri canonicalUri;
        try
        {
            canonicalUri = ClientAuthenticationUri.CanonicalizeServerBaseUri(parsedUri);
        }
        catch (Exception exception) when (exception is ArgumentException or UriFormatException)
        {
            return false;
        }

        if (!string.Equals(
                canonicalUri.AbsoluteUri,
                payload.ServerBaseUri,
                StringComparison.Ordinal))
        {
            return false;
        }

        credential = new StoredClientCredential(
            canonicalUri,
            payload.UserId,
            payload.RefreshToken!);
        return true;
    }

    private static string ResolveChildPath(string rootDirectory, string fileName)
    {
        var childPath = Path.GetFullPath(Path.Combine(rootDirectory, fileName));
        var relativePath = Path.GetRelativePath(rootDirectory, childPath);
        if (Path.IsPathFullyQualified(relativePath) ||
            string.Equals(relativePath, "..", StringComparison.Ordinal) ||
            relativePath.StartsWith(
                ".." + Path.DirectorySeparatorChar,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Resolved credential path escaped its root directory.");
        }

        return childPath;
    }

    private bool TryDeleteTemporaryFile()
    {
        try
        {
            File.Delete(temporaryPath);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(
                "Credential store temporary cleanup failed; errorType={ErrorType}.",
                exception.GetType().Name);
            return false;
        }
    }

    private void LogFailure(string operation, Exception exception)
    {
        logger.LogWarning(
            "Credential store operation failed; operation={Operation}; errorType={ErrorType}.",
            operation,
            exception.GetType().Name);
    }

    private void LogCorrupt(string category)
    {
        logger.LogWarning(
            "Credential store read failed validation; category={Category}.",
            category);
    }

    private sealed class CredentialPayload
    {
        [JsonConstructor]
        public CredentialPayload(
            int schemaVersion,
            string serverBaseUri,
            Guid userId,
            string refreshToken)
        {
            SchemaVersion = schemaVersion;
            ServerBaseUri = serverBaseUri;
            UserId = userId;
            RefreshToken = refreshToken;
        }

        public int SchemaVersion { get; }

        public string ServerBaseUri { get; }

        public Guid UserId { get; }

        public string RefreshToken { get; }

        public override string ToString() =>
            $"{nameof(CredentialPayload)} {{ SchemaVersion = {SchemaVersion}, " +
            "ServerBaseUri = [REDACTED], UserId = [REDACTED], RefreshToken = [REDACTED] }";
    }
}
