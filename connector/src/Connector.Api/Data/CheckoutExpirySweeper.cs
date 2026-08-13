using System.Text.Json;
using Connector.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace Connector.Api.Data;

/// <summary>
/// Background sweep clearing expired checkouts (specs/02 FR2.3) — runs every
/// few minutes and logs an auto-released audit event per cleared checkout.
/// </summary>
public class CheckoutExpirySweeper(IServiceScopeFactory scopeFactory, ILogger<CheckoutExpirySweeper> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<ConnectorDbContext>();
                var now = DateTimeOffset.UtcNow;
                var expired = await db.CheckoutStates.Include(c => c.CheckedOutBy).Include(c => c.Model)
                    .Where(c => c.ExpiresAt <= now).ToListAsync(ct);

                foreach (var c in expired)
                {
                    db.CheckoutStates.Remove(c);
                    db.AuditEvents.Add(new AuditEvent
                    {
                        ProjectId = c.Model!.ProjectId, EventType = "checkout.released",
                        ActorSnapshotJson = JsonSerializer.Serialize(new
                        {
                            userId = c.CheckedOutById, name = c.CheckedOutBy?.Name, email = c.CheckedOutBy?.Email
                        }),
                        ModelId = c.ModelId,
                        PayloadJson = JsonSerializer.Serialize(new { releasedBy = "auto-expiry" }),
                        Timestamp = now
                    });
                }
                if (expired.Count > 0)
                {
                    await db.SaveChangesAsync(ct);
                    logger.LogInformation("Auto-released {Count} expired checkout(s).", expired.Count);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Checkout expiry sweep failed; retrying next cycle.");
            }
            await Task.Delay(TimeSpan.FromMinutes(5), ct);
        }
    }
}
