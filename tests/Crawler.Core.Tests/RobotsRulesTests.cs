using Crawler.Core.Domain;

namespace Crawler.Core.Tests;

// contracts §7 robots.txt (RFC 9309 group selection and longest-match precedence).
public sealed class RobotsRulesTests
{
    private const string Token = "CrawlerAssignmentBot";

    [Fact]
    public void Wildcard_group_applies_when_no_group_names_our_token()
    {
        var rules = RobotsRules.Parse("User-agent: OtherBot\nDisallow: /\n\nUser-agent: *\nDisallow: /private/\n", Token);

        Assert.Equal("*", rules.Group);
        Assert.Equal(1, rules.RuleCount);
        Assert.False(rules.IsAllowed("/private/x.html"));
        Assert.True(rules.IsAllowed("/public.html"));
    }

    [Theory]
    [InlineData("CrawlerAssignmentBot")]
    [InlineData("crawlerassignmentbot")]
    [InlineData("CRAWLERASSIGNMENTBOT/1.0")]
    public void Group_naming_our_token_wins_over_wildcard_case_insensitively(string agent)
    {
        var rules = RobotsRules.Parse($"User-agent: *\nDisallow: /\n\nUser-agent: {agent}\nDisallow: /mine/\n", Token);

        Assert.Equal(Token, rules.Group);
        Assert.True(rules.IsAllowed("/other.html"));
        Assert.False(rules.IsAllowed("/mine/page.html"));
    }

    [Fact]
    public void Several_user_agent_lines_share_one_group()
    {
        var rules = RobotsRules.Parse("User-agent: OtherBot\nUser-agent: CrawlerAssignmentBot\nDisallow: /shared/\n", Token);

        Assert.Equal(Token, rules.Group);
        Assert.False(rules.IsAllowed("/shared/a"));
    }

    [Fact]
    public void Groups_for_the_same_agent_are_merged()
    {
        var rules = RobotsRules.Parse("User-agent: *\nDisallow: /a/\n\nUser-agent: OtherBot\nDisallow: /b/\n\nUser-agent: *\nDisallow: /c/\n", Token);

        Assert.Equal(2, rules.RuleCount);
        Assert.False(rules.IsAllowed("/a/x"));
        Assert.True(rules.IsAllowed("/b/x"));
        Assert.False(rules.IsAllowed("/c/x"));
    }

    [Fact]
    public void No_matching_group_allows_everything()
    {
        var rules = RobotsRules.Parse("User-agent: OtherBot\nDisallow: /\n", Token);

        Assert.Null(rules.Group);
        Assert.Equal(0, rules.RuleCount);
        Assert.True(rules.IsAllowed("/anything"));
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void Empty_file_allows_everything(string? content)
    {
        Assert.True(RobotsRules.Parse(content, Token).IsAllowed("/"));
    }

    [Fact]
    public void Longest_match_wins_regardless_of_order()
    {
        var rules = RobotsRules.Parse("User-agent: *\nAllow: /a/b\nDisallow: /a\n", Token);

        Assert.False(rules.IsAllowed("/a"));
        Assert.False(rules.IsAllowed("/a/c"));
        Assert.True(rules.IsAllowed("/a/b"));
        Assert.True(rules.IsAllowed("/a/b/c"));
    }

    [Fact]
    public void Longer_disallow_beats_shorter_allow()
    {
        var rules = RobotsRules.Parse("User-agent: *\nAllow: /\nDisallow: /admin\n", Token);

        Assert.True(rules.IsAllowed("/index.html"));
        Assert.False(rules.IsAllowed("/admin/users"));
    }

    [Theory]
    [InlineData("Allow: /page\nDisallow: /page")]
    [InlineData("Disallow: /page\nAllow: /page")]
    [InlineData("Disallow: /pag*\nAllow: /page")]
    public void Allow_wins_ties_of_equal_pattern_length(string body)
    {
        var rules = RobotsRules.Parse($"User-agent: *\n{body}\n", Token);

        Assert.True(rules.IsAllowed("/page"));
    }

    [Theory]
    [InlineData("/*.pdf$", "/docs/file.pdf", false)]
    [InlineData("/*.pdf$", "/file.pdf?download=1", true)]
    [InlineData("/*.pdf$", "/file.pdf.html", true)]
    [InlineData("/*.pdf", "/file.pdf.html", false)]
    [InlineData("/dir/*", "/dir/", false)]
    [InlineData("/dir/*", "/dir/sub/page", false)]
    [InlineData("/dir/*", "/dir", true)]
    [InlineData("/*/private/*", "/x/private/y", false)]
    [InlineData("/*/private/*", "/private/y", true)]
    [InlineData("/a*b*c", "/aXXbYYc", false)]
    [InlineData("/a*b*c", "/aXXcYYb", true)]
    [InlineData("*", "/anything", false)]
    public void Wildcards_match_any_sequence(string pattern, string path, bool allowed)
    {
        var rules = RobotsRules.Parse($"User-agent: *\nDisallow: {pattern}\n", Token);

        Assert.Equal(allowed, rules.IsAllowed(path));
    }

    [Theory]
    [InlineData("/exact$", "/exact", false)]
    [InlineData("/exact$", "/exact/", true)]
    [InlineData("/exact$", "/exactly", true)]
    [InlineData("/$", "/", false)]
    [InlineData("/$", "/index.html", true)]
    public void Dollar_anchors_the_end_of_the_path(string pattern, string path, bool allowed)
    {
        var rules = RobotsRules.Parse($"User-agent: *\nDisallow: {pattern}\n", Token);

        Assert.Equal(allowed, rules.IsAllowed(path));
    }

    [Fact]
    public void Patterns_are_prefix_matches()
    {
        var rules = RobotsRules.Parse("User-agent: *\nDisallow: /tmp\n", Token);

        Assert.False(rules.IsAllowed("/tmp"));
        Assert.False(rules.IsAllowed("/tmp/file"));
        Assert.False(rules.IsAllowed("/tmpfile.html"));
        Assert.True(rules.IsAllowed("/other/tmp"));
    }

    [Fact]
    public void Empty_disallow_allows_everything()
    {
        var rules = RobotsRules.Parse("User-agent: *\nDisallow:\n", Token);

        Assert.Equal("*", rules.Group);
        Assert.Equal(0, rules.RuleCount);
        Assert.True(rules.IsAllowed("/"));
        Assert.True(rules.IsAllowed("/anything"));
    }

    [Fact]
    public void Empty_disallow_in_our_group_overrides_a_restrictive_wildcard_group()
    {
        var rules = RobotsRules.Parse("User-agent: *\nDisallow: /\n\nUser-agent: CrawlerAssignmentBot\nDisallow:\n", Token);

        Assert.Equal(Token, rules.Group);
        Assert.True(rules.IsAllowed("/page"));
    }

    [Fact]
    public void Comments_blank_lines_and_whitespace_are_ignored()
    {
        const string content = """
            # robots.txt for example.com

              User-agent : *   # everyone
            # Disallow: /commented-out

            Disallow:   /private/   # trailing comment
            """;

        var rules = RobotsRules.Parse(content, Token);

        Assert.Equal(1, rules.RuleCount);
        Assert.False(rules.IsAllowed("/private/a"));
        Assert.True(rules.IsAllowed("/commented-out"));
    }

    [Fact]
    public void Crlf_line_endings_and_byte_order_mark_are_handled()
    {
        var rules = RobotsRules.Parse("\uFEFFUser-agent: *\r\nDisallow: /a$\r\nAllow: /a/b\r\n", Token);

        Assert.Equal("*", rules.Group);
        Assert.False(rules.IsAllowed("/a"));
        Assert.True(rules.IsAllowed("/a/"));
    }

    [Fact]
    public void Directive_names_are_case_insensitive()
    {
        var rules = RobotsRules.Parse("USER-AGENT: *\nDISALLOW: /x\nallow: /x/y\n", Token);

        Assert.False(rules.IsAllowed("/x"));
        Assert.True(rules.IsAllowed("/x/y"));
    }

    [Fact]
    public void Unknown_directives_and_sitemap_are_ignored_and_do_not_end_the_group()
    {
        const string content = "Sitemap: https://example.com/sitemap.xml\nUser-agent: *\nCrawl-delay: 10\nDisallow: /a\nHost: example.com\nNoindex: /b\nDisallow: /c\nSitemap: https://example.com/other.xml\n";

        var rules = RobotsRules.Parse(content, Token);

        Assert.Equal(2, rules.RuleCount);
        Assert.False(rules.IsAllowed("/a"));
        Assert.True(rules.IsAllowed("/b"));
        Assert.False(rules.IsAllowed("/c"));
        Assert.True(rules.IsAllowed("/sitemap.xml"));
    }

    [Fact]
    public void Lines_without_a_colon_and_rules_before_any_group_are_ignored()
    {
        var rules = RobotsRules.Parse("Disallow: /orphan\nthis is not a record\nUser-agent: *\nDisallow: /x\n", Token);

        Assert.Equal(1, rules.RuleCount);
        Assert.True(rules.IsAllowed("/orphan"));
        Assert.False(rules.IsAllowed("/x"));
    }

    [Fact]
    public void Query_strings_are_part_of_the_matched_path()
    {
        var rules = RobotsRules.Parse("User-agent: *\nDisallow: /search?\nDisallow: /*?sort=\nAllow: /search?q=public\n", Token);

        Assert.True(rules.IsAllowed("/search"));
        Assert.False(rules.IsAllowed("/search?q=secret"));
        Assert.True(rules.IsAllowed("/search?q=public&page=2"));
        Assert.False(rules.IsAllowed("/list?sort=asc"));
        Assert.True(rules.IsAllowed("/list?page=2"));
    }

    [Fact]
    public void A_user_agent_line_after_rules_starts_a_new_group()
    {
        var rules = RobotsRules.Parse("User-agent: CrawlerAssignmentBot\nDisallow: /mine\nUser-agent: OtherBot\nDisallow: /theirs\n", Token);

        Assert.Equal(1, rules.RuleCount);
        Assert.False(rules.IsAllowed("/mine"));
        Assert.True(rules.IsAllowed("/theirs"));
    }
}
