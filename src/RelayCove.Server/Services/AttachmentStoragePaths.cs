using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using RelayCove.Server.Options;

namespace RelayCove.Server.Services;

public sealed class AttachmentStoragePaths(
    IOptions<StorageOptions> storageOptions,
    IHostEnvironment hostEnvironment)
{
    private const string StagingPrefix = ".upload_";
    private const string StagingSuffix = ".tmp";

    public string UploadsRoot { get; } = ResolveUploadsRoot(
        storageOptions.Value.UploadsPath,
        hostEnvironment.ContentRootPath);

    public void Initialize()
    {
        if (File.Exists(UploadsRoot))
        {
            throw new InvalidOperationException("The configured uploads path is a file.");
        }

        Directory.CreateDirectory(UploadsRoot);
        if ((new DirectoryInfo(UploadsRoot).Attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidOperationException("The configured uploads path cannot be a reparse point.");
        }
    }

    public AttachmentStagedUpload CreateStagedUpload(
        string originalFileName,
        string contentType,
        ILogger logger)
    {
        var attachmentId = Guid.NewGuid();
        var randomSuffix = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        var storedFileName = $"{attachmentId:N}_{randomSuffix}";
        var stagingFileName = $"{StagingPrefix}{storedFileName}{StagingSuffix}";
        return new AttachmentStagedUpload(
            attachmentId,
            originalFileName,
            contentType,
            storedFileName,
            GetManagedPath(stagingFileName),
            GetManagedPath(storedFileName),
            logger);
    }

    public bool IsManagedStagingFileName(string fileName) =>
        fileName.Length == StagingPrefix.Length + Data.Entities.Attachment.StoredFileNameLength + StagingSuffix.Length &&
        fileName.StartsWith(StagingPrefix, StringComparison.Ordinal) &&
        fileName.EndsWith(StagingSuffix, StringComparison.Ordinal) &&
        IsManagedStoredFileName(fileName[StagingPrefix.Length..^StagingSuffix.Length]);

    public static bool IsManagedStoredFileName(string fileName) =>
        fileName.Length == Data.Entities.Attachment.StoredFileNameLength &&
        fileName[32] == '_' &&
        fileName.Where((_, index) => index != 32).All(character =>
            character is (>= '0' and <= '9') or (>= 'a' and <= 'f'));

    private string GetManagedPath(string fileName)
    {
        if (Path.GetFileName(fileName) != fileName)
        {
            throw new InvalidOperationException("Managed attachment names cannot contain path segments.");
        }

        var path = Path.GetFullPath(Path.Combine(UploadsRoot, fileName));
        var rootPrefix = Path.TrimEndingDirectorySeparator(UploadsRoot) + Path.DirectorySeparatorChar;
        if (!path.StartsWith(rootPrefix, PathComparison))
        {
            throw new InvalidOperationException("Managed attachment paths must remain inside the uploads root.");
        }

        return path;
    }

    private static string ResolveUploadsRoot(string configuredPath, string contentRootPath)
    {
        var combined = Path.IsPathFullyQualified(configuredPath)
            ? configuredPath
            : Path.Combine(contentRootPath, configuredPath);
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(combined));
    }

    private static StringComparison PathComparison => OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;
}
