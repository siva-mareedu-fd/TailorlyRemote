namespace Tailorly.Core.Ports;

/// <summary>Daily-cached FX rates used for "≈ INR" salary display.</summary>
public interface IFxRateProvider
{
    /// <summary>Units of <paramref name="to"/> per one unit of <paramref name="from"/>, or null if unknown.</summary>
    Task<decimal?> GetRateAsync(string from, string to, CancellationToken ct);
}
