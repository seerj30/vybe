using System.Net;
using Vybe.Api.Infrastructure;

namespace Vybe.Api.Tests;

/// <summary>Guest-creation rate-limit partitions (spec §5: createGuest is rate limited per client).</summary>
public sealed class ClientAddressTests
{
    [Theory]
    [InlineData("203.0.113.7", "203.0.113.7")]
    [InlineData("::ffff:203.0.113.7", "203.0.113.7")]
    [InlineData("2001:db8:1:2:aaaa:bbbb:cccc:dddd", "2001:db8:1:2::/64")]
    [InlineData("2001:db8:1:2::1", "2001:db8:1:2::/64")]
    [InlineData("::1", "::/64")]
    public void RateLimitPartition_GroupsIpv6ByItsSlash64(string address, string partition) =>
        Assert.Equal(partition, ClientAddress.RateLimitPartition(IPAddress.Parse(address)));

    [Fact]
    public void RateLimitPartition_TwoAddressesInOneSlash64_ShareAPartition() =>
        Assert.Equal(
            ClientAddress.RateLimitPartition(IPAddress.Parse("2001:db8:abcd:12::1")),
            ClientAddress.RateLimitPartition(IPAddress.Parse("2001:db8:abcd:12:ffff:ffff:ffff:fffe")));

    [Fact]
    public void RateLimitPartition_WithoutAddress_UsesOneSharedPartition() =>
        Assert.Equal("unknown", ClientAddress.RateLimitPartition(null));
}
