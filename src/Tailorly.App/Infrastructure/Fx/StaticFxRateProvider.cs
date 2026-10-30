using System.Globalization;
using Tailorly.Core.Ports;

namespace Tailorly.App.Infrastructure.Fx;

/// <summary>
/// Fixed rates from configuration (<c>Fx:StaticRates:USD_INR</c>). Used when <c>Fx:Provider</c> is <c>Static</c> (tests,
/// offline dev) and as the fallback of the public provider when it has never been reachable. A pair is answered directly,
/// by its inverse, or through USD.
/// </summary>
public sealed class StaticFxRateProvider(IConfiguration config) : IFxRateProvider
{
    public Task<decimal?> GetRateAsync(string from, string to, CancellationToken ct) => Task.FromResult(Rate(from, to));

    private decimal? Rate(string from, string to)
    {
        from = from.ToUpperInvariant();
        to = to.ToUpperInvariant();
        if (from == to) return 1m;
        if (Direct(from, to) is { } d) return d;
        if (from != "USD" && to != "USD" && Direct(from, "USD") is { } a && Direct("USD", to) is { } b) return a * b;
        return null;
    }

    private decimal? Direct(string from, string to)
    {
        if (Configured(from, to) is { } r) return r;
        return Configured(to, from) is { } inverse && inverse > 0 ? 1m / inverse : null;
    }

    private decimal? Configured(string from, string to)
    {
        var raw = config[$"Fx:StaticRates:{from}_{to}"];
        return decimal.TryParse(raw, NumberStyles.Number, CultureInfo.InvariantCulture, out var r) && r > 0 ? r : null;
    }
}
