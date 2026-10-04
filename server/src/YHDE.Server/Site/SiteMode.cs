namespace YHDE.Server.Site;

// Who runs this server and what its website is (setup_your_server.md).
//
// YHDE's own server is the official site: the marketing pages, plans and
// prices, early access, and YHDE Development Organization's legal pages.
// Every other server is self-hosted (the default): its pages show the
// operator's name and contact instead, plans and prices are gone (the
// operator pays for the disk, so a "Self-hosted" plan without limits applies),
// search engines are asked to stay away, and a "Powered by YHDE" credit links
// to YHDE.
//
// Settings: Yhde:Site:Official (true only on YHDE's own servers),
// Yhde:Operator:Name, Yhde:Operator:Email, Yhde:Operator:PrivacyUrl,
// Yhde:Operator:TermsUrl.
public sealed record SiteOperator(string Name, string Email, string PrivacyUrl, string TermsUrl);

public static class SiteMode
{
    // YHDE's own site, for the "Powered by YHDE" credit.
    public const string OfficialUrl = "https://yhde.frostinteractive.fi";

    public static bool Official { get; private set; } = true;
    public static SiteOperator Operator { get; private set; } = new("", "", "", "");

    public static void Configure(IConfiguration configuration) => (Official, Operator) = Parse(configuration);

    public static (bool Official, SiteOperator Operator) Parse(IConfiguration configuration) => (
        configuration.GetValue("Yhde:Site:Official", false),
        new SiteOperator(
            YHDE.Server.Accounts.NameRules.Clean(configuration["Yhde:Operator:Name"], YHDE.Server.Accounts.NameRules.Kind.Label),
            Email(configuration["Yhde:Operator:Email"]),
            HttpsUrl(configuration["Yhde:Operator:PrivacyUrl"]),
            HttpsUrl(configuration["Yhde:Operator:TermsUrl"])));

    // What every page is told about the site (the "site-mode" data block).
    public static object PageInfo() => new
    {
        official = Official,
        @operator = Official ? null : Operator,
        poweredBy = OfficialUrl,
    };

    private static string Email(string? value)
    {
        var s = (value ?? "").Trim();
        return s.Length is > 2 and <= 254 && s.Contains('@') && !s.Any(c => char.IsWhiteSpace(c) || char.IsControl(c) || c is '<' or '>' or '"')
            ? s
            : "";
    }

    // Only absolute http(s) links: never javascript: or data: in a page.
    private static string HttpsUrl(string? value)
    {
        var s = (value ?? "").Trim();
        return Uri.TryCreate(s, UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttps || u.Scheme == Uri.UriSchemeHttp) && s.Length <= 500
            ? u.AbsoluteUri
            : "";
    }
}
