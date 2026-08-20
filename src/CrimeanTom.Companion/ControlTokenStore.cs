using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Runtime.Versioning;
using System.Text;

namespace CrimeanTom.Companion;

public sealed class ControlTokenStore
{
    private readonly byte[] _tokenBytes;

    private ControlTokenStore(string token)
    {
        Token = token;
        _tokenBytes = Encoding.UTF8.GetBytes(token);
    }

    public string Token { get; }

    public static ControlTokenStore LoadOrCreate(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("A control token file path is required.", nameof(path));
        }

        path = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException("The control token path has no parent directory.");
        Directory.CreateDirectory(directory);

        string token;
        if (File.Exists(path))
        {
            token = ReadToken(path);
        }
        else
        {
            token = CreateToken();
            try
            {
                using var stream = CreateTokenFile(path);
                using var writer = new StreamWriter(
                    stream,
                    new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                writer.Write(token);
                writer.Flush();
                stream.Flush(flushToDisk: true);
                RestrictToCurrentUser(path);
            }
            catch (IOException) when (File.Exists(path))
            {
                token = ReadToken(path);
            }
        }

        return new ControlTokenStore(token);
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
                bufferSize: 4096,
                FileOptions.WriteThrough);
        }

        var currentUser = GetCurrentUserSid();
        var security = CreateCurrentUserFileSecurity(currentUser);
        return FileSystemAclExtensions.Create(
            new FileInfo(path),
            FileMode.CreateNew,
            FileSystemRights.FullControl,
            FileShare.None,
            bufferSize: 4096,
            FileOptions.WriteThrough,
            security);
    }

    public bool IsMatch(string? candidate)
    {
        if (string.IsNullOrEmpty(candidate))
        {
            return false;
        }

        var candidateBytes = Encoding.UTF8.GetBytes(candidate);
        return candidateBytes.Length == _tokenBytes.Length
            && CryptographicOperations.FixedTimeEquals(candidateBytes, _tokenBytes);
    }

    private static string CreateToken()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        return Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    private static string ReadToken(string path)
    {
        RestrictToCurrentUser(path);
        var token = File.ReadAllText(path, Encoding.UTF8).Trim();
        if (token.Length < 32)
        {
            throw new InvalidDataException(
                $"Control token file '{path}' does not contain a valid token.");
        }

        return token;
    }

    private static void RestrictToCurrentUser(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var currentUser = GetCurrentUserSid();
        var file = new FileInfo(path);
        var existingSecurity = file.GetAccessControl(AccessControlSections.Owner);
        var owner = existingSecurity.GetOwner(typeof(SecurityIdentifier));
        if (!currentUser.Equals(owner))
        {
            throw new UnauthorizedAccessException(
                $"Control token file '{path}' is not owned by the current Windows user.");
        }

        var security = CreateCurrentUserFileSecurity(currentUser);
        new FileInfo(path).SetAccessControl(security);
    }

    [SupportedOSPlatform("windows")]
    private static SecurityIdentifier GetCurrentUserSid()
    {
        return WindowsIdentity.GetCurrent().User
            ?? throw new InvalidOperationException("Could not resolve the current Windows user SID.");
    }

    [SupportedOSPlatform("windows")]
    private static FileSecurity CreateCurrentUserFileSecurity(
        SecurityIdentifier currentUser)
    {
        var security = new FileSecurity();
        security.SetOwner(currentUser);
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(
            currentUser,
            FileSystemRights.FullControl,
            AccessControlType.Allow));
        return security;
    }
}
