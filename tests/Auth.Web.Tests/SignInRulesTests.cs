using System.Globalization;
using Auth.Web;
using Microsoft.AspNetCore.Http;

namespace Auth.Web.Tests;

public sealed class SignInRulesTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 12, 9, 0, 0, TimeSpan.Zero);

    private static string At(DateTimeOffset time) => time.ToString("o", CultureInfo.InvariantCulture);

    [Fact]
    public void A_token_with_more_than_a_minute_left_is_used_as_it_is()
    {
        SignInRules.NeedsRenewal(At(Now.AddMinutes(1).AddSeconds(1)), Now).ShouldBeFalse();
    }

    [Fact]
    public void A_token_with_a_minute_or_less_left_is_renewed()
    {
        SignInRules.NeedsRenewal(At(Now.AddMinutes(1)), Now).ShouldBeTrue();
        SignInRules.NeedsRenewal(At(Now.AddSeconds(5)), Now).ShouldBeTrue();
    }

    [Fact]
    public void An_expired_token_is_renewed()
    {
        SignInRules.NeedsRenewal(At(Now.AddMinutes(-3)), Now).ShouldBeTrue();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("next tuesday")]
    public void A_token_whose_expiry_is_unknown_is_renewed(string? expiresAt)
    {
        SignInRules.NeedsRenewal(expiresAt, Now).ShouldBeTrue();
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/notes")]
    [InlineData("/notes?page=2#top")]
    public void Paths_on_this_site_are_local_return_addresses(string url)
    {
        SignInRules.IsLocalReturnUrl(url).ShouldBeTrue();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("notes")]
    [InlineData("https://evil.example/")]
    [InlineData("//evil.example/")]
    [InlineData("/\\evil.example/")]
    [InlineData("/notes\r\nSet-Cookie: x=1")]
    public void Anything_else_is_not_a_local_return_address(string? url)
    {
        SignInRules.IsLocalReturnUrl(url).ShouldBeFalse();
    }

    [Fact]
    public void A_request_from_the_sites_own_origin_is_same_origin()
    {
        SignInRules.IsSameOrigin(Request(origin: "https://web.example")).ShouldBeTrue();
    }

    [Theory]
    [InlineData("https://evil.example")]
    [InlineData("http://web.example")]
    [InlineData("https://web.example:8443")]
    [InlineData("https://sub.web.example")]
    [InlineData("null")]
    public void A_request_from_another_origin_is_not(string origin)
    {
        SignInRules.IsSameOrigin(Request(origin: origin)).ShouldBeFalse();
    }

    [Fact]
    public void Without_Origin_the_browsers_Sec_Fetch_Site_decides()
    {
        SignInRules.IsSameOrigin(Request(secFetchSite: "same-origin")).ShouldBeTrue();
        SignInRules.IsSameOrigin(Request(secFetchSite: "same-site")).ShouldBeFalse();
        SignInRules.IsSameOrigin(Request(secFetchSite: "cross-site")).ShouldBeFalse();
    }

    [Fact]
    public void Without_Origin_or_Sec_Fetch_Site_a_request_is_not_same_origin()
    {
        SignInRules.IsSameOrigin(Request()).ShouldBeFalse();
    }

    [Fact]
    public void A_browser_navigation_is_a_page_request()
    {
        SignInRules.IsPageNavigation(Request(secFetchMode: "navigate", accept: "*/*")).ShouldBeTrue();
    }

    [Theory]
    [InlineData("cors")]
    [InlineData("same-origin")]
    [InlineData("no-cors")]
    public void A_fetch_from_script_is_not_a_page_request_even_if_it_accepts_html(string mode)
    {
        SignInRules.IsPageNavigation(Request(secFetchMode: mode, accept: "text/html")).ShouldBeFalse();
    }

    [Fact]
    public void Without_Sec_Fetch_Mode_a_request_for_html_is_a_page_request()
    {
        SignInRules.IsPageNavigation(Request(accept: "text/html,application/xhtml+xml,*/*;q=0.8")).ShouldBeTrue();
        SignInRules.IsPageNavigation(Request(accept: "application/json")).ShouldBeFalse();
        SignInRules.IsPageNavigation(Request()).ShouldBeFalse();
    }

    private static HttpRequest Request(string? origin = null, string? secFetchSite = null, string? secFetchMode = null, string? accept = null)
    {
        var context = new DefaultHttpContext();
        context.Request.Scheme = "https";
        context.Request.Host = new HostString("web.example");
        if (origin is not null)
        {
            context.Request.Headers.Origin = origin;
        }

        if (secFetchSite is not null)
        {
            context.Request.Headers["Sec-Fetch-Site"] = secFetchSite;
        }

        if (secFetchMode is not null)
        {
            context.Request.Headers["Sec-Fetch-Mode"] = secFetchMode;
        }

        if (accept is not null)
        {
            context.Request.Headers.Accept = accept;
        }

        return context.Request;
    }
}
