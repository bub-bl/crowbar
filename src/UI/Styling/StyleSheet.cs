using System.Runtime.CompilerServices;
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
public sealed record StyleRule(string Selector, IReadOnlyDictionary<string, string> Properties, int Order,
    MediaQuery? Media = null)
{
    private CompiledSelector? _compiled;

    private CompiledSelector Compiled => _compiled ??= CompiledSelector.Parse(Selector);

    internal bool UsesComplexMatching => Compiled.IsComplex;

    internal bool TryGetMatchIndex(out SelectorIndexKind kind, out string value)
    {
        var parts = Compiled.Parts;
        if (parts.Length == 0 || PseudoElement.Length > 0)
        {
            kind = SelectorIndexKind.Universal;
            value = string.Empty;
            return parts.Length > 0;
        }

        return parts[^1].TryGetIndex(out kind, out value);
    }

    /// <summary>The pseudo-element the selector targets (<c>before</c>, <c>after</c>) or empty.</summary>
    public string PseudoElement
    {
        get
        {
            var parts = Compiled.Parts;
            return parts.Length > 0 ? parts[^1].PseudoElement : string.Empty;
        }
    }

    public bool Matches(Panel panel)
    {
        var parts = (_compiled ??= CompiledSelector.Parse(Selector)).Parts;
        if (parts.Length == 0 || !parts[^1].Matches(panel)) return false;
        // Walk the selector right-to-left. Each compound must be found relative
        // to the previously matched element: as an ancestor (whitespace), a
        // parent (>), the immediately preceding sibling (+) or any preceding
        // sibling (~). The combinator token between two compounds, when present,
        // sits at the position right of the left compound.
        var previous = panel;
        for (var i = parts.Length - 2; i >= 0; i--)
        {
            if (parts[i].IsCombinator) continue; // read via the relation of the compound to its right
            var relation = i + 1 < parts.Length && parts[i + 1].IsCombinator
                ? parts[i + 1].CombinatorKind
                : SelectorCombinator.Descendant;
            var matched = FindMatch(parts[i], previous, relation);
            if (matched is null) return false;
            previous = matched;
        }

        return true;
    }

    /// <summary>
    /// Matches a pseudo-element rule (<c>.x::before</c>) against a panel: the
    /// selector's last compound must carry the pseudo-element and its base
    /// (plus the whole chain) must match the panel. Used to synthesize
    /// <c>content</c> for the cascade.
    /// </summary>
    public bool MatchesPseudo(Panel panel, string pseudoElement)
    {
        var parts = (_compiled ??= CompiledSelector.Parse(Selector)).Parts;
        if (parts.Length == 0 || !parts[^1].PseudoElement.Equals(pseudoElement, StringComparison.OrdinalIgnoreCase))
            return false;
        if (!parts[^1].MatchesBase(panel)) return false;
        var previous = panel;
        for (var i = parts.Length - 2; i >= 0; i--)
        {
            if (parts[i].IsCombinator) continue;
            var relation = i + 1 < parts.Length && parts[i + 1].IsCombinator
                ? parts[i + 1].CombinatorKind
                : SelectorCombinator.Descendant;
            var matched = FindMatch(parts[i], previous, relation);
            if (matched is null) return false;
            previous = matched;
        }

        return true;
    }

    /// <summary>Finds the element the compound must match relative to <paramref name="previous"/>.</summary>
    private static Panel? FindMatch(Part compound, Panel previous, SelectorCombinator relation)
    {
        switch (relation)
        {
            case SelectorCombinator.Child:
            {
                var directParent = previous.Parent;
                return directParent is not null && compound.Matches(directParent) ? directParent : null;
            }
            case SelectorCombinator.Adjacent:
            {
                var siblingParent = previous.Parent;
                if (siblingParent is null) return null;
                var index = Part.SiblingIndex(previous);
                return index > 0 && compound.Matches(siblingParent.Children[index - 1]) ? siblingParent.Children[index - 1] : null;
            }
            case SelectorCombinator.GeneralSibling:
            {
                var siblingParent = previous.Parent;
                if (siblingParent is null) return null;
                for (var i = Part.SiblingIndex(previous) - 1; i >= 0; i--)
                    if (compound.Matches(siblingParent.Children[i])) return siblingParent.Children[i];
                return null;
            }
            default: // Descendant
                for (var ancestor = previous.Parent; ancestor is not null; ancestor = ancestor.Parent)
                    if (compound.Matches(ancestor)) return ancestor;
                return null;
        }
    }

    /// <summary>
    /// A selector split on whitespace into tokens: compound selectors (with
    /// their type/class/id/attribute/pseudo parts extracted once) and the
    /// <c>&gt;</c> combinator. Matching is pure string comparison, no regex.
    /// </summary>
    private sealed class CompiledSelector
    {
        public readonly Part[] Parts;
        public bool IsComplex { get; }

        private CompiledSelector(Part[] parts)
        {
            Parts = parts;
            IsComplex = parts.Length > 1 || parts.Any(part => part.HasComplexSelectors);
        }

        public static CompiledSelector Parse(string selector)
        {
            // Split on whitespace, but not inside [...] attribute selectors
            // (a quoted value may contain spaces: [data-label="a b"]) or inside
            // functional pseudo-class parens (:not(.a .b), :nth-child(2n+1)).
            var tokens = new List<string>();
            var depth = 0;
            var parens = 0;
            var start = 0;
            for (var i = 0; i < selector.Length; i++)
            {
                var c = selector[i];
                if (c == '[') depth++;
                else if (c == ']') depth = Math.Max(0, depth - 1);
                else if (c == '(') parens++;
                else if (c == ')') parens = Math.Max(0, parens - 1);
                else if (depth == 0 && parens == 0 && char.IsWhiteSpace(c))
                {
                    if (i > start) tokens.Add(selector[start..i]);
                    start = i + 1;
                }
            }

            if (start < selector.Length) tokens.Add(selector[start..]);
            var parts = new Part[tokens.Count];
            for (var i = 0; i < tokens.Count; i++)
            {
                if (tokens[i] == ">") parts[i] = Part.ChildCombinator;
                else if (tokens[i] == "+") parts[i] = Part.AdjacentCombinator;
                else if (tokens[i] == "~") parts[i] = Part.GeneralSiblingCombinator;
                else parts[i] = Part.ParseCompound(tokens[i]);
            }

            return new CompiledSelector(parts);
        }
    }

    /// <summary>
    /// One token of a parsed selector: either a combinator (<c>&gt;</c>, <c>+</c>,
    /// <c>~</c>) or a compound selector whose type, classes, id, attributes and
    /// pseudo-classes (including functional forms like <c>:not(.x)</c> and
    /// <c>:nth-child(2n+1)</c>) were extracted once.
    /// </summary>
    private readonly struct Part
    {
        public static readonly Part ChildCombinator = new(isCombinator: true, SelectorCombinator.Child);
        public static readonly Part AdjacentCombinator = new(isCombinator: true, SelectorCombinator.Adjacent);
        public static readonly Part GeneralSiblingCombinator = new(isCombinator: true, SelectorCombinator.GeneralSibling);

        public readonly bool IsCombinator;
        public readonly SelectorCombinator CombinatorKind;
        private readonly string _type = string.Empty;
        private readonly PseudoClass[] _pseudoClasses = [];
        private readonly string _pseudoElement = string.Empty;
        private readonly string[] _classes = [];
        private readonly string _id = string.Empty;
        private readonly (string Name, string? Value)[] _attributes = [];

        public bool HasComplexSelectors => _attributes.Length > 0 || _pseudoClasses.Length > 0 || _pseudoElement.Length > 0;

        public bool TryGetIndex(out SelectorIndexKind kind, out string value)
        {
            if (_id.Length > 0)
            {
                kind = SelectorIndexKind.Id;
                value = _id;
                return true;
            }

            if (_classes.Length > 0)
            {
                kind = SelectorIndexKind.Class;
                value = _classes[0];
                return true;
            }

            if (_type.Length > 0)
            {
                kind = SelectorIndexKind.Type;
                value = _type;
                return true;
            }

            kind = SelectorIndexKind.Universal;
            value = string.Empty;
            return false;
        }
        /// <summary>The pseudo-element name (<c>before</c>, <c>after</c>) or empty for ordinary compounds.</summary>
        public string PseudoElement => _pseudoElement;

        private Part(string type, PseudoClass[] pseudoClasses, string pseudoElement, string[] classes, string id,
            (string Name, string? Value)[] attributes)
            : this(false, SelectorCombinator.Descendant)
        {
            _type = type;
            _pseudoClasses = pseudoClasses;
            _pseudoElement = pseudoElement;
            _classes = classes;
            _id = id;
            _attributes = attributes;
        }

        private Part(bool isCombinator, SelectorCombinator kind)
        {
            IsCombinator = isCombinator;
            CombinatorKind = kind;
        }

        public static Part ParseCompound(string compound)
        {
            // Type selector: a leading run of [a-zA-Z0-9_-] starting with a letter.
            string type = string.Empty;
            var index = 0;
            if (compound.Length > 0 && compound[0] != '*' && char.IsLetter(compound[0]))
            {
                var start = 0;
                while (index < compound.Length && (char.IsLetterOrDigit(compound[index]) || compound[index] is '_' or '-')) index++;
                type = compound[start..index];
            }
            else if (compound.Length > 0 && compound[0] == '*')
            {
                index = 1;
            }

            var classes = new List<string>();
            string id = string.Empty;
            var attributes = new List<(string Name, string? Value)>();
            var pseudoClasses = new List<PseudoClass>();
            string pseudoElement = string.Empty;
            while (index < compound.Length)
            {
                var c = compound[index];
                if (c == '.' || c == '#')
                {
                    var start = ++index;
                    while (index < compound.Length && (char.IsLetterOrDigit(compound[index]) || compound[index] is '_' or '-')) index++;
                    if (index > start)
                    {
                        var name = compound[start..index];
                        if (c == '.') classes.Add(name);
                        else id = name;
                    }
                }
                else if (c == '[')
                {
                    index++;
                    var nameStart = index;
                    while (index < compound.Length && (char.IsLetterOrDigit(compound[index]) || compound[index] is '_' or '-')) index++;
                    var attrName = compound[nameStart..index];
                    string? attrValue = null;
                    if (index < compound.Length && compound[index] == '=')
                    {
                        index++;
                        var valueStart = index;
                        while (index < compound.Length && compound[index] != ']') index++;
                        attrValue = compound[valueStart..index].Trim('\"', '\'');
                    }

                    if (index < compound.Length && compound[index] == ']') index++;
                    if (attrName.Length > 0) attributes.Add((attrName, attrValue));
                }
                else if (c == ':')
                {
                    var doubleColon = index + 1 < compound.Length && compound[index + 1] == ':';
                    index += doubleColon ? 2 : 1;
                    var nameStart = index;
                    while (index < compound.Length && (char.IsLetterOrDigit(compound[index]) || compound[index] is '_' or '-')) index++;
                    var name = compound[nameStart..index];
                    string? argument = null;
                    if (index < compound.Length && compound[index] == '(')
                    {
                        var depth = 1;
                        var argumentStart = ++index;
                        while (index < compound.Length && depth > 0)
                        {
                            if (compound[index] == '(') depth++;
                            else if (compound[index] == ')') depth--;
                            index++;
                        }

                        argument = compound[argumentStart..Math.Max(argumentStart, index - 1)].Trim();
                    }

                    if (doubleColon) pseudoElement = name;
                    else pseudoClasses.Add(new PseudoClass(name, argument));
                }
                else
                {
                    // Unknown character: the regex-based matcher treated it as a
                    // separator (it simply did not match), so skip past it.
                    index++;
                }
            }

            return new Part(type, pseudoClasses.ToArray(), pseudoElement, classes.ToArray(), id, attributes.ToArray());
        }

        public bool Matches(Panel panel)
        {
            if (IsCombinator) return false;
            // Pseudo-element selectors never match panels as ordinary rules;
            // they are consumed separately (see StyleRule.MatchesPseudo).
            if (_pseudoElement.Length > 0) return false;
            return MatchesBase(panel);
        }

        /// <summary>Matches the compound ignoring any pseudo-element part (used by <see cref="StyleRule.MatchesPseudo"/>).</summary>
        public bool MatchesBase(Panel panel)
        {
            if (IsCombinator) return false;
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

            foreach (var pseudo in _pseudoClasses)
                if (!MatchesPseudo(pseudo, panel)) return false;
            return true;
        }

        private static bool MatchesPseudo(PseudoClass pseudo, Panel panel)
        {
            switch (pseudo.Name.ToLowerInvariant())
            {
                case "hover": return panel.IsHovered;
                case "active": return panel.IsPressed;
                case "focus": return panel.IsFocused;
                case "focus-within": return IsFocusedWithin(panel);
                case "disabled": return !panel.IsEnabled;
                case "enabled": return panel.IsEnabled;
                case "checked": return panel.IsChecked;
                case "first-child": return SiblingIndex(panel) == 0;
                case "last-child": return panel.Parent is not null && SiblingIndex(panel) == panel.Parent.Children.Count - 1;
                case "only-child": return panel.Parent is not null && panel.Parent.Children.Count == 1;
                case "nth-child": return NthMatches(panel, pseudo.Argument, reversed: false);
                case "nth-last-child": return NthMatches(panel, pseudo.Argument, reversed: true);
                case "empty": return panel.Children.Count == 0;
                case "not":
                    // Simple-selector negation only (a compound, not a complex
                    // selector with combinators): the common `:not(.x)` case.
                    return pseudo.Argument is not null && !ParseCompound(pseudo.Argument).Matches(panel);
                default: return false;
            }
        }

        private static bool IsFocusedWithin(Panel panel)
        {
            if (panel.IsFocused) return true;
            foreach (var child in panel.ChildrenInternal)
                if (IsFocusedWithin(child)) return true;
            return false;
        }

        /// <summary>The 0-based index of the panel among its parent's children.</summary>
        internal static int SiblingIndex(Panel panel)
        {
            var parent = panel.Parent;
            if (parent is null) return 0;
            for (var i = 0; i < parent.Children.Count; i++)
                if (ReferenceEquals(parent.Children[i], panel)) return i;
            return 0;
        }

        /// <summary>Matches <c>an+b</c>, <c>odd</c>, <c>even</c> or a literal position (1-based).</summary>
        private static bool NthMatches(Panel panel, string? argument, bool reversed)
        {
            if (string.IsNullOrWhiteSpace(argument)) return false;
            var expr = argument.Trim().ToLowerInvariant();
            var count = panel.Parent?.Children.Count ?? 1;
            var position = reversed ? count - SiblingIndex(panel) : SiblingIndex(panel) + 1;
            if (expr == "odd") return position % 2 == 1;
            if (expr == "even") return position % 2 == 0;
            var formula = Regex.Match(expr, @"^([+-]?\d*)n(?:([+-]\d+))?$");
            if (formula.Success)
            {
                var aText = formula.Groups[1].Value;
                var a = aText is "" or "+" ? 1 : aText == "-" ? -1 : int.Parse(aText);
                var b = formula.Groups[2].Success ? int.Parse(formula.Groups[2].Value) : 0;
                var diff = position - b;
                return a != 0 && diff % a == 0 && diff / a >= 0;
            }

            return int.TryParse(expr, out var exact) && position == exact;
        }
    }

    /// <summary>A parsed pseudo-class: name plus the (optional) functional argument.</summary>
    private readonly record struct PseudoClass(string Name, string? Argument);

    /// <summary>The relation between two compounds of a selector.</summary>
    internal enum SelectorCombinator
    {
        Descendant,
        Child,
        Adjacent,
        GeneralSibling
    }
}

internal enum SelectorIndexKind
{
    Universal,
    Id,
    Class,
    Type
}

/// <summary>The media feature of a <c>@media</c> condition.</summary>
public enum MediaFeature
{
    MinWidth,
    MaxWidth,
    Width,
    MinHeight,
    MaxHeight,
    Height
}

/// <summary>One <c>(feature: value)</c> condition of a media query.</summary>
public readonly record struct MediaCondition(MediaFeature Feature, float Value)
{
    public bool Matches(float width, float height) => Feature switch
    {
        MediaFeature.MinWidth => width >= Value,
        MediaFeature.MaxWidth => width <= Value,
        MediaFeature.Width => Math.Abs(width - Value) < 0.5f,
        MediaFeature.MinHeight => height >= Value,
        MediaFeature.MaxHeight => height <= Value,
        MediaFeature.Height => Math.Abs(height - Value) < 0.5f,
        _ => true
    };
}

/// <summary>
/// A parsed <c>@media</c> query: comma-separated alternatives (OR), each an
/// <c>and</c>-joined list of conditions. Width/height features are evaluated
/// against the viewport recorded on the style sheet.
/// </summary>
public sealed class MediaQuery
{
    private readonly List<List<MediaCondition>> _alternatives;

    private MediaQuery(List<List<MediaCondition>> alternatives) => _alternatives = alternatives;

    public bool Matches(float width, float height)
    {
        foreach (var alternative in _alternatives)
        {
            var all = true;
            foreach (var condition in alternative)
            {
                if (!condition.Matches(width, height)) { all = false; break; }
            }

            if (all) return true;
        }
        return false;
    }

    /// <summary>Parses the prelude of an <c>@media</c> block; null means "matches everything".</summary>
    internal static MediaQuery? Parse(string prelude)
    {
        if (prelude.Trim().Equals("all", StringComparison.OrdinalIgnoreCase)) return null;
        var alternatives = new List<List<MediaCondition>>();
        foreach (var alternative in prelude.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            // `all` as one alternative of an OR list matches everything.
            if (alternative.Equals("all", StringComparison.OrdinalIgnoreCase))
            {
                alternatives.Add([]);
                continue;
            }

            var conditions = new List<MediaCondition>();
            foreach (var part in alternative.Split("and", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (TryParseCondition(part, out var condition)) conditions.Add(condition);
            }

            if (conditions.Count > 0) alternatives.Add(conditions);
        }
        return alternatives.Count == 0 ? null : new MediaQuery(alternatives);
    }

    /// <summary>Parses one <c>(min-width: 800px)</c> condition (px values only).</summary>
    private static bool TryParseCondition(string token, out MediaCondition condition)
    {
        condition = default;
        var trimmed = token.Trim();
        if (trimmed.Length >= 2 && trimmed[0] == '(' && trimmed[^1] == ')') trimmed = trimmed[1..^1].Trim();
        var colon = trimmed.IndexOf(':');
        if (colon < 0) return false;
        var name = trimmed[..colon].Trim().ToLowerInvariant();
        var valueText = trimmed[(colon + 1)..].Trim();
        if (valueText.EndsWith("px", StringComparison.OrdinalIgnoreCase)) valueText = valueText[..^2];
        if (!float.TryParse(valueText, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var value)) return false;
        switch (name)
        {
            case "min-width": condition = new MediaCondition(MediaFeature.MinWidth, value); return true;
            case "max-width": condition = new MediaCondition(MediaFeature.MaxWidth, value); return true;
            case "width": condition = new MediaCondition(MediaFeature.Width, value); return true;
            case "min-height": condition = new MediaCondition(MediaFeature.MinHeight, value); return true;
            case "max-height": condition = new MediaCondition(MediaFeature.MaxHeight, value); return true;
            case "height": condition = new MediaCondition(MediaFeature.Height, value); return true;
            default: return false;
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
    // The combined sheet may contain rules from several source sheets whose
    // order values overlap. Sort that merged list once, not once per panel on
    // every cascade.
    private StyleRule[]? _orderedRules;
    private Dictionary<string, List<int>>? _idIndex;
    private Dictionary<string, List<int>>? _classIndex;
    private Dictionary<string, List<int>>? _typeIndex;
    private List<int>? _universalIndex;
    private int[]? _candidateStamps;
    private int _candidateStamp;
    private readonly ConditionalWeakTable<Panel, MatchCache> _matchCache = new();

    private sealed class MatchCache
    {
        public int Signature = int.MinValue;
        public readonly Dictionary<int, bool> Results = new();
    }

    // Pre-filtered pseudo-element rule lists: the cascade synthesizes
    // ::before/::after content per panel, so iterating only the rules that
    // actually target the pseudo element (instead of re-matching every rule)
    // keeps the per-frame re-cascade cheap.
    private readonly List<StyleRule> _beforeRules = [];
    private readonly List<StyleRule> _afterRules = [];
    private float _viewportWidth;
    private float _viewportHeight;
    private bool _viewportSet;
    public IReadOnlyList<StyleRule> Rules => _rules;

    public void AddRules(IEnumerable<StyleRule> rules)
    {
        foreach (var rule in rules) AddRule(rule);
    }

    private void AddRule(StyleRule rule)
    {
        _rules.Add(rule);
        InvalidateIndexes();
        var pseudo = rule.PseudoElement;
        if (pseudo.Length == 0) return;
        if (pseudo.Equals("before", StringComparison.OrdinalIgnoreCase)) _beforeRules.Add(rule);
        else if (pseudo.Equals("after", StringComparison.OrdinalIgnoreCase)) _afterRules.Add(rule);
    }

    public void Clear()
    {
        _rules.Clear();
        InvalidateIndexes();
        _beforeRules.Clear();
        _afterRules.Clear();
    }

    private void InvalidateIndexes()
    {
        _orderedRules = null;
        _idIndex = null;
        _classIndex = null;
        _typeIndex = null;
        _universalIndex = null;
        _candidateStamps = null;
        _candidateStamp = 0;
    }

    private void EnsureIndexes()
    {
        if (_orderedRules is not null && _candidateStamps is not null) return;
        _orderedRules = _rules.OrderBy(rule => rule.Order).ToArray();
        _idIndex = new(StringComparer.OrdinalIgnoreCase);
        _classIndex = new(StringComparer.OrdinalIgnoreCase);
        _typeIndex = new(StringComparer.OrdinalIgnoreCase);
        _universalIndex = [];
        _candidateStamps = new int[_orderedRules.Length];

        for (var index = 0; index < _orderedRules.Length; index++)
        {
            var rule = _orderedRules[index];
            if (rule.PseudoElement.Length > 0) continue;
            if (!rule.TryGetMatchIndex(out var kind, out var key))
            {
                _universalIndex.Add(index);
                continue;
            }

            var target = kind switch
            {
                SelectorIndexKind.Id => _idIndex,
                SelectorIndexKind.Class => _classIndex,
                SelectorIndexKind.Type => _typeIndex,
                _ => null
            };
            if (target is null) _universalIndex.Add(index);
            else
            {
                if (!target.TryGetValue(key, out var bucket)) target[key] = bucket = [];
                bucket.Add(index);
            }
        }
    }

    private int NextCandidateStamp()
    {
        if (++_candidateStamp == int.MaxValue)
        {
            Array.Clear(_candidateStamps!);
            _candidateStamp = 1;
        }

        return _candidateStamp;
    }

    private static int SelectorSignature(Panel panel)
    {
        unchecked
        {
            var signature = 17;
            for (var current = panel; current is not null; current = current.Parent)
            {
                signature = signature * 31 + RuntimeHelpers.GetHashCode(current);
                signature = signature * 31 + current.SelectorVersion;
            }
            return signature;
        }
    }

    private void MarkCandidates(Panel panel, int stamp)
    {
        void Mark(List<int>? bucket)
        {
            if (bucket is null) return;
            foreach (var index in bucket) _candidateStamps![index] = stamp;
        }

        Mark(_universalIndex);
        if (!string.IsNullOrEmpty(panel.Id) && _idIndex!.TryGetValue(panel.Id, out var idRules)) Mark(idRules);
        foreach (var className in panel.ClassesInternal)
            if (_classIndex!.TryGetValue(className, out var classRules)) Mark(classRules);
        if (_typeIndex!.TryGetValue(panel.TagName, out var typeRules)) Mark(typeRules);
    }

    /// <summary>True when the sheet carries at least one rule targeting the pseudo element.</summary>
    internal bool HasPseudoRules(string pseudoElement) =>
        pseudoElement.Equals("before", StringComparison.OrdinalIgnoreCase)
            ? _beforeRules.Count > 0
            : _afterRules.Count > 0;

    /// <summary>Records the viewport used to evaluate <c>@media</c> queries.</summary>
    public void SetViewport(float width, float height)
    {
        _viewportWidth = width;
        _viewportHeight = height;
        _viewportSet = true;
    }

    public static StyleSheet Parse(string css, string? scopeId = null)
    {
        var sheet = new StyleSheet();
        var order = 0;
        if (!string.IsNullOrWhiteSpace(scopeId)) css = ScopeKeyframeNames(css, scopeId.Trim());
        css = StripKeyframes(css, out var keyframeBlocks);
        foreach (var (name, body) in keyframeBlocks) Keyframes.Register(ParseKeyframes(name, body));
        ParseRules(sheet, css, media: null, scopeId, ref order);
        return sheet;
    }

    /// <summary>
    /// Scans a css chunk for style rules, descending into <c>@media</c> blocks
    /// (whose rules carry the parsed media condition). Brace depth is tracked so
    /// nested blocks and rule bodies are not misread. Other at-rules are
    /// skipped like unknown syntax.
    /// </summary>
    private static void ParseRules(StyleSheet sheet, string css, MediaQuery? media, string? scopeId, ref int order)
    {
        var index = 0;
        while (index < css.Length)
        {
            while (index < css.Length && char.IsWhiteSpace(css[index])) index++;
            if (index >= css.Length) break;
            if (css[index] == '@' && RegionMatches(css, index, "@media"))
            {
                var open = css.IndexOf('{', index);
                if (open < 0) break;
                var query = MediaQuery.Parse(css[(index + "@media".Length)..open]);
                var close = FindMatchingBrace(css, open);
                if (close < 0) break;
                ParseRules(sheet, css[(open + 1)..close], query, scopeId, ref order);
                index = close + 1;
                continue;
            }

            var brace = css.IndexOf('{', index);
            if (brace < 0) break;
            var ruleEnd = FindMatchingBrace(css, brace);
            if (ruleEnd < 0) break;
            var selectorText = css[index..brace].Trim();
            var body = css[(brace + 1)..ruleEnd];
            // Unknown at-rules (and stray text) must not become selector rules
            // that could match every panel.
            if (selectorText.Length > 0 && !selectorText.StartsWith('@'))
                AddSelectorRules(sheet, selectorText, body, media, scopeId, ref order);
            index = ruleEnd + 1;
        }
    }

    /// <summary>Adds the rules of one rule block, one per comma-separated selector.</summary>
    private static void AddSelectorRules(StyleSheet sheet, string selectorText, string body, MediaQuery? media,
        string? scopeId, ref int order)
    {
        var properties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var declaration in body.Split(';'))
        {
            var split = declaration.Split(':', 2);
            if (split.Length == 2) properties[split[0].Trim()] = split[1].Trim();
        }

        foreach (var selector in selectorText.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = selector.Trim();
            if (trimmed.Length == 0) continue;
            var scopedSelector = string.IsNullOrWhiteSpace(scopeId)
                ? trimmed
                : ScopeSelector(trimmed, scopeId.Trim());
            sheet.AddRule(new StyleRule(scopedSelector, properties, order++, media));
        }
    }

    /// <summary>Finds the index just after the closing brace matching the brace at <paramref name="open"/>.</summary>
    private static int FindMatchingBrace(string css, int open)
    {
        var depth = 1;
        for (var i = open + 1; i < css.Length; i++)
        {
            if (css[i] == '{') depth++;
            else if (css[i] == '}') depth--;
            if (depth == 0) return i;
        }

        return -1;
    }

    /// <summary>Case-insensitive prefix match of <paramref name="text"/> at <paramref name="index"/>.</summary>
    private static bool RegionMatches(string css, int index, string text)
    {
        if (index + text.Length > css.Length) return false;
        return css.AsSpan(index, text.Length).Equals(text.AsSpan(), StringComparison.OrdinalIgnoreCase);
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
        ComputeInto(panel, style);
        return style;
    }

    /// <summary>
    /// Cascades the rules into an existing style object. The cascade reuses a
    /// per-panel buffer (see <see cref="Panel.ComputeStyle"/>) so a
    /// style-stable panel allocates nothing per pass; this method only writes
    /// the properties the matching rules declare.
    /// </summary>
    internal void ComputeInto(Panel panel, ComputedStyle style)
    {
        EnsureIndexes();
        var mediaEnabled = !_viewportSet;
        var orderedRules = _orderedRules!;
        var stamp = NextCandidateStamp();
        MarkCandidates(panel, stamp);
        var signature = SelectorSignature(panel);
        var cache = _matchCache.GetValue(panel, static _ => new MatchCache());
        if (cache.Signature != signature)
        {
            cache.Signature = signature;
            cache.Results.Clear();
        }

        for (var i = 0; i < orderedRules.Length; i++)
        {
            if (_candidateStamps![i] != stamp) continue;
            var rule = orderedRules[i];
            if (!mediaEnabled && rule.Media?.Matches(_viewportWidth, _viewportHeight) == false) continue;

            bool matches;
            if (rule.UsesComplexMatching)
            {
                if (!cache.Results.TryGetValue(i, out matches))
                {
                    matches = rule.Matches(panel);
                    cache.Results[i] = matches;
                }
            }
            else matches = rule.Matches(panel);
            if (matches) Apply(style, rule.Properties);
        }
        Apply(style, panel.InlineStyle);
    }

    /// <summary>
    /// Computes the content and style of a matching <c>::before</c>/<c>::after</c>
    /// rule. The pseudo element inherits from the panel's computed style, then
    /// its own declarations override (content, color, font properties,
    /// letter-spacing, text-transform, text-decoration). Returns false when no
    /// rule matches; <paramref name="content"/> is null when the rule sets no
    /// text content (an empty or attr() pseudo element).
    /// </summary>
    internal bool TryComputePseudo(Panel panel, string pseudoElement, ComputedStyle inherited,
        out string? content, out ComputedStyle style)
    {
        content = null;
        style = inherited.Clone();
        var rules = pseudoElement.Equals("before", StringComparison.OrdinalIgnoreCase) ? _beforeRules : _afterRules;
        if (rules.Count == 0) return false;
        var found = false;
        var mediaEnabled = !_viewportSet;
        foreach (var rule in rules)
        {
            if (rule.Media is not null && !mediaEnabled && !rule.Media.Matches(_viewportWidth, _viewportHeight)) continue;
            if (!rule.MatchesPseudo(panel, pseudoElement)) continue;
            found = true;
            foreach (var (name, value) in rule.Properties)
            {
                if (name.Equals("content", StringComparison.OrdinalIgnoreCase))
                {
                    if (ParseContent(value, panel) is { } text) content = text;
                }
                else CssProperties.TryApply(style, name, value);
            }
        }

        return found;
    }

    /// <summary>Parses <c>content: "text"</c>, <c>content: 'text'</c> or <c>content: attr(name)</c>.</summary>
    private static string? ParseContent(string value, Panel panel)
    {
        var trimmed = value.Trim();
        if (trimmed.Length >= 2 && trimmed[0] is '"' or '\'' && trimmed[^1] == trimmed[0]) return trimmed[1..^1];
        var attr = Regex.Match(trimmed, @"^attr\(\s*([A-Za-z_][A-Za-z0-9_-]*)\s*\)$");
        if (attr.Success) return panel.Attributes.TryGetValue(attr.Groups[1].Value, out var attributeValue) ? attributeValue : null;
        return trimmed.Length == 0 ? null : trimmed;
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
