using Microsoft.EntityFrameworkCore;
using Patchy.Api.Data;
using Patchy.Api.Models;

namespace Patchy.Api.Services;

/// Runs hourly: planned sessions from earlier days that were never finished become "Missed",
/// the child's follow-up status is recalculated, and parent + doctor get an alert.
public class MissedSessionsJob(IServiceProvider sp, ILogger<MissedSessionsJob> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        await Task.Delay(TimeSpan.FromSeconds(20), ct);
        while (!ct.IsCancellationRequested)
        {
            try { await RunOnce(ct); }
            catch (Exception ex) { log.LogError(ex, "Missed-sessions job failed"); }
            await Task.Delay(TimeSpan.FromHours(1), ct);
        }
    }

    async Task RunOnce(CancellationToken ct)
    {
        using var scope = sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PatchyDb>();
        var today = DateTime.UtcNow.Date;
        var stale = await db.TherapySessions.Where(s => s.Status == "Scheduled" && s.Date < today).ToListAsync(ct);
        foreach (var s in stale) s.Status = "Missed";
        await db.SaveChangesAsync(ct);

        foreach (var childId in stale.Select(s => s.ChildId).Distinct())
        {
            var child = await db.Children.FindAsync([childId], ct);
            if (child == null) continue;
            var all = await db.TherapySessions.Where(s => s.ChildId == childId).ToListAsync(ct);
            child.FollowUp = Stats.FollowUpFor(Stats.Adherence(all));
            var missed = stale.Count(s => s.ChildId == childId);
            db.Notifications.Add(new Notification { UserId = child.ParentId, Kind = "session", Title = "Missed session",
                Body = $"{child.FullName} missed {missed} session(s). A short session today helps keep the plan on track." });
            if (child.DoctorId != null)
                db.Notifications.Add(new Notification { UserId = child.DoctorId.Value, Kind = "session", Title = "Missed session",
                    Body = $"{child.FullName} missed {missed} session(s). Adherence is now {Stats.Adherence(all)}%." });
        }
        await db.SaveChangesAsync(ct);
        if (stale.Count > 0) log.LogInformation("Marked {Count} sessions as missed", stale.Count);
    }
}
