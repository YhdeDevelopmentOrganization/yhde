using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace YHDE.Server.Accounts;

// One set of rules for every name people type: account names, editor names,
// team names, project names and link labels (security.md).
//
// Clean removes what can't be seen or breaks things: control characters (line
// breaks would split an email subject), invisible and direction-changing
// characters (a right-to-left override can make "gpj.exe" read "exe.jpg"),
// and runs of spaces. Problem then refuses names that pretend to be us or
// carry a link. Names of people and teams reach other people's inboxes in
// invitation emails, so "Your account is locked, visit evil.com" must never
// be a valid name.
public static partial class NameRules
{
    public enum Kind { Person, Team, Project, Label }

    public const int PersonMax = 48;
    public const int TeamMax = 60;
    public const int ProjectMax = 80;
    public const int LabelMax = 80;

    // Compared after folding (lower case, look-alike digits, no spaces or
    // punctuation), so "Y.H.D.E Supp0rt" is caught too.
    private static readonly string[] Reserved =
    [
        "yhde", "admin", "administrator", "support", "staff", "moderator", "mod", "official", "system", "root",
        "security", "billing", "help", "helpdesk", "team", "owner", "server", "anonymous", "everyone", "here",
    ];

    private static readonly string[] FoldedReserved = Reserved.Select(Fold).ToArray();
    private static readonly string FoldedBrand = Fold("yhde");

    public static int Max(Kind kind) => kind switch
    {
        Kind.Person => PersonMax,
        Kind.Team => TeamMax,
        Kind.Project => ProjectMax,
        _ => LabelMax,
    };

    public static string Clean(string? value, Kind kind)
    {
        if (string.IsNullOrEmpty(value)) return "";
        var sb = new StringBuilder(Math.Min(value.Length, 256));
        var space = false;
        foreach (var ch in value.Normalize(NormalizationForm.FormC))
        {
            var cat = char.GetUnicodeCategory(ch);
            // Line breaks and tabs count as spaces, so words stay apart.
            if (char.IsWhiteSpace(ch) || cat is UnicodeCategory.SpaceSeparator or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator)
            {
                space = sb.Length > 0;
                continue;
            }
            if (cat is UnicodeCategory.Control or UnicodeCategory.Format or UnicodeCategory.PrivateUse or UnicodeCategory.OtherNotAssigned)
                continue;
            if (space) sb.Append(' ');
            space = false;
            sb.Append(ch);
        }
        var text = sb.ToString();
        // Cut on whole characters (never half an emoji).
        var max = Max(kind);
        var info = new StringInfo(text);
        if (info.LengthInTextElements > max) text = info.SubstringByTextElements(0, max).TrimEnd();
        return text;
    }

    // Why this name can't be used, or null when it can. Only people and teams
    // are checked for links and reserved words: their names reach others.
    public static string? Problem(string cleaned, Kind kind)
    {
        if (cleaned.Length == 0) return "The name can't be empty.";
        if (kind is Kind.Project or Kind.Label) return null;
        if (LooksLikeLink().IsMatch(cleaned)) return "Names can't contain web addresses or email addresses.";
        if (!cleaned.Any(char.IsLetterOrDigit)) return "Use at least one letter or number.";
        var folded = Fold(cleaned);
        foreach (var word in FoldedReserved)
            if (folded == word) return "That name is reserved. Pick another.";
        if (folded.Contains(FoldedBrand, StringComparison.Ordinal)) return "Names can't include YHDE.";
        return null;
    }

    // An editor's self-chosen name: cleaned, and "Guest" if it pretends to be
    // someone official (editors without an account pick their own name).
    public static string EditorName(string? value)
    {
        var name = Clean(value, Kind.Person);
        return name.Length == 0 ? "Anonymous" : Problem(name, Kind.Person) is null ? name : "Guest";
    }

    private static string Fold(string name)
    {
        var sb = new StringBuilder(name.Length);
        foreach (var ch in name.Normalize(NormalizationForm.FormKD).ToLowerInvariant())
        {
            // Digits and Cyrillic letters that look like Latin ones.
            var c = ch switch
            {
                '0' or 'о' => 'o', '1' or 'l' or 'і' or '|' or '!' => 'i', '3' or 'е' => 'e', '4' or '@' or 'а' => 'a',
                '5' or '$' or 'ѕ' => 's', '7' => 't', 'р' => 'p', 'с' => 'c', 'х' => 'x', 'у' => 'y', 'һ' => 'h', 'ԁ' => 'd',
                _ => ch,
            };
            if (char.IsLetterOrDigit(c)) sb.Append(c);
        }
        return sb.ToString();
    }

    [GeneratedRegex(@"(://|www\.|@|\b[a-z0-9-]{2,}\.(com|net|org|fi|io|gg|xyz|ru|info|app|dev|co|me|link|site|shop|online|top|club|ly|to|tk)\b)", RegexOptions.IgnoreCase)]
    private static partial Regex LooksLikeLink();
}
