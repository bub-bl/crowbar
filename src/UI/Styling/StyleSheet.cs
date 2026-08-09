using System.Text;
using System.Text.RegularExpressions;

namespace Crowbar.UI;

/// <summary>
/// A single CSS rule: a selector, its declarations and its cascade order. The
/// selector is parsed once (lazily) into a compiled form so matching a panel
/// never runs a regex or allocates: the cascade calls <see cref="Matches"/>
/// for every rule on every re-cascade, and the animated demo page re-cascades
/// subtrees every frame.
/// </summary>
public sealed record StyleRule(string Selector, IReadOnlyDictionary<string, string> Properties, int Order)
{
    private CompiledSelector? _compiled;

    public bool Matches(Panel panel)
    {
        var parts = (_compiled ??= CompiledSelector.Parse(Selector)).Parts;
        if (parts.Length == 0 || !parts[^1].Matches(panel)) return false;
        var ancestor = panel.Parent;
        for (var i = parts.Length - 2; i >= 0; i--)
        {
            if (parts[i].IsCombinator)
            {
                i--;
                if (i < 0 || ancestor is null || !parts[i].Matches(ancestor)) return false;
                ancestor = ancestor.Parent;
            }
            else
            {
                while (ancestor is not null && !parts[i].Matches(ancestor)) ancestor = ancestor.Parent;
                if (ancestor is null) return false;
                ancestor = ancestor.Parent;
            }
        }

        return true;
    }

    /// <summary>
    /// A selector split on whitespace into tokens: compound selectors (with
    /// their type/class/id/attribute/pseudo parts extracted once) and the
    /// <c>&gt;</c> combinator. Matching is pure string comparison, no regex.
    /// </summary>
    private sealed class CompiledSelector
    {
        public readonly Part[] Parts;

        private CompiledSelector(Part[] parts) => Parts = parts;

        public static CompiledSelector Parse(string selector)
        {
            // Split on whitespace, but not inside [...] attribute selectors
            // (a quoted value may contain spaces: [data-label="a b"]).
            var tokens = new List<string>();
            var depth = 0;
            var start = 0;
            for (var i = 0; i < selector.Length; i++)
            {
                var c = selector[i];
                if (c == '[') depth++;
                else if (c == ']') depth = Math.Max(0, depth - 1);
                else if (depth == 0 && char.IsWhiteSpace(c))
                {
                    if (i > start) tokens.Add(selector[start..i]);
                    start = i + 1;
                }
            }

            if (start < selector.Length) tokens.Add(selector[start..]);
            var parts = new Part[tokens.Count];
            for (var i = 0; i < tokens.Count; i++)
            {
                if (tokens[i] == ">") parts[i] = Part.Combinator;
                else parts[i] = Part.ParseCompound(tokens[i]);
            }

            return new CompiledSelector(parts);
        }
    }

    /// <summary>
    /// One token of a parsed selector: either the child combinator or a
    /// compound selector whose type, classes, id, attributes and pseudo-class
    /// were extracted once (mirroring the previous regex-based matching).
    /// </summary>
    private readonly struct Part
    {
        public static readonly Part Combinator = new(isCombinator: true);

        private readonly bool _isCombinator;
        private readonly string _type = string.Empty;
        private readonly string _pseudo = string.Empty;
        private readonly string[] _classes = [];
        private readonly string _id = string.Empty;
        private readonly (string Name, string? Value)[] _attributes = [];

        public bool IsCombinator => _isCombinator;

        private Part(string type, string pseudo, string[] classes, string id, (string Name, string? Value)[] attributes)
            : this(false)
        {
            _type = type;
            _pseudo = pseudo;
            _classes = classes;
            _id = id;
            _attributes = attributes;
        }

        private Part(bool isCombinator) => _isCombinator = isCombinator;

        public static Part ParseCompound(string compound)
        {
            var pseudoIndex = compound.IndexOf(':');
            var simple = pseudoIndex >= 0 ? compound[..pseudoIndex] : compound;
            var pseudo = pseudoIndex >= 0 ? compound[(pseudoIndex + 1)..] : string.Empty;

            // Type selector: a leading run of [a-zA-Z0-9_-] starting with a letter.
            string type = string.Empty;
            var index = 0;
            if (simple.Length > 0 && simple[0] != '*' && char.IsLetter(simple[0]))
            {
                var start = 0;
                while (index < simple.Length && (char.IsLetterOrDigit(simple[index]) || simple[index] is '_' or '-')) index++;
                type = simple[start..index];
            }
            else if (simple.Length > 0 && simple[0] == '*')
            {
                index = 1;
            }

            var classes = new List<string>();
            string id = string.Empty;
            var attributes = new List<(string Name, string? Value)>();
            while (index < simple.Length)
            {
                var c = simple[index];
                if (c == '.' || c == '#')
                {
                    var start = ++index;
                    while (index < simple.Length && (char.IsLetterOrDigit(simple[index]) || simple[index] is '_' or '-')) index++;
                    if (index > start)
                    {
                        var name = simple[start..index];
                        if (c == '.') classes.Add(name);
                        else id = name;
                    }
                }
                else if (c == '[')
                {
                    index++;
                    var nameStart = index;
                    while (index < simple.Length && (char.IsLetterOrDigit(simple[index]) || simple[index] is '_' or '-')) index++;
                    var attrName = simple[nameStart..index];
                    string? attrValue = null;
                    if (index < simple.Length && simple[index] == '=')
                    {
                        index++;
                        var valueStart = index;
                        while (index < simple.Length && simple[index] != ']') index++;
                        attrValue = simple[valueStart..index].Trim('"', '\'');
                    }

                    if (index < simple.Length && simple[index] == ']') index++;
                    if (attrName.Length > 0) attributes.Add((attrName, attrValue));
                }
                else
                {
                    // Unknown character: the regex-based matcher treated it as a
                    // separator (it simply did not match), so skip past it.
                    index++;
                }
            }

            return new Part(type, pseudo, classes.ToArray(), id, attributes.ToArray());
        }

        public bool Matches(Panel panel)
        {
            if (IsCombinator) return false;
            if (_pseudo.Length > 0 &&
                !_pseudo.Equals("hover", StringComparison.OrdinalIgnoreCase) &&
                !_pseudo.Equals("active", StringComparison.OrdinalIgnoreCase) &&
                !_pseudo.Equals("focus", StringComparison.OrdinalIgnoreCase) &&
                !_pseudo.Equals("disabled", StringComparison.OrdinalIgnoreCase) &&
                !_pseudo.Equals("checked", StringComparison.OrdinalIgnoreCase)) return false;
            if (_pseudo.Equals("hover", StringComparison.OrdinalIgnoreCase) && !panel.IsHovered) return false;
            if (_pseudo.Equals("active", StringComparison.OrdinalIgnoreCase) && !panel.IsPressed) return false;
            if (_pseudo.Equals("focus", StringComparison.OrdinalIgnoreCase) && !panel.IsFocused) return false;
            if (_pseudo.Equals("disabled", StringComparison.OrdinalIgnoreCase) && panel.IsEnabled) return false;
            if (_pseudo.Equals("checked", StringComparison.OrdinalIgnoreCase) && !panel.IsChecked) return false;
            if (_type.Length > 0 && !_type.Equals(panel.TagName, StringComparison.OrdinalIgnoreCase)) return false;
            foreach (var className in _classes)
            {
                if (!panel.Classes.Contains(className)) return false;
            }

            if (_id.Length > 0 && !string.Equals(panel.Id, _id, StringComparison.OrdinalIgnoreCase)) return false;
            foreach (var (attrName, attrVal) in _attributes)
            {
                if (attrVal is null)
                {
                    if (!panel.Attributes.ContainsKey(attrName) && !panel.HasScope(attrName)) return false;
                }
                else
                {
                    if (!panel.Attributes.TryGetValue(attrName, out var v) ||
                        !string.Equals(v, attrVal, StringComparison.OrdinalIgnoreCase)) return false;
                }
            }

            return true;
        }
    }
}

/// <summary>
/// An ordered collection of <see cref="StyleRule"/>s. Cascading computes a
/// <see cref="ComputedStyle"/> by applying matching rules in declaration order
/// and finishing with the panel's inline styles. Each declaration is dispatched
/// through the <see cref="CssProperties"/> registry, so custom registered
/// properties cascade automatically.
/// </summary>
public sealed class StyleSheet
{
    private readonly List<StyleRule> _rules = [];
    public IReadOnlyList<StyleRule> Rules => _rules;

    public void AddRules(IEnumerable<StyleRule> rules) => _rules.AddRange(rules);
    public void Clear() => _rules.Clear();

    public static StyleSheet Parse(string css, string? scopeId = null)
    {
        var sheet = new StyleSheet();
        var order = 0;
        if (!string.IsNullOrWhiteSpace(scopeId)) css = ScopeKeyframeNames(css, scopeId.Trim());
        css = StripKeyframes(css, out var keyframeBlocks);
        foreach (var (name, body) in keyframeBlocks) Keyframes.Register(ParseKeyframes(name, body));
        foreach (Match match in Regex.Matches(css, "(?s)([^{}]+)\\{([^{}]*)\\}"))
        {
            var properties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var declaration in match.Groups[2].Value.Split(';'))
            {
                var split = declaration.Split(':', 2);
                if (split.Length == 2) properties[split[0].Trim()] = split[1].Trim();
            }

            foreach (var selector in match.Groups[1].Value.Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                var scopedSelector = string.IsNullOrWhiteSpace(scopeId)
                    ? selector.Trim()
                    : ScopeSelector(selector.Trim(), scopeId.Trim());
                sheet._rules.Add(new StyleRule(scopedSelector, properties, order++));
            }
        }

        return sheet;
    }

    /// <summary>
    /// Removes every <c>@keyframes</c> block from the css text and returns its
    /// (name, body) pairs. The body still contains the per-keyframe rule blocks;
    /// brace depth is tracked so nested braces (one per keyframe) are kept
    /// together instead of being misread as style rules.
    /// </summary>
    private static string StripKeyframes(string css, out List<(string Name, string Body)> keyframes)
    {
        keyframes = [];
        var sb = new StringBuilder();
        var index = 0;
        while (index < css.Length)
        {
            var at = css.IndexOf("@keyframes", index, StringComparison.OrdinalIgnoreCase);
            if (at < 0)
            {
                sb.Append(css, index, css.Length - index);
                break;
            }
            sb.Append(css, index, at - index);

            var nameStart = at + "@keyframes".Length;
            while (nameStart < css.Length && char.IsWhiteSpace(css[nameStart])) nameStart++;
            var nameEnd = nameStart;
            while (nameEnd < css.Length && (char.IsLetterOrDigit(css[nameEnd]) || css[nameEnd] is '_' or '-')) nameEnd++;
            if (nameEnd == nameStart)
            {
                sb.Append(css, at, "@keyframes".Length);
                index = at + "@keyframes".Length;
                continue;
            }
            var name = css[nameStart..nameEnd];
            var brace = css.IndexOf('{', nameEnd);
            if (brace < 0)
            {
                sb.Append(css, at, css.Length - at);
                break;
            }
            var depth = 1;
            var end = brace + 1;
            for (; end < css.Length && depth > 0; end++)
            {
                if (css[end] == '{') depth++;
                else if (css[end] == '}') depth--;
            }
            if (depth > 0)
            {
                // Unterminated block: keep it as-is and stop.
                sb.Append(css, at, css.Length - at);
                break;
            }
            keyframes.Add((name, css[(brace + 1)..(end - 1)]));
            index = end;
        }
        return sb.ToString();
    }

    /// <summary>
    /// Renames the keyframes of a scoped stylesheet: each <c>@keyframes name</c>
    /// definition becomes <c>&lt;scopeId&gt;-name</c> and the matching usages in
    /// <c>animation</c>/<c>animation-name</c> declarations are rewritten, so
    /// components never collide on keyframe names (mirroring Blazor CSS
    /// isolation).
    /// </summary>
    private static string ScopeKeyframeNames(string css, string scopeId)
    {
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        css = Regex.Replace(css, "@keyframes\\s+([a-zA-Z0-9_-]+)", match =>
        {
            var name = match.Groups[1].Value;
            var scoped = scopeId + "-" + name;
            names[name] = scoped;
            return "@keyframes " + scoped;
        });
        if (names.Count == 0) return css;
        return Regex.Replace(css, "(?i)(animation(?:-name)?\\s*:\\s*)([^;\\r\\n}]+)", match =>
        {
            var value = match.Groups[2].Value;
            foreach (var (raw, scoped) in names)
            {
                value = Regex.Replace(value, "(?<![a-zA-Z0-9_-])" + Regex.Escape(raw) + "(?![a-zA-Z0-9_-])", scoped);
            }
            return match.Groups[1].Value + value;
        });
    }

    /// <summary>
    /// Parses the body of a <c>@keyframes</c> block into an ordered keyframe
    /// list. Comma-separated selectors (<c>0%, 100%</c>) register one keyframe
    /// per offset, sharing the declarations.
    /// </summary>
    private static KeyframeList ParseKeyframes(string name, string body)
    {
        var frames = new List<KeyframeFrame>();
        foreach (Match match in Regex.Matches(body, "(?s)([^{}]+)\\{([^{}]*)\\}"))
        {
            var declarations = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var declaration in match.Groups[2].Value.Split(';'))
            {
                var split = declaration.Split(':', 2);
                if (split.Length == 2) declarations[split[0].Trim()] = split[1].Trim();
            }
            foreach (var selector in match.Groups[1].Value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                frames.Add(new KeyframeFrame(ParseKeyframeOffset(selector), declarations));
            }
        }
        return new KeyframeList(name, frames);
    }

    /// <summary>Parses a keyframe selector (<c>from</c>, <c>to</c> or a percentage) into a [0, 1] offset.</summary>
    private static float ParseKeyframeOffset(string selector)
    {
        var trimmed = selector.Trim();
        if (trimmed.Equals("from", StringComparison.OrdinalIgnoreCase)) return 0;
        if (trimmed.Equals("to", StringComparison.OrdinalIgnoreCase)) return 1;
        if (trimmed.EndsWith('%')) trimmed = trimmed[..^1];
        return float.TryParse(trimmed, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture,
            out var percent)
            ? Math.Clamp(percent / 100f, 0f, 1f)
            : 0;
    }

    public static string ScopeSelector(string selector, string scopeId)
    {
        if (string.IsNullOrWhiteSpace(scopeId) || string.IsNullOrWhiteSpace(selector)) return selector;
        var scopeAttr = $"[{scopeId}]";
        if (selector.Contains(scopeAttr, StringComparison.OrdinalIgnoreCase)) return selector;

        var deepMatch = Regex.Match(selector, @"::deep|:deep\(([^)]+)\)");
        if (deepMatch.Success)
        {
            var beforeDeep = selector[..deepMatch.Index].TrimEnd();
            var afterDeep = deepMatch.Groups[1].Success
                ? deepMatch.Groups[1].Value
                : selector[(deepMatch.Index + deepMatch.Length)..].TrimStart();

            var scopedBefore = ScopeCompoundSelectors(beforeDeep, scopeAttr);
            return string.IsNullOrWhiteSpace(afterDeep) ? scopedBefore : $"{scopedBefore} {afterDeep.Trim()}";
        }

        return ScopeCompoundSelectors(selector, scopeAttr);
    }

    private static string ScopeCompoundSelectors(string selector, string scopeAttr)
    {
        var parts = Regex.Split(selector, @"(?<=[\s>+~])|(?=[\s>+~])");
        var sb = new StringBuilder();
        foreach (var part in parts)
        {
            if (string.IsNullOrWhiteSpace(part) || part == ">" || part == "+" || part == "~")
            {
                sb.Append(part);
                continue;
            }

            var trimmed = part.Trim();
            if (string.IsNullOrEmpty(trimmed) || trimmed.Contains(scopeAttr, StringComparison.OrdinalIgnoreCase))
            {
                sb.Append(part);
                continue;
            }

            var pseudoIdx = trimmed.IndexOf(':');
            string scopedPart;
            if (pseudoIdx >= 0)
            {
                scopedPart = trimmed[..pseudoIdx] + scopeAttr + trimmed[pseudoIdx..];
            }
            else
            {
                scopedPart = trimmed + scopeAttr;
            }

            sb.Append(scopedPart);
        }

        return sb.ToString();
    }

    public ComputedStyle Compute(Panel panel)
    {
        var style = new ComputedStyle();
        foreach (var rule in _rules.Where(r => r.Matches(panel)).OrderBy(r => r.Order)) Apply(style, rule.Properties);
        Apply(style, panel.InlineStyle);
        return style;
    }

    /// <summary>
    /// Applies declarations to a computed style. Unknown properties and invalid
    /// values are silently ignored, mirroring CSS semantics.
    /// </summary>
    internal static void Apply(ComputedStyle style, IReadOnlyDictionary<string, string> properties)
    {
        foreach (var (name, value) in properties) CssProperties.TryApply(style, name, value);
    }
}
