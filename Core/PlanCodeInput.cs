namespace XIVMit.Core;

/// <summary>
/// Turns whatever landed in the code box into a plan code. The share URL is what xivmit.app
/// actually puts on the clipboard, so pasting one whole is the normal case rather than the
/// exception: entering "just the code" otherwise means reading it out of the middle of a URL by
/// hand and retyping it.
/// </summary>
public static class PlanCodeInput
{
    /// <summary>
    /// Pulls the plan code out of <paramref name="input"/> - a bare code, a full share URL, or a
    /// URL whose scheme got dropped on the way to the clipboard. Anything not recognisably a URL
    /// comes back trimmed and upper-cased, exactly as a typed code always has: a code that turns
    /// out to be wrong is the server's 404 to report, not something to second-guess here.
    /// </summary>
    public static string Extract(string input)
    {
        var text = input.Trim();
        if (text.Length == 0) return "";
        if (!TryAsUri(text, out var uri)) return text.ToUpperInvariant();

        // ?plan=CODE is the share link's shape. The fragment gets the same treatment because a
        // single-page app that moved to hash routing would carry the same query there instead.
        foreach (var part in new[] { uri.Query, uri.Fragment })
        {
            if (QueryValue(part, "plan") is { Length: > 0 } code) return code.ToUpperInvariant();
        }

        // Otherwise the last non-empty segment, so a /plans/CODE shaped link resolves too rather
        // than failing on a technicality.
        var segments = (uri.AbsolutePath + uri.Fragment)
            .Split(['/', '?', '#', '&'], StringSplitOptions.RemoveEmptyEntries);
        return segments.Length > 0
            ? Uri.UnescapeDataString(segments[^1]).ToUpperInvariant()
            : text.ToUpperInvariant();
    }

    private static bool TryAsUri(string text, out Uri uri)
    {
        if (Uri.TryCreate(text, UriKind.Absolute, out var parsed)
            && (parsed.Scheme == Uri.UriSchemeHttp || parsed.Scheme == Uri.UriSchemeHttps))
        {
            uri = parsed;
            return true;
        }

        // A scheme-less paste ("xivmit.app/?plan=...") is still a URL to whoever pasted it.
        // Only retried when something about the text is URL-shaped, so a bare code never gets
        // https:// bolted onto it and reinterpreted as a hostname.
        if (text.Contains('/') || text.Contains('?'))
            return Uri.TryCreate("https://" + text, UriKind.Absolute, out uri!);

        uri = null!;
        return false;
    }

    private static string? QueryValue(string query, string key)
    {
        // Everything after the first '?' - which for a plain query is the whole thing, and for a
        // hash-routed fragment ("#/?plan=CODE") skips past the route to the query it carries.
        var mark = query.IndexOf('?');
        var pairs = mark >= 0 ? query[(mark + 1)..] : query.TrimStart('#');

        foreach (var pair in pairs.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=');
            if (eq < 0) continue;
            if (!pair.AsSpan(0, eq).Equals(key, StringComparison.OrdinalIgnoreCase)) continue;
            return Uri.UnescapeDataString(pair[(eq + 1)..]).Trim();
        }
        return null;
    }
}
