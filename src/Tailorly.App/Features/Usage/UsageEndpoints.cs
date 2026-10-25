using Microsoft.AspNetCore.Http.HttpResults;
using Tailorly.App.Ai;
using Tailorly.App.Api;
using Tailorly.App.Infrastructure.Data;
using Tailorly.Core.Budgets;

namespace Tailorly.App.Features.Usage;

public sealed record UsageBucketDto(string Bucket, long Limit, long Used, long Remaining, int PercentUsed);

/// <param name="ResetsOn">First day of the next period (Asia/Kolkata), <c>YYYY-MM-DD</c>.</param>
/// <param name="Warnings"><c>{bucket}_80_percent</c> or <c>{bucket}_100_percent</c>.</param>
public sealed record UsageResponse(string PeriodKey, string ResetsOn, IReadOnlyList<UsageBucketDto> Buckets, IReadOnlyList<string> Warnings);

/// <summary><c>GET /usage</c> (ApiDesign §4.9): this month's Discovery and Documents budgets.</summary>
public static class UsageEndpoints
{
    public static void MapUsageEndpoints(this RouteGroupBuilder api) =>
        api.MapGet("/usage", GetUsage).WithName("GetUsage").WithTags("Usage").RequireAuthorization(AuthPolicies.User);

    private static async Task<Ok<UsageResponse>> GetUsage(BudgetService budgets, ICurrentUser current, CancellationToken ct)
    {
        var buckets = await budgets.GetAllAsync(current.UserId, ct);
        return TypedResults.Ok(new UsageResponse(
            budgets.CurrentPeriod,
            BudgetPolicy.FormatResetsOn(budgets.ResetsOn),
            buckets.Select(b => new UsageBucketDto(b.Bucket, b.Limit, b.Used, b.Remaining, b.PercentUsed)).ToList(),
            BudgetPolicy.Warnings(buckets)));
    }
}
