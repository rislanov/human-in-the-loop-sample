using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace HumanLoopBooking.Services;

public static class SecurityHelpers
{
    public static string NewToken(string prefix)
    {
        Span<byte> bytes = stackalloc byte[32];
        RandomNumberGenerator.Fill(bytes);
        var token = Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

        return $"{prefix}_{token}";
    }

    public static string Hash(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    public static string? HashIp(IPAddress? address)
    {
        return address is null ? null : Hash(address.ToString());
    }

    public static string? HashSubnet(IPAddress? address)
    {
        if (address is null)
        {
            return null;
        }

        var bytes = address.GetAddressBytes();
        if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && bytes.Length == 4)
        {
            return Hash($"{bytes[0]}.{bytes[1]}.{bytes[2]}.0/24");
        }

        if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 && bytes.Length == 16)
        {
            return Hash(Convert.ToHexString(bytes[..8]) + "::/64");
        }

        return Hash(address.ToString());
    }

    public static string? HashDevice(BrowserSignals? browser)
    {
        if (browser is null)
        {
            return null;
        }

        var material = string.Join('|',
            browser.UserAgent ?? "",
            browser.Platform ?? "",
            browser.LanguagesLength,
            browser.PluginsLength,
            browser.TouchCapable);

        return Hash(material);
    }
}
