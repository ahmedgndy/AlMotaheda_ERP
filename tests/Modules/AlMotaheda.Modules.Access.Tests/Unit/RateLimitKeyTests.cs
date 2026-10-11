using System.Net;
using AlMotaheda.Api.Infrastructure.RateLimiting;

namespace AlMotaheda.Modules.Access.Tests.Unit;

public sealed class RateLimitKeyTests
{
    [Theory]
    [InlineData("203.0.113.7", "203.0.113.7")]
    [InlineData("::ffff:203.0.113.7", "203.0.113.7")]
    [InlineData("2001:db8:1:2:aaaa:bbbb:cccc:dddd", "2001:db8:1:2::/64")]
    [InlineData("2001:db8:1:2::1", "2001:db8:1:2::/64")]
    public void Clients_are_counted_by_address_or_IPv6_network(string address, string expected)
    {
        Assert.Equal(expected, RateLimitingSetup.ClientKey(IPAddress.Parse(address)));
    }
}
