using System.Net.Mail;
using Tailorly.Core;

namespace Tailorly.App.Api;

/// <summary>Collects field errors and throws one 422 <c>VALIDATION_FAILED</c> with an <c>errors</c> map (ApiDesign §1.1).</summary>
public sealed class FieldErrors
{
    private readonly Dictionary<string, List<string>> _errors = new(StringComparer.Ordinal);

    public bool Any => _errors.Count > 0;

    public FieldErrors Add(string field, string message)
    {
        if (!_errors.TryGetValue(field, out var list)) _errors[field] = list = [];
        list.Add(message);
        return this;
    }

    /// <summary>Required, trimmed, length-limited text. Returns the trimmed value (or null when invalid).</summary>
    public string? Text(string field, string? value, int max, bool required = true, string? label = null)
    {
        var v = value?.Trim();
        if (string.IsNullOrEmpty(v))
        {
            if (required) Add(field, $"{label ?? "This field"} is required.");
            return null;
        }
        if (v.Length > max)
        {
            Add(field, $"{label ?? "This field"} must be {max} characters or fewer.");
            return null;
        }
        return v;
    }

    public string? Email(string field, string? value, bool required = true)
    {
        var v = Text(field, value, 254, required, "Email");
        if (v is null) return null;
        if (!IsEmail(v))
        {
            Add(field, "Enter a valid email address.");
            return null;
        }
        return v;
    }

    public void ThrowIfAny()
    {
        if (!Any) return;
        throw new ApiException(StatusCodes.Status422UnprocessableEntity, ErrorCodes.ValidationFailed, "Validation failed",
            "Check the highlighted fields.", errors: _errors.ToDictionary(kv => kv.Key, kv => kv.Value.ToArray()));
    }

    public static ApiException Single(string field, string message) =>
        new(StatusCodes.Status422UnprocessableEntity, ErrorCodes.ValidationFailed, "Validation failed", message,
            errors: new Dictionary<string, string[]> { [field] = [message] });

    public static bool IsEmail(string value)
    {
        if (value.Length > 254 || value.Contains(' ', StringComparison.Ordinal) || !value.Contains('@', StringComparison.Ordinal))
            return false;
        try
        {
            var a = new MailAddress(value);
            return a.Address == value && a.Host.Contains('.', StringComparison.Ordinal);
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
