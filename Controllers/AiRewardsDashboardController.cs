using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Patchy.Api.Data;
using Patchy.Api.Models;
using Patchy.Api.Services;

namespace Patchy.Api.Controllers;

public record AssistantReq(string Question, int? ChildId);
public record RedeemReq(int ChildId, string Item);

/// AI features. In this build they are SIMULATED (rule-based on the child's real data) and every answer says so.
/// Replace the bodies with calls to a real model/OCR provider; the API contract can stay the same.
[ApiController, Route("api/ai"), Authorize(Roles = "Doctor,Parent")]
public class AiController(PatchyDb db) : ControllerBase
{
    const string Disclaimer = "Decision support only. This is not a diagnosis; a doctor reviews every result.";

    async Task LogCheck(string service, bool ok, int ms)
    {
        var s = await db.AiServices.FirstOrDefaultAsync(x => x.Name == service);
        if (s == null) return;
        db.AiServiceChecks.Add(new AiServiceCheck { ServiceId = s.Id, Success = ok, ResponseMs = ms });
        await db.SaveChangesAsync();
    }

    [HttpPost("ocr"), RequestSizeLimit(8_000_000)]
    public async Task<IActionResult> Ocr(IFormFile image)
    {
        if (image == null || image.Length == 0) return BadRequest(new { error = "Choose an image." });
        if (!(image.ContentType?.StartsWith("image/") ?? false)) return BadRequest(new { error = "Only image files are accepted." });
        var sw = System.Diagnostics.Stopwatch.StartNew();
        // Simulated reading: deterministic values derived from the file size so the demo is repeatable.
        var seed = (int)(image.Length % 7);
        var result = new { Simulated = true, LeftEye = $"20/{40 + seed * 10}", RightEye = "20/20", Sphere = $"+{1 + seed * 0.25:0.00}", Cylinder = "-0.50", Axis = 90 + seed,
            Confidence = 0.72 + seed * 0.03, Disclaimer = "Please confirm these values before saving them." };
        await LogCheck("Prescription OCR", true, (int)sw.ElapsedMilliseconds + 900);
        return Ok(result);
    }

    [HttpPost("assistant")]
    public async Task<IActionResult> Assistant(AssistantReq r)
    {
        string context = "I do not have a child selected, so this is general guidance.";
        if (r.ChildId != null)
        {
            var c = await Access.GetChild(db, User, r.ChildId.Value);
            if (c == null) return NotFound();
            var sessions = await db.TherapySessions.Where(s => s.ChildId == c.Id).ToListAsync();
            var adherence = Stats.Adherence(sessions);
            var recent = sessions.Where(s => s.Status == "Completed").OrderByDescending(s => s.Date).Take(7).ToList();
            var attention = recent.Count == 0 ? 0 : (int)recent.Average(s => s.AttentionAvg);
            context = $"{c.FullName}: adherence {adherence}%, average attention over the last sessions {attention}%.";
        }
        var q = r.Question.ToLowerInvariant();
        var tip = q.Contains("patch") ? "Pair the patch with a favourite activity (drawing, a story, the game) so it feels like play, and praise the effort rather than the result."
            : q.Contains("refus") || q.Contains("cry") || q.Contains("bored") ? "Short, predictable sessions at the same time each day work best. Let the child choose the exercise and use the stars as a visible reward."
            : q.Contains("miss") || q.Contains("late") ? "A missed day is not a failure. Do a normal session the next day instead of doubling up, and let the doctor know if it keeps happening."
            : "Keep to the plan your doctor prescribed and message the doctor about anything that worries you.";
        return Ok(new { Simulated = true, Answer = $"{context} {tip}", Disclaimer });
    }

    [HttpGet("report/{childId:int}")]
    public async Task<IActionResult> Report(int childId)
    {
        var c = await Access.GetChild(db, User, childId);
        if (c == null) return NotFound();
        var sessions = await db.TherapySessions.Where(s => s.ChildId == childId && !s.IsExtra && s.Date < DateTime.UtcNow.Date).ToListAsync();
        var done = sessions.Where(s => s.Status == "Completed").ToList();
        var adherence = Stats.Adherence(sessions);
        var attention = done.Count == 0 ? 0 : (int)done.Average(s => s.AttentionAvg);
        var meas = await db.VisionMeasurements.Where(m => m.ChildId == childId).OrderBy(m => m.Date).ToListAsync();
        var trend = meas.Count >= 2 ? $"Measured acuity in the lazy eye went from {(c.LazyEye == "Left" ? meas[0].LeftEye : meas[0].RightEye)} to {(c.LazyEye == "Left" ? meas[^1].LeftEye : meas[^1].RightEye)}." : "Not enough measurements yet to show a trend.";
        var risk = adherence >= 80 ? "Low" : adherence >= 60 ? "Medium" : "High";
        await LogCheck("AI Adherence & Reports", true, 420);
        return Ok(new { Simulated = true, Child = c.FullName, Adherence = adherence, AttentionAvg = attention, SessionsDone = done.Count, Risk = risk,
            Summary = $"{c.FullName} completed {done.Count} of {sessions.Count} planned sessions ({adherence}%). Average attention was {attention}%. {trend}", Disclaimer });
    }
}

[ApiController, Route("api/rewards"), Authorize(Roles = "Parent,Child")]
public class RewardsController(PatchyDb db) : ControllerBase
{
    static readonly Dictionary<string, int> Shop = new() { ["Moon Cape"] = 900, ["Rainbow Trail"] = 650, ["Pip Hat"] = 500, ["Space Story"] = 350 };

    [HttpGet("{childId:int}")]
    public async Task<IActionResult> Get(int childId)
    {
        var c = await Access.GetChild(db, User, childId);
        if (c == null) return NotFound();
        var sessions = await db.TherapySessions.Where(s => s.ChildId == childId && !s.IsExtra).ToListAsync();
        var doneDays = sessions.Where(s => s.Status == "Completed").Select(s => s.Date.Date).ToHashSet();
        var streak = 0; var d = DateTime.UtcNow.Date;
        if (!doneDays.Contains(d)) d = d.AddDays(-1);
        while (doneDays.Contains(d)) { streak++; d = d.AddDays(-1); }
        var perfectPatch = sessions.Count(s => s.Status == "Completed" && s.PatchConfirmed && s.FaceLostCount == 0);
        return Ok(new
        {
            c.Points, Level = c.Points / 250 + 1, Streak = streak,
            Events = await db.RewardEvents.Where(e => e.ChildId == childId).OrderByDescending(e => e.CreatedAt).Take(20).ToListAsync(),
            Shop = Shop.Select(x => new { Name = x.Key, Cost = x.Value }),
            Badges = new[] { new { Name = "6-day hero", Earned = streak >= 6 }, new { Name = "Perfect patch", Earned = perfectPatch >= 5 },
                new { Name = "Quest master", Earned = doneDays.Count >= 20 }, new { Name = "Focus friend", Earned = doneDays.Count >= 10 } }
        });
    }

    [HttpPost("redeem")]
    public async Task<IActionResult> Redeem(RedeemReq r)
    {
        var c = await Access.GetChild(db, User, r.ChildId);
        if (c == null) return NotFound();
        if (!Shop.TryGetValue(r.Item, out var cost)) return BadRequest(new { error = "Unknown reward." });
        if (c.Points < cost) return BadRequest(new { error = "Not enough points yet." });
        c.Points -= cost;
        db.RewardEvents.Add(new RewardEvent { ChildId = c.Id, Points = -cost, Reason = $"Unlocked {r.Item}" });
        await db.SaveChangesAsync();
        return Ok(new { c.Points });
    }
}

[ApiController, Route("api/dashboard"), Authorize]
public class DashboardController(PatchyDb db) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Get()
    {
        var role = User.RoleName();
        var today = DateTime.UtcNow.Date;
        if (role == "Admin")
        {
            var users = await db.Users.ToListAsync();
            return Ok(new
            {
                Role = role,
                Accounts = new { Total = users.Count, Active = users.Count(u => u.IsActive) },
                OpenTickets = await db.SupportTickets.CountAsync(t => t.Status != "Resolved"),
                Games = await db.Games.CountAsync(g => g.IsActive),
                AiServices = await db.AiServices.Select(s => new { s.Id, s.Name, s.Status }).ToListAsync(),
                LatestTickets = await db.SupportTickets.OrderByDescending(t => t.CreatedAt).Take(5).Select(t => new { t.Id, t.Number, t.Title, t.Category, t.Status }).ToListAsync(),
                Activity = await db.AuditLogs.OrderByDescending(a => a.CreatedAt).Take(5).Select(a => new { a.Action, a.CreatedAt }).ToListAsync()
            });
        }

        var kids = await Access.Visible(db, User).ToListAsync();
        var ids = kids.Select(k => k.Id).ToList();
        var sessions = (await db.TherapySessions.Where(s => ids.Contains(s.ChildId)).ToListAsync()).ToLookup(s => s.ChildId);

        if (role == "Doctor")
        {
            var me = User.Uid();
            var unread = await db.Messages.CountAsync(m => ids.Contains(m.ChildId) && !m.IsRead && m.SenderId != me);
            var flagged = kids.Where(k => k.FollowUp != "OnTrack").Select(k => new { k.Id, k.FullName, k.FollowUp, Adherence = Stats.Adherence(sessions[k.Id]),
                Reason = $"Adherence {Stats.Adherence(sessions[k.Id])}%" }).ToList();
            return Ok(new { Role = role, TotalChildren = kids.Count, NeedFollowUp = flagged.Count, UnreadMessages = unread, Flagged = flagged,
                Tasks = new[] { $"Review {flagged.Count} flagged child(ren)", $"Reply to {unread} unread message(s)" } });
        }

        var cards = kids.Select(k =>
        {
            var ss = sessions[k.Id].ToList();
            var t = ss.FirstOrDefault(s => s.Date == today && !s.IsExtra);
            var planned = ss.Where(s => !s.IsExtra).ToList();
            return new
            {
                k.Id, k.FullName, k.Points, k.FollowUp, Adherence = Stats.Adherence(ss),
                Today = t == null ? null : new { t.Id, t.Status },
                Summary = new { Completed = planned.Count(s => s.Status == "Completed"), Missed = planned.Count(s => s.Status == "Missed"), Remaining = planned.Count(s => s.Status == "Scheduled") },
                Week = Enumerable.Range(0, 7).Select(i => today.AddDays(i - 6)).Select(day => new { Day = day, Status = ss.FirstOrDefault(s => s.Date == day && !s.IsExtra)?.Status ?? "None" })
            };
        }).ToList();
        return Ok(new { Role = role, Children = cards });
    }
}
