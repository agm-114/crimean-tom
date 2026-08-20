using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;

namespace CrimeanTom.Campaign.ServiceHost;

public sealed class CampaignServiceToken
{
    private readonly byte[] _bytes;

    private CampaignServiceToken(string value)
    {
        Value = value;
        _bytes = Encoding.UTF8.GetBytes(value);
    }

    public string Value { get; }

    public static CampaignServiceToken LoadOrCreate(string path)
    {
        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        if (!File.Exists(fullPath))
        {
            var bytes = RandomNumberGenerator.GetBytes(32);
            var token = Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
            using var stream = CreateTokenFile(fullPath);
            stream.Write(Encoding.UTF8.GetBytes(token));
            stream.Flush(flushToDisk: true);
            RestrictToCurrentUser(fullPath);
        }

        RestrictToCurrentUser(fullPath);
        var value = File.ReadAllText(fullPath, Encoding.UTF8).Trim();
        if (value.Length < 32)
        {
            throw new InvalidDataException("The campaign-service token is invalid.");
        }
        return new CampaignServiceToken(value);
    }

    public bool IsMatch(string? candidate)
    {
        if (string.IsNullOrEmpty(candidate))
        {
            return false;
        }
        var bytes = Encoding.UTF8.GetBytes(candidate);
        return bytes.Length == _bytes.Length && CryptographicOperations.FixedTimeEquals(bytes, _bytes);
    }

    private static FileStream CreateTokenFile(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            return new FileStream(
                path,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                4096,
                FileOptions.WriteThrough);
        }
        return FileSystemAclExtensions.Create(
            new FileInfo(path),
            FileMode.CreateNew,
            FileSystemRights.FullControl,
            FileShare.None,
            4096,
            FileOptions.WriteThrough,
            CreateCurrentUserFileSecurity(CurrentUserSid()));
    }

    private static void RestrictToCurrentUser(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        var user = CurrentUserSid();
        var file = new FileInfo(path);
        if (!user.Equals(file.GetAccessControl(AccessControlSections.Owner).GetOwner(typeof(SecurityIdentifier))))
        {
            throw new UnauthorizedAccessException("The campaign-service token is not owned by the current user.");
        }
        file.SetAccessControl(CreateCurrentUserFileSecurity(user));
    }

    [SupportedOSPlatform("windows")]
    private static SecurityIdentifier CurrentUserSid() =>
        WindowsIdentity.GetCurrent().User
        ?? throw new InvalidOperationException("Could not resolve the current Windows user SID.");

    [SupportedOSPlatform("windows")]
    private static FileSecurity CreateCurrentUserFileSecurity(SecurityIdentifier user)
    {
        var security = new FileSecurity();
        security.SetOwner(user);
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(
            user,
            FileSystemRights.FullControl,
            AccessControlType.Allow));
        return security;
    }
}
