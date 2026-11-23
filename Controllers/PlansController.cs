using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Patchy.Api.Data;
using Patchy.Api.Models;
using Patchy.Api.Services;

namespace Patchy.Api.Controllers;

public record PlanGameReq(int GameId, int Minutes);
public record PlanReq(int ChildId, DateTime StartDate, DateTime EndDate, int DailyMinutes, bool UsesPatch, bool UsesGlasses, double PatchHours, List<PlanGameReq> Games);
public record CompletePlanReq(string? ResultNote);

[ApiController, Route("api/plans"), Authorize(Roles = "Admin,Doctor,Parent")]
public class PlansController(PatchyDb db) : ControllerBase
{
    static string Methods(TreatmentPlan p) => string.Join(" + ", new[] { p.UsesPatch ? "Patch" : null, p.UsesGlasses ? "Glasses" : null, "Digital" }.Where(x => x != null));

    [HttpGet]
    public async Task<IActionResult> List([FromQuery] int? childId, [FromQuery] string? status)
    {
        var kids = await Access.Visible(db, User).ToListAsync();
        var ids = kids.Select(k => k.Id).ToList();
        var q = db.TreatmentPlans.Where(p => ids.Contains(p.ChildId));
        if (childId != null) q = q.Where(p => p.ChildId == childId);
        if (!string.IsNullOrWhiteSpace(status)) q = q.Where(p => p.Status == status);
        var plans = await q.OrderByDescending(p => p.Id).ToListAsync();
        var planIds = plans.Select(p => p.Id).ToList();
        var sessions = (await db.TherapySessions.Where(s => s.PlanId != null && planIds.Contains(s.PlanId.Value)).ToListAsync()).ToLookup(s => s.PlanId!.Value);
        return Ok(plans.Select(p => new
        {
            p.Id, p.PlanNumber, p.ChildId, ChildName = kids.First(k => k.Id == p.ChildId).FullName, p.StartDate, p.EndDate, p.DailyMinutes,
            Methods = Methods(p), p.Status, Adherence = Stats.Adherence(sessions[p.Id])
        }));
    }

    [HttpGet("{id:int}")]
    public async Task<IActionResult> Get(int id)
    {
        var p = await db.TreatmentPlans.FindAsync(id);
        if (p == null) return NotFound();
        var child = await Access.GetChild(db, User, p.ChildId);
        if (child == null) return NotFound();
        var sessions = await db.TherapySessions.Where(s => s.PlanId == id).ToListAsync();
        var games = await (from pg in db.PlanGames where pg.PlanId == id join g in db.Games on pg.GameId equals g.Id select new { g.Id, g.Name, g.Category, pg.Minutes }).ToListAsync();
        var planned = sessions.Where(s => !s.IsExtra).ToList();
        return Ok(new
        {
            p.Id, p.PlanNumber, p.ChildId, ChildName = child.FullName, p.StartDate, p.EndDate, p.DailyMinutes, p.UsesPatch, p.UsesGlasses, p.PatchHours,
            p.Status, p.ResultNote, Methods = Methods(p), Games = games, Adherence = Stats.Adherence(sessions),
            Summary = new { Total = planned.Count, Completed = planned.Count(s => s.Status == "Completed"), Missed = planned.Count(s => s.Status == "Missed"), Remaining = planned.Count(s => s.Status == "Scheduled") }
        });
    }

    [Authorize(Roles = "Doctor"), HttpPost]
    public async Task<IActionResult> Create(PlanReq r)
    {
        var child = await Access.GetChild(db, User, r.ChildId);
        if (child == null) return NotFound(new { error = "Child not found or not linked to you." });
        if (r.EndDate.Date <= r.StartDate.Date) return BadRequest(new { error = "End date must be after the start date." });
        if ((r.EndDate - r.StartDate).TotalDays > 180) return BadRequest(new { error = "A plan can be at most 180 days." });
        if (r.Games.Count == 0) return BadRequest(new { error = "Choose at least one exercise." });

        foreach (var old in await db.TreatmentPlans.Where(x => x.ChildId == r.ChildId && x.Status != "Complete").ToListAsync()) old.Status = "Complete";
        var n = 2100 + await db.TreatmentPlans.CountAsync();
        while (await db.TreatmentPlans.AnyAsync(x => x.PlanNumber == $"TP-{n}")) n++;
        var plan = new TreatmentPlan { PlanNumber = $"TP-{n}", ChildId = r.ChildId, DoctorId = User.Uid(), StartDate = r.StartDate.Date, EndDate = r.EndDate.Date,
            DailyMinutes = r.DailyMinutes, UsesPatch = r.UsesPatch, UsesGlasses = r.UsesGlasses, PatchHours = r.PatchHours };
        db.TreatmentPlans.Add(plan);
        await db.SaveChangesAsync();
        db.PlanGames.AddRange(r.Games.Select(g => new PlanGame { PlanId = plan.Id, GameId = g.GameId, Minutes = g.Minutes }));
        AddSessions(plan, r.Games, plan.StartDate);
        await db.SaveChangesAsync();
        await Stats.Notify(db, child.ParentId, "New treatment plan", $"Plan {plan.PlanNumber} for {child.FullName} is ready.", "info");
        return Ok(new { plan.Id, plan.PlanNumber });
    }

    void AddSessions(TreatmentPlan plan, List<PlanGameReq> games, DateTime from)
    {
        var i = 0;
        for (var d = from; d <= plan.EndDate; d = d.AddDays(1))
            db.TherapySessions.Add(new TherapySession { ChildId = plan.ChildId, PlanId = plan.Id, GameId = games[i++ % games.Count].GameId, Date = d });
    }

    [Authorize(Roles = "Doctor"), HttpPut("{id:int}")]
    public async Task<IActionResult> Update(int id, PlanReq r)
    {
        var plan = await db.TreatmentPlans.FindAsync(id);
        if (plan == null || await Access.GetChild(db, User, plan.ChildId) == null) return NotFound();
        if (r.EndDate.Date <= r.StartDate.Date || r.Games.Count == 0) return BadRequest(new { error = "Check the dates and exercises." });
        plan.StartDate = r.StartDate.Date; plan.EndDate = r.EndDate.Date; plan.DailyMinutes = r.DailyMinutes;
        plan.UsesPatch = r.UsesPatch; plan.UsesGlasses = r.UsesGlasses; plan.PatchHours = r.PatchHours;
        db.PlanGames.RemoveRange(db.PlanGames.Where(g => g.PlanId == id));
        db.PlanGames.AddRange(r.Games.Select(g => new PlanGame { PlanId = id, GameId = g.GameId, Minutes = g.Minutes }));
        var today = DateTime.UtcNow.Date;
        db.TherapySessions.RemoveRange(db.TherapySessions.Where(s => s.PlanId == id && s.Status == "Scheduled" && !s.IsExtra && s.Date >= today));
        await db.SaveChangesAsync();
        AddSessions(plan, r.Games, today > plan.StartDate ? today : plan.StartDate);
        await db.SaveChangesAsync();
        return Ok(new { plan.Id });
    }

    [Authorize(Roles = "Doctor"), HttpPost("{id:int}/complete")]
    public async Task<IActionResult> Complete(int id, CompletePlanReq r)
    {
        var plan = await db.TreatmentPlans.FindAsync(id);
        if (plan == null || await Access.GetChild(db, User, plan.ChildId) == null) return NotFound();
        plan.Status = "Complete"; plan.ResultNote = r.ResultNote;
        await db.SaveChangesAsync();
        return Ok(new { plan.Id });
    }
}
