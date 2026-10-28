using Crawler.Core.Domain;

namespace Crawler.Core.Tests;

// Expected values come from docs/test-plan.md section 1 (N1–N20).
public sealed class UrlNormalizerTests
{
    private static readonly Uri Base = new("https://example.com/dir/page.html");

    [Theory]
    [InlineData("about.html", "https://example.com/dir/about.html")] // N1
    [InlineData("../up.html", "https://example.com/up.html")] // N2
    [InlineData("/root.html", "https://example.com/root.html")] // N3
    [InlineData("https://other.org/x", "https://other.org/x")] // N4
    [InlineData("page.html#section", "https://example.com/dir/page.html")] // N5
    [InlineData("#top", "https://example.com/dir/page.html")] // N6
    [InlineData("HTTPS://EXAMPLE.COM/Path/File", "https://example.com/Path/File")] // N7
    [InlineData("https://example.com:443/a", "https://example.com/a")] // N8
    [InlineData("http://example.com:80/a", "http://example.com/a")] // N9
    [InlineData("https://example.com:8443/a", "https://example.com:8443/a")] // N10
    [InlineData("/search?q=a&b=2", "https://example.com/search?q=a&b=2")] // N11
    [InlineData("/b?x=2&a=1", "https://example.com/b?x=2&a=1")] // N12
    [InlineData("https://example.com", "https://example.com/")] // N16
    [InlineData("//other.org/p", "https://other.org/p")] // N19
    [InlineData("/path with space", "https://example.com/path%20with%20space")] // N20
    public void Normalizes_relative_and_absolute_hrefs(string href, string expected)
    {
        var ok = UrlNormalizer.TryNormalize(href, Base, out var normalized);

        Assert.True(ok);
        Assert.Equal(expected, normalized!.AbsoluteUri);
    }

    [Fact] // N13
    public void Trailing_slash_is_preserved_as_a_distinct_resource()
    {
        Assert.True(UrlNormalizer.TryNormalize("/dir/", Base, out var withSlash));
        Assert.True(UrlNormalizer.TryNormalize("/dir", Base, out var withoutSlash));

        Assert.Equal("https://example.com/dir/", withSlash!.AbsoluteUri);
        Assert.Equal("https://example.com/dir", withoutSlash!.AbsoluteUri);
        Assert.NotEqual(withSlash.AbsoluteUri, withoutSlash.AbsoluteUri);
    }

    [Theory] // N14, N15
    [InlineData("mailto:a@b.com")]
    [InlineData("tel:+123")]
    [InlineData("javascript:void(0)")]
    [InlineData("ftp://x/y")]
    [InlineData("data:text/plain,x")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Rejects_unsupported_schemes_and_empty_hrefs(string? href)
    {
        var ok = UrlNormalizer.TryNormalize(href, Base, out var normalized);

        Assert.False(ok);
        Assert.Null(normalized);
    }

    [Fact] // N18
    public void Equivalent_spellings_share_one_normalized_key()
    {
        var variants = new[] { "a.html", "./a.html", "a.html#x", "https://EXAMPLE.com/dir/a.html" };

        var keys = variants
            .Select(v => UrlNormalizer.TryNormalize(v, Base, out var u) ? u!.AbsoluteUri : null)
            .Distinct()
            .ToList();

        Assert.Equal(["https://example.com/dir/a.html"], keys);
    }

    [Fact]
    public void Absolute_input_without_base_is_normalized()
    {
        var ok = UrlNormalizer.TryNormalize("HTTP://Example.COM:80/A?b=1#c", null, out var normalized);

        Assert.True(ok);
        Assert.Equal("http://example.com/A?b=1", normalized!.AbsoluteUri);
    }

    [Theory]
    [InlineData("notaurl")]
    [InlineData("example.com/path")]
    public void Relative_input_without_base_is_rejected(string href)
    {
        Assert.False(UrlNormalizer.TryNormalize(href, null, out _));
    }

    [Fact]
    public void Embedded_credentials_are_stripped_and_do_not_split_the_dedup_key()
    {
        Assert.True(UrlNormalizer.TryNormalize("https://user:secret@example.com/a", null, out var withCredentials));
        Assert.True(UrlNormalizer.TryNormalize("https://example.com/a", null, out var plain));

        Assert.Equal("https://example.com/a", withCredentials!.AbsoluteUri);
        Assert.Equal(plain!.AbsoluteUri, withCredentials.AbsoluteUri);
        Assert.DoesNotContain("secret", withCredentials.AbsoluteUri);
    }
}
