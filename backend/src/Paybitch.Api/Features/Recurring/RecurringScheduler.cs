using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Paybitch.Infrastructure;
using Paybitch.Infrastructure.Abstractions;

namespace Paybitch.Api.Features.Recurring;

/// <summary>
/// The E3 materialization worker (EXT-D3j). A polling <see cref="BackgroundService"/> — safe to run on every
/// app instance — that each pass drains due rules one at a time through <see cref="RecurringFireService"/>.
/// Each rule is claimed <c>FOR UPDATE SKIP LOCKED</c> and processed in its OWN transaction and DI scope, so
/// two instances during a deploy never collide, tracking never bleeds between rules, and a failure on one
/// rule can never roll back another. The deterministic <c>client_id</c> (EXT-D3f) is the belt under these braces.
/// </summary>
public sealed class RecurringScheduler : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(60);
    private const int MaxRulesPerPass = 500; // bound a single tick; the next tick continues the backlog

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<RecurringScheduler> _logger;

    public RecurringScheduler(IServiceScopeFactory scopeFactory, ILogger<RecurringScheduler> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("RecurringScheduler started (poll {Interval}s)", PollInterval.TotalSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await DrainAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "RecurringScheduler drain failed"); // never let a bad pass kill the loop
            }

            try
            {
                await Task.Delay(PollInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        _logger.LogInformation("RecurringScheduler stopping");
    }

    /// <summary>Claim + fire due rules one-per-scope-per-transaction until none remain or the pass cap is hit.</summary>
    private async Task DrainAsync(CancellationToken ct)
    {
        var fired = 0;
        for (var i = 0; i < MaxRulesPerPass; i++)
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var sp = scope.ServiceProvider;
            var db = sp.GetRequiredService<AppDbContext>();
            var changeLog = sp.GetRequiredService<IChangeLogWriter>();
            var clock = sp.GetRequiredService<IClock>();
            var fireService = sp.GetRequiredService<RecurringFireService>();

            FireOutcome outcome;
            try
            {
                outcome = await fireService.ProcessOneDueRuleAsync(db, changeLog, clock, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // A per-rule failure (transient DB error, etc.) rolls back that rule's tx; stop this drain and
                // let the next tick retry from a clean slate.
                _logger.LogError(ex, "RecurringScheduler failed processing a due rule");
                break;
            }

            if (outcome == FireOutcome.NoWork)
                break;
            if (outcome is FireOutcome.Fired or FireOutcome.Advanced)
                fired++;
        }

        if (fired > 0)
            _logger.LogInformation("RecurringScheduler pass materialized/advanced {Count} rule(s)", fired);
    }
}
