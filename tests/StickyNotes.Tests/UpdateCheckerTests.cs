using System;
using Xunit;

namespace StickyNotes.Tests;

public class UpdateCheckerTests
{
    [Theory]
    [InlineData("v1.2.3", 1, 2, 3)]
    [InlineData("V2.0.0", 2, 0, 0)]
    [InlineData("1.2.3-beta.1", 1, 2, 3)]
    [InlineData("v2.0.0+abc123", 2, 0, 0)]
    [InlineData(" v1.0.1 ", 1, 0, 1)]
    [InlineData("1.4", 1, 4, 0)]
    public void Release_tags_are_read_as_versions(string tag, int major, int minor, int build)
    {
        Assert.Equal(new Version(major, minor, build), UpdateChecker.ParseTag(tag));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("latest")]
    [InlineData("vNext")]
    public void Tags_that_are_not_versions_are_ignored(string? tag)
    {
        Assert.Null(UpdateChecker.ParseTag(tag));
    }

    [Fact]
    public void A_four_part_and_three_part_version_are_the_same_release()
    {
        // System.Version orders 1.0.1 before 1.0.1.0; the checker must not offer an "update" to the same release.
        var current = new Version(1, 0, 1, 0);
        Assert.False(new UpdateInfo(UpdateChecker.ParseTag("v1.0.1")!, UpdateChecker.ParseTag("v1.0.1"), "x").IsNewer);
        Assert.Equal(UpdateChecker.ParseTag("v1.0.1"), UpdateChecker.ParseTag(current.ToString()));
    }

    [Theory]
    [InlineData("1.0.0", "1.0.1", true)]
    [InlineData("1.0.1", "1.1.0", true)]
    [InlineData("1.1.0", "2.0.0", true)]
    [InlineData("1.1.0", "1.1.0", false)]
    [InlineData("1.1.0", "1.0.9", false)]
    public void Only_a_higher_version_counts_as_an_update(string current, string latest, bool expected)
    {
        var info = new UpdateInfo(Version.Parse(current), Version.Parse(latest), UpdateChecker.ReleasesUrl);
        Assert.Equal(expected, info.IsNewer);
    }

    [Fact]
    public void No_published_release_is_never_an_update()
    {
        Assert.False(new UpdateInfo(new Version(1, 1, 0), null, UpdateChecker.ReleasesUrl).IsNewer);
    }

    [Theory]
    [InlineData("https://github.com/JamieAinsworth/StickyNotes/releases/tag/v1.1.0")]
    [InlineData("https://GitHub.com/JamieAinsworth/StickyNotes/releases/tag/v1.1.0")]
    public void A_github_https_release_page_is_opened_as_given(string url)
    {
        Assert.Equal(new Uri(url).AbsoluteUri, UpdateChecker.SafeReleaseUrl(url));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not a url")]
    [InlineData("http://github.com/JamieAinsworth/StickyNotes/releases")]
    [InlineData("https://evil.example.com/github.com/releases")]
    [InlineData("https://github.com.evil.example.com/releases")]
    [InlineData("file:///C:/Windows/System32/calc.exe")]
    [InlineData("javascript:alert(1)")]
    public void Anything_else_falls_back_to_the_known_releases_page(string? url)
    {
        Assert.Equal(UpdateChecker.ReleasesUrl, UpdateChecker.SafeReleaseUrl(url));
    }
}
