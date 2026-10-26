using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace ToroSquad.Modules.Sor.Domain;

/// <summary>What a remembered note is about. Wire names (the model's "kind") are Turkish and fixed.</summary>
public enum SorMemoryKind
{
    /// <summary>How the member wants to be addressed ("Bana Ada de"). Single-valued: a new statement replaces the old one.</summary>
    Address = 0,

    /// <summary>Short or detailed answers, code language, tone. Single-valued.</summary>
    AnswerStyle = 1,

    Game = 2,
    Hobby = 3,
    Interest = 4,

    /// <summary>An ongoing goal the member stated ("C# öğreniyorum").</summary>
    Goal = 5,

    /// <summary>Any other lasting preference, and notes the member wrote by hand.</summary>
    Preference = 6,
}

/// <summary>Who may see a note being used, decided by the application — never by the model.</summary>
public enum SorMemoryScope
{
    /// <summary>Learned in a private /sor (default there): only the owner's private answers use it.</summary>
    Private = 0,

    /// <summary>
    /// Learned in a channel /sor (default there): the owner's channel answers in that same channel or thread, and the owner's
    /// private answers. Never another member's question.
    /// </summary>
    Channel = 1,

    /// <summary>Shared by the owner explicitly in /hafizam: other members' questions about the owner may use it. Never automatic.</summary>
    Shared = 2,
}

public enum SorMemoryOrigin
{
    /// <summary>Picked up from a question (the member's own statement in that message).</summary>
    Auto = 0,

    /// <summary>The member asked to remember it ("bunu hatırla").</summary>
    Explicit = 1,

    /// <summary>Written or corrected by hand in /hafizam.</summary>
    Manual = 2,
}

public enum SorMemoryOp
{
    Add = 0,
    Update = 1,
    Forget = 2,
}

/// <summary>A memory change the model proposed. Only a proposal: <see cref="SorMemoryRules"/> decides what is kept.</summary>
public sealed record SorMemoryCandidate(SorMemoryOp Op, SorMemoryKind Kind, string Text, string Evidence, string? Target);

public static class SorMemoryKinds
{
    private static readonly (SorMemoryKind Kind, string Wire)[] Names =
    [
        (SorMemoryKind.Address, "hitap"), (SorMemoryKind.AnswerStyle, "cevap_tarzi"), (SorMemoryKind.Game, "oyun"),
        (SorMemoryKind.Hobby, "hobi"), (SorMemoryKind.Interest, "ilgi"), (SorMemoryKind.Goal, "hedef"), (SorMemoryKind.Preference, "tercih"),
    ];

    public static string Wire(SorMemoryKind kind) => Names.First(n => n.Kind == kind).Wire;

    public static bool TryParse(string? wire, out SorMemoryKind kind)
    {
        foreach (var (k, w) in Names)
        {
            if (string.Equals(w, wire?.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                kind = k;
                return true;
            }
        }

        kind = default;
        return false;
    }

    /// <summary>One value at a time: a clear new statement replaces the old note of the same kind.</summary>
    public static bool IsSingleValued(SorMemoryKind kind) => kind is SorMemoryKind.Address or SorMemoryKind.AnswerStyle;

    /// <summary>Always loaded first when the asker's notes are allowed (they shape every answer).</summary>
    public static bool IsStyle(SorMemoryKind kind) => IsSingleValued(kind);
}

/// <summary>Why a proposed or typed note is refused.</summary>
public enum SorMemoryRefusal
{
    None = 0,
    Empty = 1,
    TooLong = 2,

    /// <summary>The evidence is not a quote from the member's current message.</summary>
    NoEvidence = 3,

    /// <summary>A password, token, key, identity/card/bank number, e-mail or phone number: never stored.</summary>
    Secret = 4,

    /// <summary>Health and similar sensitive details: never picked up automatically.</summary>
    Sensitive = 5,

    /// <summary>About someone else (the note names another member the question was about).</summary>
    AboutSomeoneElse = 6,

    /// <summary>The evidence sits in quotation marks or in reported speech ("… dedi"): someone else's words.</summary>
    Quoted = 7,

    /// <summary>The message is hypothetical or role play ("farz et", "diyelim", "rol yap").</summary>
    Hypothetical = 8,

    /// <summary>The evidence is negated but the note is not ("istemiyorum" → "istiyor").</summary>
    Negated = 9,

    /// <summary>A personal fact (game, hobby, interest, goal, preference) without a first-person statement in the evidence.</summary>
    NotSelfStated = 10,

    /// <summary>The evidence shares no content word with the note ("aynen" as proof of "Strateji oyunlarını seviyor").</summary>
    Unsupported = 11,

    /// <summary>The member said it was a joke or asked not to save it ("şakaydı", "kaydetme").</summary>
    NotToBeSaved = 12,
}

/// <summary>
/// The rules for what may be remembered. Pure: text in, decision out. A proposal must quote the member's CURRENT message as
/// evidence (never an earlier turn, never the assistant), stays within the length limit, never carries secrets, and an
/// automatic proposal never carries health-like sensitive details or another member's name. On top of the quote, a few
/// conservative surface checks separate a self-statement from words that merely appear in the message: quotation marks or
/// reported speech in the evidence's sentence, hypothetical/role-play or "joke / don't save" wording anywhere in the
/// message, a negated evidence for an affirmative note, no first-person form for a personal fact, and no shared content
/// word between evidence and note. These are heuristics, not a parser of Turkish: they refuse doubtful proposals (nothing
/// is saved then) and cannot prove a proposal true; the model's own judgement is the first filter.
/// </summary>
public static partial class SorMemoryRules
{
    /// <summary>Normalized for comparisons: lower case (Turkish), single spaces, no surrounding punctuation.</summary>
    public static string Normalize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return "";
        var builder = new StringBuilder(text.Length);
        var space = false;
        foreach (var c in text.Trim().ToLower(CultureInfo.GetCultureInfo("tr-TR")))
        {
            if (char.IsWhiteSpace(c))
            {
                space = builder.Length > 0;
                continue;
            }

            if (space)
                builder.Append(' ');
            space = false;
            builder.Append(c switch { '’' or '‘' or '`' => '\'', '“' or '”' => '"', _ => c });
        }

        return builder.ToString().Trim('.', '!', '?', ',', ';', ':', '"', '\'', ' ');
    }

    /// <summary>One line, no control characters, trimmed.</summary>
    public static string CleanNote(string? text)
    {
        if (text is null)
            return "";
        var builder = new StringBuilder(text.Length);
        foreach (var c in text)
            builder.Append(char.IsControl(c) || c is '\u2028' or '\u2029' ? ' ' : c);
        return Regex.Replace(builder.ToString(), @"\s{2,}", " ").Trim();
    }

    /// <summary>Checks a note's text (any origin): length and secrets.</summary>
    public static SorMemoryRefusal CheckText(string text, int maxLength)
    {
        if (text.Length == 0)
            return SorMemoryRefusal.Empty;
        if (text.Length > maxLength)
            return SorMemoryRefusal.TooLong;
        return ContainsSecret(text) ? SorMemoryRefusal.Secret : SorMemoryRefusal.None;
    }

    /// <summary>
    /// Checks a proposal from the model: the note's own rules, the evidence quote from <paramref name="question"/>, nothing
    /// sensitive, and no name of the other members the question was about (<paramref name="otherNames"/>).
    /// </summary>
    public static SorMemoryRefusal CheckCandidate(SorMemoryCandidate candidate, string question, IReadOnlyCollection<string> otherNames, int maxLength)
    {
        var text = CleanNote(candidate.Text);
        if (candidate.Op != SorMemoryOp.Forget)
        {
            var refusal = CheckText(text, maxLength);
            if (refusal != SorMemoryRefusal.None)
                return refusal;
        }

        var evidence = Normalize(candidate.Evidence);
        if (evidence.Length < 3 || !Normalize(question).Contains(evidence, StringComparison.Ordinal))
            return SorMemoryRefusal.NoEvidence;
        if (ContainsSecret(candidate.Evidence) || ContainsSecret(candidate.Target ?? ""))
            return SorMemoryRefusal.Secret;
        if (candidate.Op != SorMemoryOp.Forget && (IsSensitive(text) || IsSensitive(candidate.Evidence)))
            return SorMemoryRefusal.Sensitive;
        // Any word of another member's name (3+ letters) in the note: it is about them, not about the asker.
        var normalized = Normalize(text);
        foreach (var word in otherNames.SelectMany(n => NameWord().Matches(Normalize(n)).Select(m => m.Value)).Where(w => w.Length >= 3))
        {
            if (Regex.IsMatch(normalized, @"(^|\W)" + Regex.Escape(word) + @"(\W|$)"))
                return SorMemoryRefusal.AboutSomeoneElse;
        }

        return candidate.Op != SorMemoryOp.Forget && SelfStatementRefusal(candidate.Kind, text, evidence, question) is { } notSelf
            ? notSelf
            : SorMemoryRefusal.None;
    }

    /// <summary>
    /// The self-statement checks of an add/update (see the class summary). Style kinds (address, answer style) may be plain
    /// requests to the bot ("kısa cevap ver"), so only they skip the first-person check.
    /// </summary>
    private static SorMemoryRefusal? SelfStatementRefusal(SorMemoryKind kind, string note, string evidence, string question)
    {
        var whole = Normalize(question);
        if (NotToBeSavedWord().IsMatch(whole))
            return SorMemoryRefusal.NotToBeSaved;
        if (HypotheticalWord().IsMatch(whole))
            return SorMemoryRefusal.Hypothetical;
        var sentence = SentenceSplit().Split(question).FirstOrDefault(s => Normalize(s).Contains(evidence, StringComparison.Ordinal)) ?? question;
        if (QuoteMark().IsMatch(sentence) || ReportedSpeech().IsMatch(Normalize(sentence)))
            return SorMemoryRefusal.Quoted;
        var normalizedNote = Normalize(note);
        if (NegatedWord().IsMatch(evidence) && !NegatedWord().IsMatch(normalizedNote))
            return SorMemoryRefusal.Negated;
        var noteWords = ContentWords(normalizedNote);
        if (!ContentWords(evidence).Any(e => noteWords.Any(n => SameWord(e, n))))
            return SorMemoryRefusal.Unsupported;
        return !SorMemoryKinds.IsStyle(kind) && !FirstPerson().IsMatch(evidence) ? SorMemoryRefusal.NotSelfStated : null;
    }

    private static readonly HashSet<string> StopWords = new(StringComparer.Ordinal)
    {
        "ben", "bana", "beni", "benim", "sen", "sana", "bir", "ile", "ama", "çok", "daha", "her", "şey", "gibi", "için", "diye", "artık",
        "lütfen", "bunu", "şunu", "olarak", "the", "and", "for", "you", "are", "was", "not", "with", "this", "that",
    };

    /// <summary>Content words (3+ letters, no common function words) of normalized text.</summary>
    public static IReadOnlyList<string> ContentWords(string normalized) =>
        NameWord().Matches(normalized).Select(m => m.Value).Where(w => w.Length >= 3 && !StopWords.Contains(w)).ToList();

    /// <summary>The same word up to Turkish suffixes: equal, or the same first four letters ("seviyorum" ~ "seviyor").</summary>
    public static bool SameWord(string a, string b) =>
        a == b || (a.Length >= 4 && b.Length >= 4 && string.CompareOrdinal(a, 0, b, 0, 4) == 0);

    /// <summary>Passwords, tokens, keys, identity/card/bank numbers, e-mail addresses and phone numbers.</summary>
    public static bool ContainsSecret(string text) =>
        SecretWord().IsMatch(text) || LongToken().IsMatch(text) || DigitRun().IsMatch(Regex.Replace(text, @"[\s\-.]", "")) ||
        Iban().IsMatch(text.Replace(" ", "", StringComparison.Ordinal)) || Email().IsMatch(text) || Phone().IsMatch(text);

    /// <summary>Health, diagnoses, medication, religion, politics, sexuality and similar details.</summary>
    public static bool IsSensitive(string text) => SensitiveWord().IsMatch(Normalize(text));

    [GeneratedRegex(@"[\p{L}\p{N}]+")]
    private static partial Regex NameWord();

    [GeneratedRegex(@"(?<=[.!?;])\s+|\n")]
    private static partial Regex SentenceSplit();

    /// <summary>Double quotes, guillemets, or a ‘…’ pair (a lone ’ or the ASCII apostrophe is a Turkish suffix mark: "Mert’in").</summary>
    [GeneratedRegex(@"[""“”«»]|‘[^’]*’")]
    private static partial Regex QuoteMark();

    [GeneratedRegex(@"\b(dedi|diyor|demiş|der ki|söyledi|söylüyor|söylemiş|yazdı|yazmış|iddia ediyor|anlattı|said|says|told)\b")]
    private static partial Regex ReportedSpeech();

    [GeneratedRegex(@"\b(farz\w*|varsay\w*|diyelim|sanki|hayal et\w*|rol yap\w*|rolünde|gibi davran\w*|imagine|suppose|pretend|hypothetical\w*)\b")]
    private static partial Regex HypotheticalWord();

    [GeneratedRegex(@"\b(şaka\w*|dalga geç\w*|kaydetme|kaydetmeyin|hatırlama|aklında tutma|just kidding|joking|don't (save|remember))\b")]
    private static partial Regex NotToBeSavedWord();

    [GeneratedRegex(@"\w+m[ıiuü]yor\w*|\w+m[ae]z\b|\w+m[ae]m\b|\bdeğil\w*|\bhiç\b|\bnot\b|\bnever\b|n't\b")]
    private static partial Regex NegatedWord();

    /// <summary>First-person pronouns or Turkish first-person verb/possessive endings ("seviyorum", "oynarım", "hedefim").</summary>
    [GeneratedRegex(@"\b(ben|bana|beni|benim|bende|benden|bence|kendim\w*|i|i'm|my|me)\b|\w(yorum|[ae]r[ıiuü]m|[ıiuü]r[ıiuü]m|d[ıiuü]m|t[ıiuü]m|y[ıiuü]m|ac[ae]ğ[ıi]m|[ıiuü]m)\b")]
    private static partial Regex FirstPerson();

    [GeneratedRegex(@"(?i)(şifre|sifre|parola|password|passwd|api[ _-]?key|token|secret|gizli anahtar|private key|seed phrase|kimlik no|tc no|t\.c\.|kart no|cvv|iban)")]
    private static partial Regex SecretWord();

    /// <summary>A long random-looking token (letters and digits mixed, 24+ characters).</summary>
    [GeneratedRegex(@"(?=[A-Za-z0-9_\-]*\d)(?=[A-Za-z0-9_\-]*[A-Za-z])[A-Za-z0-9_\-]{24,}")]
    private static partial Regex LongToken();

    /// <summary>11+ digits in a row once spaces, dashes and dots are removed (identity, card, account numbers).</summary>
    [GeneratedRegex(@"\d{11,}")]
    private static partial Regex DigitRun();

    [GeneratedRegex(@"(?i)\bTR\d{24}\b")]
    private static partial Regex Iban();

    [GeneratedRegex(@"[^\s@]+@[^\s@]+\.[A-Za-z]{2,}")]
    private static partial Regex Email();

    [GeneratedRegex(@"(\+?\d[\d\s\-()]{9,}\d)")]
    private static partial Regex Phone();

    [GeneratedRegex(@"(sağlık|hastalı|hastayım|teşhis|tanı kondu|ilaç|tedavi|ameliyat|depresyon|anksiyete|panik atak|kanser|diyabet|şeker hastası|hamile|gebelik|psikiyatr|psikolog|terapi|engelli|mezhep|ateist|siyasi görüş|partiliyim|oy verdim|cinsel|eşcinsel|biseksüel|lezbiyen|transgender|adresim|ev adresi|maaşım|borcum|sabıka|health|diagnos|medication|pregnan|religio|sexual)|\b(hasta|gay|trans|dinim|dindar|namaz|oruç)\b")]
    private static partial Regex SensitiveWord();
}
