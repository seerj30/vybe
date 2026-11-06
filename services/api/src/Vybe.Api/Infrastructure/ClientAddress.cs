using System.Net;
using System.Net.Sockets;

namespace Vybe.Api.Infrastructure;

/// <summary>Client address keys for per-address rate limits.</summary>
public static class ClientAddress
{
    /// <summary>
    /// Rate-limit partition of a client address. An IPv6 client usually owns a whole /64, so the limit
    /// applies per /64; per address, rotating addresses inside the prefix would bypass it.
    /// IPv4-mapped IPv6 addresses count as their IPv4 address.
    /// </summary>
    public static string RateLimitPartition(IPAddress? address)
    {
        if (address is null)
        {
            return "unknown";
        }

        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (address.AddressFamily != AddressFamily.InterNetworkV6)
        {
            return address.ToString();
        }

        var bytes = address.GetAddressBytes();
        Array.Clear(bytes, 8, 8);
        return new IPAddress(bytes) + "/64";
    }
}
