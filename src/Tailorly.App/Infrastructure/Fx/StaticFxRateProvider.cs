using System.Globalization;
using Tailorly.Core.Ports;

namespace Tailorly.App.Infrastructure.Fx;

/// <summary>
/// Placeholder FX provider reading fixed rates from configuration (<c>Fx:StaticRates:USD_INR</c>).
/// Replaced by a daily-cached public provider in M3.
/// </summary>
public sealed class StaticFxRateProvider(IConfiguration config) : IFxRateProvider
{
    public Task<decimal?> GetRateAsync(string from, string to, CancellationToken ct)
    {
        if (string.Equals(from, to, StringComparison.OrdinalIgnoreCase))
            return Task.FromResult<decimal?>(1m);

        var key = $"{from.ToUpperInvariant()}_{to.ToUpperInvariant()}";
        var raw = config[$"Fx:StaticRates:{key}"];
        decimal? rate = decimal.TryParse(raw, NumberStyles.Number, CultureInfo.InvariantCulture, out var r) ? r : null;
        return Task.FromResult(rate);
    }
}
