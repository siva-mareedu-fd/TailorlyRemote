namespace Tailorly.Core.Ports;

/// <summary>
/// Private file storage. Keys look like <c>{userId}/resumes/{id}/original.docx</c>.
/// There are no public URLs; downloads always go through authorised API endpoints.
/// </summary>
public interface IFileStore
{
    Task SaveAsync(string key, Stream content, CancellationToken ct);

    /// <summary>Opens a stored file for reading, or returns null if it does not exist.</summary>
    Task<Stream?> OpenReadAsync(string key, CancellationToken ct);

    /// <summary>Deletes one file. Idempotent.</summary>
    Task DeleteAsync(string key, CancellationToken ct);

    /// <summary>Deletes every file under a prefix (e.g. <c>{userId}/</c> on account deletion). Idempotent.</summary>
    Task DeletePrefixAsync(string prefix, CancellationToken ct);
}
