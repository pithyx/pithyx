namespace Pithyx.Daemon.Realtime;

/// <summary>
/// Topic names are slash-separated segments such as <c>storage/disks</c>. Subscription patterns may use <c>+</c> for
/// exactly one segment and <c>#</c> as the last segment for any number of remaining segments (MQTT semantics).
/// </summary>
public static class TopicPattern
{
    public const int MaxLength = 256;

    /// <summary>True for a well-formed topic name (no wildcards) or pattern (with wildcards when allowed).</summary>
    public static bool IsValid(string value, bool allowWildcards)
    {
        if (string.IsNullOrEmpty(value) || value.Length > MaxLength)
        {
            return false;
        }
        var segments = value.Split('/');
        for (var i = 0; i < segments.Length; i++)
        {
            var segment = segments[i];
            if (segment.Length == 0)
            {
                return false;
            }
            var wildcard = segment is "+" || (segment is "#" && i == segments.Length - 1);
            if (wildcard && !allowWildcards)
            {
                return false;
            }
            if (!wildcard && segment.AsSpan().IndexOfAny('+', '#') >= 0)
            {
                return false;
            }
        }
        return true;
    }

    public static bool HasWildcards(string pattern) => pattern.Contains('+', StringComparison.Ordinal) || pattern.Contains('#', StringComparison.Ordinal);

    /// <summary>Whether a concrete topic matches a pattern.</summary>
    public static bool Matches(string pattern, string topic)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        ArgumentNullException.ThrowIfNull(topic);
        var patternSegments = pattern.Split('/');
        var topicSegments = topic.Split('/');
        for (var i = 0; i < patternSegments.Length; i++)
        {
            if (patternSegments[i] == "#")
            {
                return true;
            }
            if (i >= topicSegments.Length || (patternSegments[i] != "+" && patternSegments[i] != topicSegments[i]))
            {
                return false;
            }
        }
        return patternSegments.Length == topicSegments.Length;
    }
}
