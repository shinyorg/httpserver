namespace Shiny.Net.HttpServer.WebDav.Internal;

/// <summary>One assertion: a lock token the client claims to hold, or an entity tag it expects.</summary>
/// <param name="Negated">The condition was written <c>Not</c>.</param>
/// <param name="Token">A lock token, or null when this condition is about an entity tag.</param>
/// <param name="ETag">An entity tag, or null when this condition is about a lock token.</param>
sealed record IfCondition(bool Negated, string? Token, string? ETag);

/// <summary>A parenthesised list of conditions, all of which must hold.</summary>
/// <param name="ResourceTag">
/// The URL the list is about, when the client tagged it. Null for an untagged list, which is about
/// the request's own resource.
/// </param>
/// <param name="Conditions">Every condition in the list. Combined with AND.</param>
sealed record IfList(string? ResourceTag, IReadOnlyList<IfCondition> Conditions);

/// <summary>
/// The <c>If</c> header of RFC 4918 §10.4 — how a client proves it holds a lock, and how it makes a
/// write conditional on an entity tag.
/// <para>
/// Two jobs at once, which is why it is not just a precondition. <see cref="Tokens"/> is what
/// unlocks a locked resource for this request; <see cref="Evaluate"/> is what decides 412.
/// </para>
/// </summary>
sealed class IfHeader
{
    IfHeader(IReadOnlyList<IfList> lists, IReadOnlyList<string> tokens)
    {
        this.Lists = lists;
        this.Tokens = tokens;
    }

    public IReadOnlyList<IfList> Lists { get; }

    /// <summary>
    /// Every lock token the header mentions, negated or not.
    /// <para>
    /// Deliberately every one rather than only the tokens in lists that held: RFC 4918 §9.9.6 talks
    /// about tokens being *submitted*, and a client that names a token has shown it holds it.
    /// Whether the surrounding condition was true is a separate question, answered by
    /// <see cref="Evaluate"/>.
    /// </para>
    /// </summary>
    public IReadOnlyList<string> Tokens { get; }

    public static readonly IReadOnlyList<string> NoTokens = Array.Empty<string>();

    /// <summary>Parses the header. False when it is syntactically malformed, which is a 400.</summary>
    public static bool TryParse(string? header, out IfHeader? result)
    {
        result = null;

        if (header is null)
            return true;

        var text = header;
        var lists = new List<IfList>();
        var tokens = new List<string>();
        string? tag = null;
        var index = 0;

        while (true)
        {
            SkipWhitespace(text, ref index);

            if (index >= text.Length)
                break;

            switch (text[index])
            {
                // A '<' out here opens a Resource-Tag, which retags every list that follows until
                // the next one. Inside a list the same character opens a state token.
                case '<':
                {
                    var end = text.IndexOf('>', index + 1);
                    if (end < 0)
                        return false;

                    tag = text[(index + 1)..end];
                    index = end + 1;
                    break;
                }

                case '(':
                {
                    if (!TryParseList(text, ref index, tokens, out var conditions))
                        return false;

                    lists.Add(new IfList(tag, conditions));
                    break;
                }

                default:
                    return false;
            }
        }

        if (lists.Count == 0)
            return false;

        result = new IfHeader(lists, tokens);
        return true;
    }

    static bool TryParseList(string text, ref int index, List<string> tokens, out IReadOnlyList<IfCondition> result)
    {
        result = Array.Empty<IfCondition>();

        // Steps over the '(' the caller matched.
        index++;

        var conditions = new List<IfCondition>();

        while (true)
        {
            SkipWhitespace(text, ref index);

            if (index >= text.Length)
                return false;

            if (text[index] == ')')
            {
                index++;
                break;
            }

            var negated = false;

            if (index + 3 <= text.Length && text.AsSpan(index, 3).Equals("Not", StringComparison.OrdinalIgnoreCase))
            {
                negated = true;
                index += 3;
                SkipWhitespace(text, ref index);
            }

            if (index >= text.Length)
                return false;

            if (text[index] == '<')
            {
                var end = text.IndexOf('>', index + 1);
                if (end < 0)
                    return false;

                var token = text[(index + 1)..end];
                conditions.Add(new IfCondition(negated, token, null));

                if (!tokens.Contains(token, StringComparer.Ordinal))
                    tokens.Add(token);

                index = end + 1;
            }
            else if (text[index] == '[')
            {
                var end = text.IndexOf(']', index + 1);
                if (end < 0)
                    return false;

                conditions.Add(new IfCondition(negated, null, text[(index + 1)..end].Trim()));
                index = end + 1;
            }
            else
            {
                return false;
            }
        }

        if (conditions.Count == 0)
            return false;

        result = conditions;
        return true;
    }

    /// <summary>
    /// Decides whether the header holds. True when any one of its lists does — a client offers
    /// several because it does not know which of them the server will find true.
    /// </summary>
    /// <param name="defaultPath">The resource an untagged list is about.</param>
    /// <param name="resolveTag">
    /// Maps a tagged URL onto a path in this mount, or returns null for one that is not in it.
    /// </param>
    /// <param name="stateOf">The lock tokens in force on a path, and its current entity tag.</param>
    public bool Evaluate(
        string defaultPath,
        Func<string, string?> resolveTag,
        Func<string, (IReadOnlyList<string> Tokens, string? ETag)> stateOf
    )
    {
        foreach (var list in this.Lists)
        {
            var path = defaultPath;

            if (list.ResourceTag is { } tagged)
            {
                var resolved = resolveTag(tagged);

                // A list tagged with a URL this mount does not serve cannot be judged, so it does
                // not get to be the one that makes the header true.
                if (resolved is null)
                    continue;

                path = resolved;
            }

            var state = stateOf(path);
            var holds = true;

            foreach (var condition in list.Conditions)
            {
                var matched = condition.Token is { } token
                    ? state.Tokens.Contains(token, StringComparer.Ordinal)
                    : state.ETag is { } etag && ETagsMatch(etag, condition.ETag!);

                if (matched == condition.Negated)
                {
                    holds = false;
                    break;
                }
            }

            if (holds)
                return true;
        }

        return false;
    }

    /// <summary>
    /// Compares entity tags. Strong comparison, as RFC 4918 requires — but the <c>W/</c> prefix is
    /// stripped from the candidate first, because clients echo back tags they were given and some
    /// of them add it.
    /// </summary>
    static bool ETagsMatch(string actual, string candidate)
    {
        var wanted = candidate.AsSpan().Trim();

        if (wanted.StartsWith("W/", StringComparison.Ordinal))
            wanted = wanted[2..];

        return wanted.SequenceEqual(actual.AsSpan());
    }

    static void SkipWhitespace(string text, ref int index)
    {
        while (index < text.Length && char.IsWhiteSpace(text[index]))
            index++;
    }
}
