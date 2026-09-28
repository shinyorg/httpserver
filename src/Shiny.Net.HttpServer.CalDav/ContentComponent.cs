using System.Text;

namespace Shiny.Net.HttpServer.CalDav;

/// <summary>
/// One <c>BEGIN:…</c>/<c>END:…</c> block of an iCalendar (RFC 5545) or vCard (RFC 6350) object: a
/// <c>VCALENDAR</c>, a <c>VEVENT</c> inside it, a <c>VCARD</c>.
/// <para>
/// The two formats share one grammar — folded content lines of <c>name;param=value:value</c>, nested
/// by <c>BEGIN</c> and <c>END</c> — so there is one parser and one model for both. It is deliberately
/// small: enough to validate what a client <c>PUT</c>s, to evaluate a query filter against it, and
/// for a store that adapts an app's own data to read and write the fields it cares about. It is not
/// a calendar library; properties keep their raw text and nothing is interpreted that a filter does
/// not need.
/// </para>
/// <code>
/// var calendar = ContentComponent.Parse(icsText);
/// var evt = calendar.Components.First(c => c.Name == "VEVENT");
/// var title = evt.GetProperty("SUMMARY")?.GetText();
/// </code>
/// </summary>
public sealed class ContentComponent
{
    public ContentComponent(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        this.Name = name.ToUpperInvariant();
    }

    /// <summary>The component name, upper-cased: <c>VCALENDAR</c>, <c>VEVENT</c>, <c>VCARD</c>.</summary>
    public string Name { get; }

    /// <summary>The properties, in the order they appeared.</summary>
    public List<ContentProperty> Properties { get; } = [];

    /// <summary>Nested components, in the order they appeared.</summary>
    public List<ContentComponent> Components { get; } = [];

    /// <summary>The first property with this name, ignoring case and any vCard group, or null.</summary>
    public ContentProperty? GetProperty(string name)
    {
        foreach (var property in this.Properties)
        {
            if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                return property;
        }

        return null;
    }

    /// <summary>Every property with this name, ignoring case and any vCard group.</summary>
    public IEnumerable<ContentProperty> GetProperties(string name)
        => this.Properties.Where(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Nested components with this name, ignoring case.</summary>
    public IEnumerable<ContentComponent> GetComponents(string name)
        => this.Components.Where(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Parses one iCalendar or vCard object. Throws <see cref="FormatException"/> naming the first
    /// line that does not parse.
    /// </summary>
    public static ContentComponent Parse(string text)
        => TryParse(text, out var component, out var error)
            ? component
            : throw new FormatException(error);

    /// <summary>
    /// Parses one iCalendar or vCard object: exactly one top-level component, every <c>BEGIN</c>
    /// matched by its <c>END</c>, and nothing but blank lines after it.
    /// </summary>
    public static bool TryParse(
        string? text,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out ContentComponent? component,
        out string? error
    )
    {
        component = null;
        error = null;

        if (string.IsNullOrWhiteSpace(text))
        {
            error = "The body is empty.";
            return false;
        }

        var stack = new Stack<ContentComponent>();
        ContentComponent? root = null;
        var lineNumber = 0;

        foreach (var line in Unfold(text))
        {
            lineNumber++;

            if (line.Length == 0)
                continue;

            if (!ContentProperty.TryParseLine(line, out var property))
            {
                error = $"Line {lineNumber} is not a content line.";
                return false;
            }

            if (property.Name.Equals("BEGIN", StringComparison.OrdinalIgnoreCase))
            {
                if (root is not null && stack.Count == 0)
                {
                    error = "More than one top-level component.";
                    return false;
                }

                if (property.Value.Trim().Length == 0)
                {
                    error = $"Line {lineNumber} begins a component without a name.";
                    return false;
                }

                var child = new ContentComponent(property.Value.Trim());

                if (stack.Count > 0)
                    stack.Peek().Components.Add(child);
                else
                    root = child;

                stack.Push(child);

                // Nesting in either format is three or four deep. Anything past this is a body
                // built to exhaust the stack of whatever walks it next.
                if (stack.Count > 32)
                {
                    error = "Components are nested too deeply.";
                    return false;
                }

                continue;
            }

            if (property.Name.Equals("END", StringComparison.OrdinalIgnoreCase))
            {
                if (stack.Count == 0 || !stack.Peek().Name.Equals(property.Value.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    error = $"Line {lineNumber} ends a component that is not open.";
                    return false;
                }

                stack.Pop();
                continue;
            }

            if (stack.Count == 0)
            {
                error = $"Line {lineNumber} is outside any component.";
                return false;
            }

            stack.Peek().Properties.Add(property);
        }

        if (root is null)
        {
            error = "No BEGIN line.";
            return false;
        }

        if (stack.Count > 0)
        {
            error = $"{stack.Peek().Name} is never ended.";
            return false;
        }

        component = root;
        return true;
    }

    /// <summary>
    /// Undoes line folding (RFC 5545 §3.1, RFC 6350 §3.2): a line break followed by one space or
    /// tab is a continuation of the line before. Bare <c>LF</c> is accepted as well as <c>CRLF</c>,
    /// because plenty of real clients send it.
    /// </summary>
    internal static List<string> Unfold(string text)
    {
        var lines = new List<string>();
        var current = new StringBuilder();
        var hasCurrent = false;
        var index = 0;

        while (index < text.Length)
        {
            var end = text.IndexOf('\n', index);
            var lineEnd = end < 0 ? text.Length : end;
            var line = text.AsSpan(index, lineEnd - index);

            if (line.Length > 0 && line[^1] == '\r')
                line = line[..^1];

            index = end < 0 ? text.Length : end + 1;

            if (line.Length > 0 && (line[0] == ' ' || line[0] == '\t') && hasCurrent)
            {
                current.Append(line[1..]);
                continue;
            }

            if (hasCurrent)
                lines.Add(current.ToString());

            current.Clear();
            current.Append(line);
            hasCurrent = true;
        }

        if (hasCurrent)
            lines.Add(current.ToString());

        return lines;
    }

    /// <summary>
    /// Writes the component back out, folded at 75 octets with <c>CRLF</c> line endings as both
    /// RFCs require.
    /// </summary>
    public override string ToString()
    {
        var builder = new StringBuilder();
        this.WriteTo(builder);
        return builder.ToString();
    }

    /// <summary>Appends the folded text of this component to <paramref name="builder"/>.</summary>
    public void WriteTo(StringBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        AppendFolded(builder, "BEGIN:" + this.Name);

        foreach (var property in this.Properties)
            AppendFolded(builder, property.ToString());

        foreach (var component in this.Components)
            component.WriteTo(builder);

        AppendFolded(builder, "END:" + this.Name);
    }

    /// <summary>
    /// Folds a line at 75 octets of UTF-8, never inside a character — a fold that split a
    /// multi-byte sequence would leave both halves undecodable.
    /// </summary>
    static void AppendFolded(StringBuilder builder, string line)
    {
        var octets = 0;
        var limit = 75;

        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            var width = c < 0x80 ? 1 : c < 0x800 ? 2 : char.IsHighSurrogate(c) ? 4 : char.IsLowSurrogate(c) ? 0 : 3;

            if (octets + width > limit && width > 0)
            {
                builder.Append("\r\n ");
                octets = 1;
                limit = 75;
            }

            builder.Append(c);
            octets += width;
        }

        builder.Append("\r\n");
    }
}

/// <summary>One parameter of a content line: <c>TZID=Europe/London</c>, <c>TYPE=work,voice</c>.</summary>
/// <param name="Name">The parameter name, upper-cased.</param>
/// <param name="Values">
/// Its values, split on unquoted commas, unquoted, with RFC 6868 <c>^</c> escapes undone.
/// </param>
public sealed record ContentParameter(string Name, IReadOnlyList<string> Values)
{
    /// <summary>The first value, which is the whole value for every parameter that takes one.</summary>
    public string Value => this.Values.Count > 0 ? this.Values[0] : string.Empty;
}

/// <summary>
/// One content line: <c>[group.]NAME[;PARAM=value…]:value</c>.
/// <para>
/// <see cref="Value"/> is kept exactly as it arrived, escapes and all, because what it means depends
/// on its type — a <c>TEXT</c> value escapes commas and a <c>URI</c> does not. <see cref="GetText"/>
/// is the reading for text.
/// </para>
/// </summary>
public sealed class ContentProperty
{
    public ContentProperty(string name, string value, IReadOnlyList<ContentParameter>? parameters = null, string? group = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        this.Name = name.ToUpperInvariant();
        this.Value = value ?? string.Empty;
        this.Parameters = parameters ?? [];
        this.Group = group;
    }

    /// <summary>The vCard group prefix (<c>item1</c> in <c>item1.EMAIL</c>), or null.</summary>
    public string? Group { get; }

    /// <summary>The property name, upper-cased and without any group.</summary>
    public string Name { get; }

    /// <summary>The parameters, in the order they appeared.</summary>
    public IReadOnlyList<ContentParameter> Parameters { get; }

    /// <summary>The raw value, with its escapes.</summary>
    public string Value { get; }

    /// <summary>The first value of a parameter, ignoring case, or null when it is absent.</summary>
    public string? GetParameter(string name)
    {
        foreach (var parameter in this.Parameters)
        {
            if (parameter.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                return parameter.Value;
        }

        return null;
    }

    /// <summary>
    /// The value read as <c>TEXT</c>: <c>\n</c> is a line break and <c>\\</c>, <c>\;</c> and
    /// <c>\,</c> are the characters themselves.
    /// </summary>
    public string GetText() => UnescapeText(this.Value);

    /// <summary>Undoes <c>TEXT</c> escaping.</summary>
    public static string UnescapeText(string value)
    {
        if (value.IndexOf('\\') < 0)
            return value;

        var builder = new StringBuilder(value.Length);

        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];

            if (c != '\\' || i == value.Length - 1)
            {
                builder.Append(c);
                continue;
            }

            var next = value[++i];
            builder.Append(next is 'n' or 'N' ? '\n' : next);
        }

        return builder.ToString();
    }

    /// <summary>Applies <c>TEXT</c> escaping, for building a value to write.</summary>
    public static string EscapeText(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        var builder = new StringBuilder(value.Length);

        foreach (var c in value)
        {
            switch (c)
            {
                case '\\': builder.Append("\\\\"); break;
                case ';': builder.Append("\\;"); break;
                case ',': builder.Append("\\,"); break;
                case '\n': builder.Append("\\n"); break;
                case '\r': break;
                default: builder.Append(c); break;
            }
        }

        return builder.ToString();
    }

    public override string ToString()
    {
        var builder = new StringBuilder();

        if (this.Group is not null)
            builder.Append(this.Group).Append('.');

        builder.Append(this.Name);

        foreach (var parameter in this.Parameters)
        {
            builder.Append(';').Append(parameter.Name).Append('=');

            for (var i = 0; i < parameter.Values.Count; i++)
            {
                if (i > 0)
                    builder.Append(',');

                var value = parameter.Values[i]
                    .Replace("^", "^^", StringComparison.Ordinal)
                    .Replace("\n", "^n", StringComparison.Ordinal)
                    .Replace("\"", "^'", StringComparison.Ordinal);

                // Quoted whenever a delimiter would otherwise end it early.
                if (value.AsSpan().IndexOfAny(";:,") >= 0)
                    builder.Append('"').Append(value).Append('"');
                else
                    builder.Append(value);
            }
        }

        builder.Append(':').Append(this.Value);

        return builder.ToString();
    }

    /// <summary>Splits one unfolded line into group, name, parameters and value.</summary>
    internal static bool TryParseLine(string line, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out ContentProperty? property)
    {
        property = null;

        // The name runs to the first ';' or ':'.
        var nameEnd = line.AsSpan().IndexOfAny(';', ':');

        if (nameEnd <= 0)
            return false;

        var fullName = line[..nameEnd];
        string? group = null;
        var dot = fullName.LastIndexOf('.');

        if (dot >= 0)
        {
            group = fullName[..dot];
            fullName = fullName[(dot + 1)..];
        }

        if (fullName.Length == 0 || !IsName(fullName) || (group is not null && (group.Length == 0 || !IsName(group))))
            return false;

        var parameters = new List<ContentParameter>();
        var index = nameEnd;

        while (index < line.Length && line[index] == ';')
        {
            index++;

            var paramStart = index;
            while (index < line.Length && line[index] is not ('=' or ';' or ':'))
                index++;

            var paramName = line[paramStart..index];

            if (paramName.Length == 0)
                return false;

            // vCard 2.1 writes a bare type as a parameter with no name ("TEL;HOME:…"). It is a
            // TYPE, and reading it as one is kinder than refusing a card a phone exported.
            if (index >= line.Length || line[index] != '=')
            {
                parameters.Add(new ContentParameter("TYPE", [paramName]));
                continue;
            }

            index++;

            var values = new List<string>();

            while (true)
            {
                string value;

                if (index < line.Length && line[index] == '"')
                {
                    var close = line.IndexOf('"', index + 1);

                    if (close < 0)
                        return false;

                    value = line[(index + 1)..close];
                    index = close + 1;
                }
                else
                {
                    var valueStart = index;
                    while (index < line.Length && line[index] is not (';' or ':' or ','))
                        index++;

                    value = line[valueStart..index];
                }

                values.Add(UnescapeParameter(value));

                if (index < line.Length && line[index] == ',')
                {
                    index++;
                    continue;
                }

                break;
            }

            parameters.Add(new ContentParameter(paramName.ToUpperInvariant(), values));
        }

        if (index >= line.Length || line[index] != ':')
            return false;

        property = new ContentProperty(fullName, line[(index + 1)..], parameters, group);
        return true;
    }

    /// <summary>RFC 6868: <c>^n</c> is a newline, <c>^'</c> a double quote, <c>^^</c> a caret.</summary>
    static string UnescapeParameter(string value)
    {
        if (value.IndexOf('^') < 0)
            return value;

        var builder = new StringBuilder(value.Length);

        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] == '^' && i + 1 < value.Length && value[i + 1] is 'n' or '\'' or '^')
            {
                var next = value[++i];
                builder.Append(next switch { 'n' => '\n', '\'' => '"', _ => '^' });
                continue;
            }

            builder.Append(value[i]);
        }

        return builder.ToString();
    }

    /// <summary>
    /// Names are letters, digits and hyphens. Lenient about underscores too, which some exporters
    /// put in X- names.
    /// </summary>
    static bool IsName(string name)
    {
        foreach (var c in name)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_'))
                return false;
        }

        return true;
    }
}
