namespace SocialGraph.Api.Infrastructure;

using System.Security.Cryptography;
using System.Text;

internal enum InternalAuthenticationResult
{
    Valid,
    Invalid,
    NotConfigured
}

internal static class InternalCallerAuthentication
{
    public const string ServiceSecretHeaderName = "X-Internal-SocialGraphService-Secret";

    public static InternalAuthenticationResult Validate(IConfiguration configuration, IHeaderDictionary headers)
    {
        var configured = false;
        foreach (var candidate in GetCandidates(configuration))
        {
            if (Encoding.UTF8.GetByteCount(candidate.Secret) < 32)
            {
                continue;
            }

            configured = true;
            if (SecretsMatch(candidate.Secret, headers[candidate.Header].ToString()))
            {
                return InternalAuthenticationResult.Valid;
            }
        }

        return configured ? InternalAuthenticationResult.Invalid : InternalAuthenticationResult.NotConfigured;
    }

    private static IEnumerable<(string Header, string Secret)> GetCandidates(IConfiguration configuration)
    {
        yield return (
            ServiceSecretHeaderName,
            configuration["InternalServices:SocialGraph:SharedSecret"] ?? string.Empty);
        yield return (
            InternalApiAuthenticationMiddleware.SecretHeaderName,
            configuration["Gateway:InternalSharedSecret"] ??
            configuration["InternalServices:SharedSecret"] ??
            string.Empty);
    }

    private static bool SecretsMatch(string expected, string actual)
    {
        var expectedBytes = Encoding.UTF8.GetBytes(expected);
        var actualBytes = Encoding.UTF8.GetBytes(actual);
        return expectedBytes.Length == actualBytes.Length &&
               CryptographicOperations.FixedTimeEquals(expectedBytes, actualBytes);
    }
}

