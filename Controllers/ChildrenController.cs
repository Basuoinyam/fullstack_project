using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Patchy.Api.Data;
using Patchy.Api.Models;
using Patchy.Api.Services;

namespace Patchy.Api.Controllers;

public record CreateChildReq(string FullName, DateTime BirthDate, string LazyEye, string? Degree, string? Cause, string? DoctorCode);
public record UpdateChildReq(string FullName, DateTime BirthDate, string LazyEye, string? Degree, string? Cause, string? Status, DateTime? TreatmentStart, DateTime? TreatmentEnd);
public record LinkDoctorReq(string DoctorCode);
public record MeasurementReq(DateTime? Date, string LeftEye, string RightEye, string? Notes, string? PrescriptionImageUrl);

[ApiController, Route("api/children"), Authorize]
public class ChildrenController(PatchyDb db) : ControllerBase
{
    static int Age(DateTime b) { var t = DateTime.UtcNow; var a = t.Year - b.Year; if (b.Date > t.AddYears(-a)) a--; return a; }

    [HttpGet]
    public async Task<IActionResult> List([FromQuery] string? q, [FromQuery] string? followUp, [FromQuery] string? status)
    {
        var query = Access.Visible(db, User);
        if (!string.IsNullOrWhiteSpace(q)) query = query.Where(c => c.FullName.Contains(q));
        if (!string.IsNullOrWhiteSpace(followUp)) query = query.Where(c => c.FollowUp == followUp);
        if (!string.IsNullOrWhiteSpace(status)) query = query.Where(c => c.Status == status);
        var kids = await query.OrderBy(c => c.FullName).ToListAsync();
        var ids = kids.Select(k => k.Id).ToList();
        var sessions = (await db.TherapySessions.Where(s => ids.Contains(s.ChildId)).ToListAsync()).ToLookup(s => s.ChildId);
        var meas = (await db.VisionMeasurements.Where(m => ids.Contains(m.ChildId)).ToListAsync()).GroupBy(m => m.ChildId)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(m => m.Date).First());
        var userIds = kids.Select(k => k.ParentId).Concat(kids.Where(k => k.DoctorId != null).Select(k => k.DoctorId!.Value)).Distinct().ToList();
        var users = await db.Users.Where(u => userIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.FullName);
        return Ok(kids.Select(c => new
        {
            c.Id, c.FullName, Age = Age(c.BirthDate), c.LazyEye, c.Status, c.FollowUp, c.Degree, c.Points,
            Adherence = Stats.Adherence(sessions[c.Id]),
            LatestAcuity = meas.TryGetValue(c.Id, out var m) ? (c.LazyEye == "Left" ? m.LeftEye : m.RightEye) : null,
            ParentName = users.GetValueOrDefault(c.ParentId),
            DoctorName = c.DoctorId == null ? null : users.GetValueOrDefault(c.DoctorId.Value),
        }));
    }

    [HttpGet("{id:int}")]
    public async Task<IActionResult> Get(int id)
    {
        var c = await Access.GetChild(db, User, id);
        if (c == null) return NotFound();
        var parent = await db.Users.FindAsync(c.ParentId);
        var doctor = c.DoctorId == null ? null : await db.Users.FindAsync(c.DoctorId.Value);
        var sessions = await db.TherapySessions.Where(s => s.ChildId == id).ToListAsync();
        var plan = await db.TreatmentPlans.Where(p => p.ChildId == id && p.Status != "Complete").OrderByDescending(p => p.Id).FirstOrDefaultAsync();
        var planned = sessions.Where(s => !s.IsExtra).ToList();
        return Ok(new
        {
            c.Id, c.FullName, c.BirthDate, Age = Age(c.BirthDate), c.LazyEye, c.Degree, c.Cause, c.Status, c.FollowUp, c.TreatmentStart, c.TreatmentEnd, c.Points,
            LoginCode = User.RoleName() == "Child" ? null : c.LoginCode,
            Parent = parent == null ? null : new { parent.Id, parent.FullName, parent.Phone, parent.Email },
            Doctor = doctor == null ? null : new { doctor.Id, doctor.FullName, doctor.DoctorCode },
            Measurements = await db.VisionMeasurements.Where(m => m.ChildId == id).OrderByDescending(m => m.Date).ToListAsync(),
            ActivePlan = plan == null ? null : new { plan.Id, plan.PlanNumber },
            Adherence = Stats.Adherence(sessions),
            Summary = new
            {
                Total = planned.Count, Completed = planned.Count(s => s.Status == "Completed"), Incomplete = planned.Count(s => s.Status == "Incomplete"),
                Missed = planned.Count(s => s.Status == "Missed"), Remaining = planned.Count(s => s.Status == "Scheduled")
            }
        });
    }

    [Authorize(Roles = "Parent"), HttpPost]
    public async Task<IActionResult> Create(CreateChildReq r)
    {
        var child = new Child { FullName = r.FullName.Trim(), BirthDate = r.BirthDate, ParentId = User.Uid(), LazyEye = r.LazyEye, Degree = r.Degree, Cause = r.Cause, TreatmentStart = DateTime.UtcNow.Date };
        if (!string.IsNullOrWhiteSpace(r.DoctorCode))
        {
            var doc = await db.Users.FirstOrDefaultAsync(u => u.Role == "Doctor" && u.DoctorCode == r.DoctorCode!.Trim().ToUpper());
            if (doc == null) return BadRequest(new { error = "Doctor code not found." });
            child.DoctorId = doc.Id;
        }
        do child.LoginCode = Stats.NewCode(); while (await db.Children.AnyAsync(c => c.LoginCode == child.LoginCode));
        db.Children.Add(child);
        await db.SaveChangesAsync();
        if (child.DoctorId != null) await Stats.Notify(db, child.DoctorId.Value, "New patient linked", $"{child.FullName} was linked to you by the parent.", "info");
        return Ok(new { child.Id, child.FullName, child.LoginCode });
    }

    [Authorize(Roles = "Parent,Doctor"), HttpPut("{id:int}")]
    public async Task<IActionResult> Update(int id, UpdateChildReq r)
    {
        var c = await Access.GetChild(db, User, id);
        if (c == null) return NotFound();
        c.FullName = r.FullName.Trim(); c.BirthDate = r.BirthDate; c.LazyEye = r.LazyEye; c.Degree = r.Degree; c.Cause = r.Cause;
        if (User.RoleName() == "Doctor") { c.Status = r.Status ?? c.Status; c.TreatmentStart = r.TreatmentStart ?? c.TreatmentStart; c.TreatmentEnd = r.TreatmentEnd; }
        await db.SaveChangesAsync();
        return Ok(new { c.Id });
    }

    [Authorize(Roles = "Parent,Admin"), HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id)
    {
        var c = await Access.GetChild(db, User, id);
        if (c == null) return NotFound();
        var planIds = await db.TreatmentPlans.Where(p => p.ChildId == id).Select(p => p.Id).ToListAsync();
        db.TherapySessions.RemoveRange(db.TherapySessions.Where(s => s.ChildId == id));
        db.PlanGames.RemoveRange(db.PlanGames.Where(g => planIds.Contains(g.PlanId)));
        db.TreatmentPlans.RemoveRange(db.TreatmentPlans.Where(p => p.ChildId == id));
        db.VisionMeasurements.RemoveRange(db.VisionMeasurements.Where(m => m.ChildId == id));
        db.Messages.RemoveRange(db.Messages.Where(m => m.ChildId == id));
        db.RewardEvents.RemoveRange(db.RewardEvents.Where(r => r.ChildId == id));
        db.Children.Remove(c);
        await db.SaveChangesAsync();
        return NoContent();
    }

    [Authorize(Roles = "Parent"), HttpPost("{id:int}/link-doctor")]
    public async Task<IActionResult> LinkDoctor(int id, LinkDoctorReq r)
    {
        var c = await Access.GetChild(db, User, id);
        if (c == null) return NotFound();
        var doc = await db.Users.FirstOrDefaultAsync(u => u.Role == "Doctor" && u.DoctorCode == r.DoctorCode.Trim().ToUpper());
        if (doc == null) return BadRequest(new { error = "Doctor code not found." });
        c.DoctorId = doc.Id;
        await db.SaveChangesAsync();
        await Stats.Notify(db, doc.Id, "New patient linked", $"{c.FullName} was linked to you by the parent.");
        return Ok(new { doctor = new { doc.Id, doc.FullName, doc.DoctorCode } });
    }

    [Authorize(Roles = "Parent"), HttpDelete("{id:int}/doctor")]
    public async Task<IActionResult> UnlinkDoctor(int id)
    {
        var c = await Access.GetChild(db, User, id);
        if (c == null) return NotFound();
        c.DoctorId = null;
        await db.SaveChangesAsync();
        return NoContent();
    }

    [Authorize(Roles = "Parent,Doctor"), HttpPost("{id:int}/login-code")]
    public async Task<IActionResult> NewLoginCode(int id)
    {
        var c = await Access.GetChild(db, User, id);
        if (c == null) return NotFound();
        do c.LoginCode = Stats.NewCode(); while (await db.Children.AnyAsync(x => x.LoginCode == c.LoginCode));
        await db.SaveChangesAsync();
        return Ok(new { c.LoginCode });
    }

    [HttpGet("{id:int}/measurements")]
    public async Task<IActionResult> Measurements(int id)
    {
        if (await Access.GetChild(db, User, id) == null) return NotFound();
        return Ok(await db.VisionMeasurements.Where(m => m.ChildId == id).OrderByDescending(m => m.Date).ToListAsync());
    }

    [Authorize(Roles = "Parent,Doctor"), HttpPost("{id:int}/measurements")]
    public async Task<IActionResult> AddMeasurement(int id, MeasurementReq r)
    {
        var c = await Access.GetChild(db, User, id);
        if (c == null) return NotFound();
        var m = new VisionMeasurement { ChildId = id, Date = r.Date ?? DateTime.UtcNow.Date, LeftEye = r.LeftEye, RightEye = r.RightEye, Notes = r.Notes, PrescriptionImageUrl = r.PrescriptionImageUrl };
        db.VisionMeasurements.Add(m);
        await db.SaveChangesAsync();
        return Ok(m);
    }

    /// Weekly trend used by the progress charts (adherence + average attention per week, last 8 weeks).
    [HttpGet("{id:int}/progress")]
    public async Task<IActionResult> Progress(int id)
    {
        var c = await Access.GetChild(db, User, id);
        if (c == null) return NotFound();
        var today = DateTime.UtcNow.Date;
        var from = today.AddDays(-56);
        var sessions = await db.TherapySessions.Where(s => s.ChildId == id && s.Date >= from && s.Date < today && !s.IsExtra).ToListAsync();
        var weeks = Enumerable.Range(0, 8).Select(i =>
        {
            var start = from.AddDays(i * 7); var end = start.AddDays(7);
            var ws = sessions.Where(s => s.Date >= start && s.Date < end).ToList();
            var done = ws.Where(s => s.Status == "Completed").ToList();
            return new
            {
                Week = $"W{i + 1}", Adherence = ws.Count == 0 ? 0 : (int)Math.Round(100.0 * done.Count / ws.Count),
                Attention = done.Count == 0 ? 0 : (int)Math.Round(done.Average(s => s.AttentionAvg)), Sessions = done.Count
            };
        });
        return Ok(new { Weeks = weeks, Measurements = await db.VisionMeasurements.Where(m => m.ChildId == id).OrderBy(m => m.Date).ToListAsync() });
    }
}
