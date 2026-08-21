using System.Security.Cryptography;
using System.Text;

namespace CrimeanTom.Campaign.ServiceHost;

public sealed class CampaignMonitorSession
{
    public const string CookieName = "ct_monitor_session";
    private readonly byte[] _token = RandomNumberGenerator.GetBytes(32);

    public string Value => Convert.ToBase64String(_token)
        .TrimEnd('=')
        .Replace('+', '-')
        .Replace('/', '_');

    public bool IsMatch(string? candidate)
    {
        if (string.IsNullOrEmpty(candidate)) return false;
        var bytes = Encoding.UTF8.GetBytes(candidate);
        var expected = Encoding.UTF8.GetBytes(Value);
        return bytes.Length == expected.Length
            && CryptographicOperations.FixedTimeEquals(bytes, expected);
    }
}
