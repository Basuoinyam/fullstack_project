using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Patchy.Api.Data;
using Patchy.Api.Models;
using Patchy.Api.Services;

namespace Patchy.Api.Controllers;

public record StartSessionReq(bool PatchConfirmed);
public record CompleteSessionReq(int DurationSeconds, int Score, double AttentionAvg, int FaceLostCount, bool PatchConfirmed, bool Completed);
public record ExtraSessionReq(int ChildId, int? GameId);

[ApiController, Route("api/sessions"), Authorize]
public class SessionsController(PatchyDb db) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List([FromQuery] int? childId, [FromQuery] int? planId, [FromQuery] string? status)
    {
        var kids = await Access.Visible(db, User).ToListAsync();
        var ids = kids.Select(k => k.Id).ToList();
        var q = db.TherapySessions.Where(s => ids.Contains(s.ChildId) && s.Date <= DateTime.UtcNow.Date);
        if (childId != null) q = q.Where(s => s.ChildId == childId);
        if (planId != null) q = q.Where(s => s.PlanId == planId);
        if (!string.IsNullOrWhiteSpace(status)) q = q.Where(s => s.Status == status);
        var list = await q.OrderByDescending(s => s.Date).Take(200).ToListAsync();
        var games = await db.Games.ToDictionaryAsync(g => g.Id, g => g.Name);
        var summary = list.Where(s => !s.IsExtra).ToList();
        return Ok(new
        {
            Summary = new { Total = summary.Count, Completed = summary.Count(s => s.Status == "Completed"), Incomplete = summary.Count(s => s.Status == "Incomplete"), Missed = summary.Count(s => s.Status == "Missed") },
            Items = list.Select(s => new
            {
                s.Id, s.ChildId, ChildName = kids.First(k => k.Id == s.ChildId).FullName, s.PlanId, s.Date, s.StartedAt, s.EndedAt, s.DurationSeconds, s.Score,
                s.AttentionAvg, s.PatchConfirmed, s.FaceLostCount, s.Status, s.IsExtra, GameName = s.GameId == null ? null : games.GetValueOrDefault(s.GameId.Value)
            })
        });
    }

    [HttpGet("{id:int}")]
    public async Task<IActionResult> Get(int id)
    {
        var s = await db.TherapySessions.FindAsync(id);
        if (s == null || await Access.GetChild(db, User, s.ChildId) == null) return NotFound();
        var game = s.GameId == null ? null : await db.Games.FindAsync(s.GameId.Value);
        return Ok(new { s.Id, s.ChildId, s.PlanId, s.Date, s.StartedAt, s.EndedAt, s.DurationSeconds, s.Score, s.AttentionAvg, s.FaceLostCount, s.PatchConfirmed, s.Status, s.IsExtra, GameName = game?.Name });
    }

    /// Today's quest for a child: the planned session plus the exercises prescribed in the plan.
    [HttpGet("today")]
    public async Task<IActionResult> Today([FromQuery] int childId)
    {
        if (await Access.GetChild(db, User, childId) == null) return NotFound();
        var today = DateTime.UtcNow.Date;
        var s = await db.TherapySessions.Where(x => x.ChildId == childId && x.Date == today && !x.IsExtra).FirstOrDefaultAsync();
        if (s == null) return Ok(new { Session = (object?)null, Games = Array.Empty<object>() });
        var games = await (from pg in db.PlanGames where pg.PlanId == s.PlanId join g in db.Games on pg.GameId equals g.Id where g.IsActive
                           select new { g.Id, g.Name, g.Category, g.Description, pg.Minutes }).ToListAsync();
        return Ok(new { Session = new { s.Id, s.Status, s.Date, s.GameId, s.DurationSeconds, s.Score }, Games = games });
    }

    [Authorize(Roles = "Parent,Child"), HttpPost("{id:int}/start")]
    public async Task<IActionResult> Start(int id, StartSessionReq r)
    {
        var s = await db.TherapySessions.FindAsync(id);
        if (s == null || await Access.GetChild(db, User, s.ChildId) == null) return NotFound();
        if (s.Status is "Completed" or "Missed") return BadRequest(new { error = "This session can no longer be started." });
        s.StartedAt = DateTime.UtcNow; s.PatchConfirmed = r.PatchConfirmed;
        await db.SaveChangesAsync();
        return Ok(new { s.Id, s.StartedAt });
    }

    /// The browser sends only scores (duration, attention average, face-lost count). Raw video never reaches the server.
    [Authorize(Roles = "Parent,Child"), HttpPost("{id:int}/complete")]
    public async Task<IActionResult> Complete(int id, CompleteSessionReq r)
    {
        var s = await db.TherapySessions.FindAsync(id);
        if (s == null) return NotFound();
        var child = await Access.GetChild(db, User, s.ChildId);
        if (child == null) return NotFound();
        if (s.Status == "Completed") return BadRequest(new { error = "Already completed." });

        s.EndedAt = DateTime.UtcNow; s.StartedAt ??= s.EndedAt.Value.AddSeconds(-r.DurationSeconds);
        s.DurationSeconds = Math.Max(0, r.DurationSeconds); s.Score = Math.Clamp(r.Score, 0, 100);
        s.AttentionAvg = Math.Clamp(r.AttentionAvg, 0, 100); s.FaceLostCount = Math.Max(0, r.FaceLostCount); s.PatchConfirmed = r.PatchConfirmed;
        s.Status = r.Completed ? "Completed" : "Incomplete";

        var earned = 0;
        if (r.Completed)
        {
            earned = 20 + s.Score / 2 + (r.PatchConfirmed ? 10 : 0);
            child.Points += earned;
            db.RewardEvents.Add(new RewardEvent { ChildId = child.Id, Points = earned, Reason = "Finished a quest" });
        }
        await db.SaveChangesAsync();

        var all = await db.TherapySessions.Where(x => x.ChildId == child.Id).ToListAsync();
        var adherence = Stats.Adherence(all);
        child.FollowUp = Stats.FollowUpFor(adherence);
        await db.SaveChangesAsync();

        if (child.DoctorId != null && (!r.Completed || s.FaceLostCount >= 6))
            await Stats.Notify(db, child.DoctorId.Value, "Session needs a look",
                $"{child.FullName}: {(r.Completed ? "face was lost often" : "session was not finished")} (adherence {adherence}%).", "session");
        return Ok(new { s.Id, s.Status, PointsEarned = earned, TotalPoints = child.Points, Adherence = adherence });
    }

    [Authorize(Roles = "Parent,Child"), HttpPost("extra")]
    public async Task<IActionResult> Extra(ExtraSessionReq r)
    {
        var child = await Access.GetChild(db, User, r.ChildId);
        if (child == null) return NotFound();
        var s = new TherapySession { ChildId = child.Id, GameId = r.GameId, Date = DateTime.UtcNow.Date, IsExtra = true };
        db.TherapySessions.Add(s);
        await db.SaveChangesAsync();
        return Ok(new { s.Id });
    }
}
