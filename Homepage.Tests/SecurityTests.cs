using System.Net;
using Homepage.Content;
using Homepage.Themes;
using Homepage.Web.Rendering;
using Microsoft.AspNetCore.Http;

namespace Homepage.Tests;

/// <summary>Regression tests for the issues found in the security review of 0.2.1.</summary>
public class SecurityUnitTests
{
    [Fact]
    public void ContentCannotContainFormsOrOverlays()
    {
        var md = new MarkdownRenderer();
        var html = md.ToHtml("""<form method="post" action="/admin/content/7/delete"><button style="position:fixed;top:0;left:0;width:100vw;height:100vh;opacity:0;z-index:9999">x</button><input name="a"></form> text""");
        Assert.DoesNotContain("<form", html);
        Assert.DoesNotContain("<button", html);
        Assert.DoesNotContain("<input", html);
        Assert.DoesNotContain("position", html);
        Assert.DoesNotContain("z-index", html);
        Assert.Contains("text", html);
        Assert.DoesNotContain("<form", md.SanitizeHtml("<form action='/admin/logout'><button>go</button></form>"));
    }

    [Theory]
    [InlineData(".a{background:url(/\\/evil.example/x.png)}")]
    [InlineData(".a{background:u\\72l(https://evil.example/x.png)}")]
    [InlineData(".a{background:image-set(\"https://evil.example/x.png\" 1x)}")]
    [InlineData(".a{background:-webkit-image-set(url(https://evil.example/x.png) 1x)}")]
    [InlineData("@im@import;port \"https://evil.example/x.css\";")]
    [InlineData("@im/**/port url(https://evil.example/x.css);")]
    [InlineData(".a{background:url( //evil.example/x.png )}")]
    [InlineData(".a{background:url('https://evil.example/x.png')}")]
    public void CustomCssCannotLoadFromOtherSites(string css)
    {
        var clean = ThemeTokens.SanitizeCustomCss(css);
        Assert.DoesNotContain("evil.example", clean.Contains("url(", StringComparison.OrdinalIgnoreCase) || clean.Contains("@import", StringComparison.OrdinalIgnoreCase) ? clean : "", StringComparison.Ordinal);
        Assert.DoesNotContain("image-set(", clean, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("@import", clean, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\\", clean);
        Assert.False(System.Text.RegularExpressions.Regex.IsMatch(clean, @"url\(\s*['""]?\s*(https?:|//)", System.Text.RegularExpressions.RegexOptions.IgnoreCase), clean);
    }

    [Fact]
    public void CustomCssKeepsSitePathsAndDataImages()
    {
        var clean = ThemeTokens.SanitizeCustomCss(".a{background:url(/media/x.png)} .b{background:url(\"data:image/png;base64,AAAA\")} .c{color:red}");
        Assert.Contains("url(/media/x.png)", clean);
        Assert.Contains("data:image/png", clean);
        Assert.Contains("color:red", clean);
    }

    [Theory]
    [InlineData(null, null, false)]
    [InlineData("", null, false)]
    [InlineData("1", null, false)]
    [InlineData("true", null, true)]
    [InlineData("true", "true", false)]
    public void OnlyRealHtmxRequestsGetFragments(string? hxRequest, string? hxBoosted, bool fragment)
    {
        var ctx = new DefaultHttpContext();
        if (hxRequest is not null) ctx.Request.Headers["HX-Request"] = hxRequest;
        if (hxBoosted is not null) ctx.Request.Headers["HX-Boosted"] = hxBoosted;
        Assert.Equal(fragment, Html.IsFragmentRequest(ctx.Request));
    }

    [Fact]
    public void SignInLimiterCountsPerAccountNetworkAndFreesSuccesses()
    {
        var limiter = new SignInLimiter(TimeProvider.System);
        var ip = IPAddress.Parse("203.0.113.5");
        for (var i = 0; i < SignInLimiter.PerAccountAndNetwork; i++) Assert.NotNull(limiter.TryReserve("ADMIN", ip));
        Assert.Null(limiter.TryReserve("ADMIN", ip));                              // this network is done for this account
        Assert.NotNull(limiter.TryReserve("ADMIN", IPAddress.Parse("198.51.100.7"))); // the real admin elsewhere is not locked out
        var ok = limiter.TryReserve("OTHER", ip)!;
        limiter.Succeeded(ok);                                                      // correct passwords do not count
        for (var i = 0; i < 4; i++) Assert.NotNull(limiter.TryReserve("OTHER", ip));
    }

    [Fact]
    public void IPv6ClientsCountPer64()
    {
        Assert.Equal(SignInLimiter.Network(IPAddress.Parse("2001:db8:1:2:aaaa::1")), SignInLimiter.Network(IPAddress.Parse("2001:db8:1:2:ffff::9")));
        Assert.NotEqual(SignInLimiter.Network(IPAddress.Parse("2001:db8:1:2::1")), SignInLimiter.Network(IPAddress.Parse("2001:db8:1:3::1")));
        Assert.Equal("192.0.2.1", SignInLimiter.Network(IPAddress.Parse("::ffff:192.0.2.1")));
    }
}
