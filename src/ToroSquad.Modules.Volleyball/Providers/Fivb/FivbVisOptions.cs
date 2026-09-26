namespace ToroSquad.Modules.Volleyball.Providers.Fivb;

/// <summary>
/// Section "Volleyball:Fivb". FIVB VIS web service (www.fivb.org/Vis2009/XmlRequest.asmx): official, public data is
/// readable anonymously; FIVB asks clients to identify themselves with an application id (X-FIVB-App-ID header, obtained
/// from FIVB by e-mail). No rate limit is published, so TSQ sets its own conservative budget.
/// Research and verification: docs/volleyball/PROVIDER_RESEARCH.md.
/// </summary>
public sealed class FivbVisOptions
{
    public string BaseUrl { get; set; } = "https://www.fivb.org/Vis2009/";

    /// <summary>Optional FIVB application id (sent as X-FIVB-App-ID; never logged). Empty = anonymous public access.</summary>
    public string? AppId { get; set; }

    public string UserAgent { get; set; } = "TSQBot/0.1 (+https://github.com/Torokal/TSQ-Bot)";

    /// <summary>Self-imposed budget (FIVB publishes none). Normal use is ~1 request/minute during a match, a few per day otherwise.</summary>
    public int RequestsPerMinute { get; set; } = 10;

    public int TimeoutSeconds { get; set; } = 30;

    public int MaxRetries { get; set; } = 2;

    /// <summary>Every Nth live poll is a full (non-incremental) request, so a missed "Version" change can never persist.</summary>
    public int FullLiveRefreshEvery { get; set; } = 5;

    public string? Problem()
    {
        if (!Uri.TryCreate(BaseUrl, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            return "Volleyball:Fivb:BaseUrl must be an absolute https URL";
        if (RequestsPerMinute is < 1 or > 60)
            return "Volleyball:Fivb:RequestsPerMinute must be 1..60";
        if (TimeoutSeconds is < 5 or > 120)
            return "Volleyball:Fivb:TimeoutSeconds must be 5..120";
        if (MaxRetries is < 0 or > 5)
            return "Volleyball:Fivb:MaxRetries must be 0..5";
        if (FullLiveRefreshEvery is < 1 or > 60)
            return "Volleyball:Fivb:FullLiveRefreshEvery must be 1..60";
        return null;
    }
}
