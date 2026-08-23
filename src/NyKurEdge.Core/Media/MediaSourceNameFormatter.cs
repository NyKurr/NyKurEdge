namespace NyKurEdge.Core.Media;

public static class MediaSourceNameFormatter
{
    public static string Format(string? sourceAppId)
    {
        if (string.IsNullOrWhiteSpace(sourceAppId))
        {
            return "WINDOWS MEDIA";
        }

        var source = sourceAppId.Trim();
        var bangIndex = source.LastIndexOf('!');
        var candidate = bangIndex >= 0 && bangIndex < source.Length - 1
            ? source[(bangIndex + 1)..]
            : source[..Math.Max(bangIndex, source.Length)];

        if (candidate.Equals("App", StringComparison.OrdinalIgnoreCase) && bangIndex > 0)
        {
            candidate = source[..bangIndex];
        }

        candidate = candidate.Replace('\\', '/');
        var slashIndex = candidate.LastIndexOf('/');
        if (slashIndex >= 0 && slashIndex < candidate.Length - 1)
        {
            candidate = candidate[(slashIndex + 1)..];
        }

        if (candidate.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            candidate = candidate[..^4];
        }

        var publisherSeparator = candidate.IndexOf('_');
        if (publisherSeparator > 0)
        {
            candidate = candidate[..publisherSeparator];
        }

        var segments = candidate.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        candidate = segments.Length > 0 ? segments[^1] : candidate;

        var knownName = candidate.ToLowerInvariant() switch
        {
            "chrome" => "GOOGLE CHROME",
            "msedge" => "MICROSOFT EDGE",
            "opera" => "OPERA",
            "operagx" => "OPERA GX",
            "spotify" => "SPOTIFY",
            _ => null,
        };

        if (knownName is not null)
        {
            return knownName;
        }

        candidate = candidate
            .Replace('_', ' ')
            .Replace('-', ' ')
            .Trim();
        return string.IsNullOrWhiteSpace(candidate)
            ? "WINDOWS MEDIA"
            : candidate.ToUpperInvariant();
    }
}
