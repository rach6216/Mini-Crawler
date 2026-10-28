using System.Net;
using Crawler.Core.Domain;

namespace Crawler.Core.Tests;

public sealed class UrlSafetyTests
{
    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("10.0.0.1")]
    [InlineData("172.16.5.4")]
    [InlineData("172.31.255.255")]
    [InlineData("192.168.1.1")]
    [InlineData("169.254.169.254")]
    [InlineData("100.64.0.1")]
    [InlineData("0.0.0.0")]
    [InlineData("224.0.0.1")]
    [InlineData("255.255.255.255")]
    [InlineData("::1")]
    [InlineData("::")]
    [InlineData("fe80::1")]
    [InlineData("fc00::1")]
    [InlineData("fd12:3456::1")]
    [InlineData("ff02::1")]
    [InlineData("::ffff:127.0.0.1")]
    [InlineData("::ffff:10.1.2.3")]
    [InlineData("2001:db8::1")]
    [InlineData("64:ff9b::a00:1")]
    [InlineData("64:ff9b:1::1")]
    [InlineData("::10.0.0.1")]
    [InlineData("2002:0a00:0001::1")] // 6to4 embedding 10.0.0.1
    [InlineData("2002:7f00:0001::1")] // 6to4 embedding 127.0.0.1
    [InlineData("2001:10::1")]
    [InlineData("2001:2::1")]
    [InlineData("100::1")]
    public void Non_public_addresses_are_rejected(string ip)
    {
        Assert.False(UrlSafety.IsPubliclyRoutable(IPAddress.Parse(ip)));
    }

    [Theory]
    [InlineData("8.8.8.8")]
    [InlineData("1.1.1.1")]
    [InlineData("172.32.0.1")]
    [InlineData("100.128.0.1")]
    [InlineData("2606:4700:4700::1111")]
    [InlineData("2a00:1450:4001:80b::200e")]
    [InlineData("2002:0808:0808::1")] // 6to4 embedding 8.8.8.8
    public void Public_addresses_are_accepted(string ip)
    {
        Assert.True(UrlSafety.IsPubliclyRoutable(IPAddress.Parse(ip)));
    }

    [Theory]
    [InlineData("localhost")]
    [InlineData("api.localhost")]
    [InlineData("127.0.0.1")]
    [InlineData("[::1]")]
    [InlineData("10.0.0.5")]
    [InlineData("")]
    public void Unsafe_hosts_are_rejected_without_dns(string host)
    {
        Assert.False(UrlSafety.IsHostAllowedWithoutDns(host, []));
    }

    [Theory]
    [InlineData("example.com")]
    [InlineData("sub.example.org")]
    [InlineData("8.8.8.8")]
    public void Public_hosts_pass_without_dns(string host)
    {
        Assert.True(UrlSafety.IsHostAllowedWithoutDns(host, []));
    }

    [Fact]
    public void Trusted_hosts_bypass_the_check()
    {
        Assert.True(UrlSafety.IsHostAllowedWithoutDns("127.0.0.1", ["127.0.0.1"]));
        Assert.True(UrlSafety.IsHostAllowedWithoutDns("fixture-site", ["Fixture-Site"]));
        Assert.False(UrlSafety.IsHostAllowedWithoutDns("localhost", ["fixture-site"]));
    }
}
