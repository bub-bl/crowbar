using System.Reflection;
using System.Text.RegularExpressions;

namespace Crowbar.UI;

/// <summary>Bridges synthetic <c>data-codex-*</c> attributes to component members at runtime.</summary>
internal static class RazorEventInvoker
{
    public static void Invoke(object target, string expression, object? argument)
    {
        var invocation = RazorComponentFactory.CleanRazorExpression(expression);
        if (invocation.Contains("=>", StringComparison.Ordinal))
            invocation = invocation[(invocation.IndexOf("=>", StringComparison.Ordinal) + 2)..].Trim();
        if (invocation.StartsWith("this.", StringComparison.Ordinal)) invocation = invocation[5..].Trim();
        // Simple state mutations from inline lambdas: `Counter++`, `Counter--`
        // and `Name = "value"` (or `Name = OtherMember`). These cover the
        // common demo pattern `@onclick="() => Counter++"` without needing to
        // compile the lambda body.
        var update = Regex.Match(invocation, @"^([A-Za-z_][A-Za-z0-9_.]*)(\s*(\+\+|--)\s*|\s*=\s*(.+))?$");
        if (update.Success && update.Groups[2].Success)
        {
            var member = update.Groups[1].Value;
            if (update.Groups[3].Success)
            {
                var delta = update.Groups[3].Value == "++" ? 1 : -1;
                SetNumeric(target, member, GetNumeric(target, member) + delta);
            }
            else
            {
                SetMember(target, member, ParseLiteral(update.Groups[4].Value, target));
            }
            if (target is PanelComponent pc) pc.StateHasChanged();
            return;
        }

        var methodName = Regex.Match(invocation, @"^[A-Za-z_][A-Za-z0-9_]*").Value;
        if (string.IsNullOrEmpty(methodName))
            throw new InvalidOperationException(
                $"Unsupported Razor event expression '{expression}'. Use a method, a method-call lambda or a member assignment.");
        var methods = target.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Where(m => m.Name.Equals(methodName, StringComparison.Ordinal)).ToList();
        if (methods.Count == 0)
            throw new InvalidOperationException($"Razor event handler '{methodName}' was not found.");
        var method = methods.FirstOrDefault(m => m.GetParameters().Length == (argument is null ? 0 : 1)) ?? methods[0];
        var parameters = method.GetParameters();
        object?[] args = parameters.Length == 0 ? [] : [ConvertArgument(argument!, parameters[0].ParameterType)];
        var result = method.Invoke(target, args);
        if (result is Task task) task.GetAwaiter().GetResult();
        // Event handlers normally trigger a component render. A handler may
        // return false to explicitly opt out for high-frequency events such as
        // pointer move when no visible state changed.
        var shouldRender = result is not bool render || render;
        if (target is PanelComponent component && shouldRender)
            component.StateHasChanged();
    }

    /// <summary>Reads a numeric member (field or property) of the target.</summary>
    private static double GetNumeric(object target, string member)
    {
        if (target is PanelComponent component && component.GetType().GetProperty(member,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(component) is { } value)
            return Convert.ToDouble(value);
        return Convert.ToDouble(target.GetType().GetField(member,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(target));
    }

    /// <summary>Writes a numeric member (field or property) of the target.</summary>
    private static void SetNumeric(object target, string member, double value)
    {
        var type = target.GetType();
        var property = type.GetProperty(member, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (property?.CanWrite == true) { property.SetValue(target, Convert.ChangeType(value, property.PropertyType)); return; }
        var field = type.GetField(member, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (field is not null) { field.SetValue(target, Convert.ChangeType(value, field.FieldType)); return; }
        throw new InvalidOperationException($"Razor event member '{member}' was not found or is read-only.");
    }

    /// <summary>Writes a member from a literal (string, number, bool) or another member reference.</summary>
    private static void SetMember(object target, string member, object? value)
    {
        var type = target.GetType();
        var property = type.GetProperty(member, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (property?.CanWrite == true)
        {
            property.SetValue(target, ConvertLiteral(value, property.PropertyType, target));
            return;
        }
        var field = type.GetField(member, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (field is not null) { field.SetValue(target, ConvertLiteral(value, field.FieldType, target)); return; }
        throw new InvalidOperationException($"Razor event member '{member}' was not found or is read-only.");
    }

    /// <summary>Converts an assignment value to the member type: literal, or a reference to another member of the target.</summary>
    private static object? ConvertLiteral(object? value, Type targetType, object target)
    {
        if (value is string text && !text.StartsWith('"') && !text.EndsWith('"'))
        {
            var property = target.GetType().GetProperty(text, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            var field = target.GetType().GetField(text, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            var referenced = property?.GetValue(target) ?? field?.GetValue(target);
            if (referenced is not null) value = referenced;
        }
        if (value is string quoted && quoted.Length >= 2 && quoted[0] == '"' && quoted[^1] == '"')
            value = quoted[1..^1];
        if (targetType == typeof(string)) return value?.ToString();
        if (value is null) return null;
        return Convert.ChangeType(value, Nullable.GetUnderlyingType(targetType) ?? targetType);
    }

    /// <summary>Parses an assignment literal (quoted string, number, true/false) or a member reference.</summary>
    private static object? ParseLiteral(string text, object target)
    {
        var trimmed = text.Trim();
        if (trimmed.Length >= 2 && trimmed[0] == '"' && trimmed[^1] == '"') return trimmed[1..^1];
        if (bool.TryParse(trimmed, out var boolean)) return boolean;
        if (double.TryParse(trimmed, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var number)) return number;
        return trimmed;
    }

    public static void SetValue(object target, string memberName, string value)
    {
        memberName = RazorComponentFactory.CleanRazorExpression(memberName);
        var type = target.GetType();
        var property =
            type.GetProperty(memberName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (property?.CanWrite == true)
        {
            property.SetValue(target, value);
            if (target is PanelComponent c) c.StateHasChanged();
            return;
        }

        var field = type.GetField(memberName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (field is not null)
        {
            field.SetValue(target, value);
            if (target is PanelComponent c) c.StateHasChanged();
            return;
        }

        throw new InvalidOperationException($"Razor binding target '{memberName}' was not found or is read-only.");
    }

    private static object? ConvertArgument(object argument, Type type)
    {
        if (type.IsInstanceOfType(argument)) return argument;
        if (type == typeof(string)) return argument.ToString();
        return Convert.ChangeType(argument, type);
    }
}
