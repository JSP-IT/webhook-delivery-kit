using System.Net;
using JspIt.WebhookDelivery.Http;

namespace JspIt.WebhookDelivery.Tests;

public class NetworkAddressPolicyTests
{
    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("127.10.20.30")]
    [InlineData("10.1.2.3")]
    [InlineData("172.16.0.1")]
    [InlineData("172.31.255.255")]
    [InlineData("192.168.1.1")]
    [InlineData("169.254.169.254")] // cloud instance metadata
    [InlineData("100.64.0.1")]
    [InlineData("0.0.0.0")]
    [InlineData("224.0.0.1")]
    [InlineData("255.255.255.255")]
    [InlineData("198.18.0.1")]
    [InlineData("192.0.2.10")]
    [InlineData("::1")]
    [InlineData("::")]
    [InlineData("fe80::1")]
    [InlineData("fd00::1")]
    [InlineData("fc00::1")]
    [InlineData("ff02::1")]
    [InlineData("::ffff:127.0.0.1")] // IPv4-mapped loopback
    [InlineData("::ffff:169.254.169.254")] // IPv4-mapped metadata
    [InlineData("::ffff:10.0.0.1")]
    [InlineData("::127.0.0.1")] // IPv4-compatible
    [InlineData("64:ff9b::a9fe:a9fe")] // NAT64 of 169.254.169.254
    [InlineData("2002:7f00:1::")] // 6to4 embedding 127.0.0.1
    [InlineData("2001:db8::1")]
    [InlineData("fd00:ec2::254")] // AWS IPv6 metadata
    public void Non_public_addresses_are_blocked_by_default(string address) =>
        Assert.False(NetworkAddressPolicy.IsAllowed(IPAddress.Parse(address), allowLoopback: false, []));

    [Theory]
    [InlineData("8.8.8.8")]
    [InlineData("1.1.1.1")]
    [InlineData("172.32.0.1")]
    [InlineData("100.128.0.1")]
    [InlineData("2606:4700:4700::1111")]
    [InlineData("::ffff:8.8.8.8")]
    public void Public_addresses_are_allowed(string address) =>
        Assert.True(NetworkAddressPolicy.IsAllowed(IPAddress.Parse(address), allowLoopback: false, []));

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("::1")]
    [InlineData("::ffff:127.0.0.1")]
    public void Loopback_requires_explicit_opt_in(string address)
    {
        Assert.False(NetworkAddressPolicy.IsAllowed(IPAddress.Parse(address), false, []));
        Assert.True(NetworkAddressPolicy.IsAllowed(IPAddress.Parse(address), true, []));
    }

    [Fact]
    public void Loopback_opt_in_does_not_open_private_or_metadata_ranges()
    {
        Assert.False(NetworkAddressPolicy.IsAllowed(IPAddress.Parse("10.0.0.1"), true, []));
        Assert.False(NetworkAddressPolicy.IsAllowed(IPAddress.Parse("169.254.169.254"), true, []));
    }

    [Fact]
    public void Allowed_private_networks_open_only_that_range()
    {
        IPNetwork[] allowed = [IPNetwork.Parse("10.20.0.0/16")];
        Assert.True(NetworkAddressPolicy.IsAllowed(IPAddress.Parse("10.20.5.6"), false, allowed));
        Assert.True(NetworkAddressPolicy.IsAllowed(IPAddress.Parse("::ffff:10.20.5.6"), false, allowed));
        Assert.False(NetworkAddressPolicy.IsAllowed(IPAddress.Parse("10.21.0.1"), false, allowed));
        Assert.False(NetworkAddressPolicy.IsAllowed(IPAddress.Parse("169.254.169.254"), false, allowed));
    }
}
