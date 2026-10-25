namespace Tailorly.App.Api;

/// <summary>
/// An expected API failure with a stable error code (ApiDesign §10). Mapped to RFC 7807 by
/// <see cref="ApiExceptionHandler"/>. Messages must never contain résumé/job text or emails.
/// </summary>
public sealed class ApiException(
    int status,
    string code,
    string title,
    string? detail = null,
    IReadOnlyDictionary<string, object?>? extra = null,
    IDictionary<string, string[]>? errors = null) : Exception(title)
{
    public int Status { get; } = status;
    public string Code { get; } = code;
    public string Title { get; } = title;
    public string? Detail { get; } = detail;
    public IReadOnlyDictionary<string, object?>? Extra { get; } = extra;
    public IDictionary<string, string[]>? Errors { get; } = errors;
}
