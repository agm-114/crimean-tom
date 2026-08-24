using System.Reflection;

using CrimeanTom.Protocol;

using SeaPower;

namespace SeaPowerRuntimeBridge;

internal static class RuntimeDeterministicHookFingerprintProvider
{
    private const BindingFlags StaticMembers = BindingFlags.Public
        | BindingFlags.NonPublic
        | BindingFlags.Static;

    public static DeterministicHookCapabilityPayload[] Capture()
    {
        return new[]
        {
            Pin(
                CaptureGroup(
                "logical-time",
                RequiredMethod(typeof(GameTime), "OnStart"),
                RequiredMethod(typeof(GameTime), "OnUpdate"),
                RequiredMethod(typeof(GameTime), "OnFixedUpdate"),
                RequiredMethod(typeof(GameTime), "SetTimeCompression"),
                RequiredMethod(typeof(GameTime), "Pause"),
                RequiredMethod(typeof(GameTime), "UnPause")),
                "dc15ef7e141af00443c385c62384231e673e0a3e149961d117e923f8d70697f9"),
        };
    }

    public static bool IsVerified(string name) => Capture().Any(hook =>
        string.Equals(hook.Name, name, StringComparison.Ordinal)
        && string.Equals(hook.Status, "verified", StringComparison.Ordinal));

    private static DeterministicHookCapabilityPayload Pin(
        DeterministicHookCapabilityPayload value,
        string expectedFingerprint)
    {
        if (string.Equals(value.Status, "inventory-observed", StringComparison.Ordinal))
        {
            value.Status = string.Equals(
                value.MethodFingerprintSha256,
                expectedFingerprint,
                StringComparison.Ordinal)
                    ? "verified"
                    : "fingerprint-mismatch";
        }
        return value;
    }

    internal static DeterministicHookCapabilityPayload CaptureGroup(
        string name,
        params MemberInfo[] members)
    {
        try
        {
            var fields = new List<KeyValuePair<string, string?>>();
            foreach (var member in members.OrderBy(
                member => $"{member.DeclaringType?.FullName}.{member.Name}",
                StringComparer.Ordinal))
            {
                var key = $"{member.DeclaringType?.FullName}.{member.Name}";
                fields.Add(new KeyValuePair<string, string?>(
                    $"{key}.metadata-token",
                    member.MetadataToken.ToString(
                        System.Globalization.CultureInfo.InvariantCulture)));

                switch (member)
                {
                    case MethodBase method:
                        fields.Add(new KeyValuePair<string, string?>(
                            $"{key}.signature",
                            method.ToString()));
                        fields.Add(new KeyValuePair<string, string?>(
                            $"{key}.il",
                            Convert.ToBase64String(
                                method.GetMethodBody()?.GetILAsByteArray()
                                ?? Array.Empty<byte>())));
                        break;
                    case FieldInfo field:
                        fields.Add(new KeyValuePair<string, string?>(
                            $"{key}.field-type",
                            field.FieldType.FullName));
                        break;
                }
            }

            return new DeterministicHookCapabilityPayload
            {
                Name = name,
                Status = "inventory-observed",
                MethodFingerprintSha256 = CapabilityFingerprint.ComputeSha256(fields),
            };
        }
        catch
        {
            return new DeterministicHookCapabilityPayload
            {
                Name = name,
                Status = "inventory-failed",
                MethodFingerprintSha256 = null,
            };
        }
    }

    private static MethodInfo RequiredMethod(Type type, string name)
    {
        return type.GetMethods(StaticMembers | BindingFlags.Instance)
            .Single(method => string.Equals(method.Name, name, StringComparison.Ordinal));
    }

    private static FieldInfo RequiredField(Type type, string name)
    {
        return type.GetField(name, StaticMembers)
            ?? throw new MissingFieldException(type.FullName, name);
    }
}
