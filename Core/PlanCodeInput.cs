namespace XIVMit.Core;

public static class PlanCodeInput
{
    public static bool Same(string? a, string? b) =>
        string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Pulls the plan code out of a bare code or a share URL (with or without its scheme).
    /// Anything that isn't URL-shaped is returned trimmed and upper-cased as-is.
    /// </summary>
    public static string Extract(string input)
    {
        var text = input.Trim();
        if (text.Length == 0) return "";
        if (!TryAsUri(text, out var uri)) return text.ToUpperInvariant();

        // ?plan=CODE, also checked in the fragment in case the site moves to hash routing.
        foreach (var part in new[] { uri.Query, uri.Fragment })
        {
            if (QueryValue(part, "plan") is { Length: > 0 } code) return code.ToUpperInvariant();
        }

        // Fall back to the last path segment, for /plans/CODE style links.
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

        // Retry scheme-less pastes ("xivmit.app/?plan=..."), but only when they look like a URL,
        // so a bare code isn't parsed as a hostname.
        if (text.Contains('/') || text.Contains('?'))
            return Uri.TryCreate("https://" + text, UriKind.Absolute, out uri!);

        uri = null!;
        return false;
    }

    private static string? QueryValue(string query, string key)
    {
        // Skip to the first '?', which also steps past a route in a fragment like "#/?plan=CODE".
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
