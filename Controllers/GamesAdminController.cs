using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Patchy.Api.Data;
using Patchy.Api.Models;
using Patchy.Api.Services;

namespace Patchy.Api.Controllers;

public record GameReq(string Name, string Type, string? Category, string? ImageUrl, string? Url, string? Description, bool IsActive);
public record ActiveReq(bool IsActive);
public record InviteReq(string FullName, string Email, string? Permissions);

[ApiController, Route("api/games"), Authorize]
public class GamesController(PatchyDb db) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List([FromQuery] string? q, [FromQuery] string? type, [FromQuery] bool? active)
    {
        var query = db.Games.AsQueryable();
        if (User.RoleName() != "Admin") query = query.Where(g => g.IsActive);
        else if (active != null) query = query.Where(g => g.IsActive == active);
        if (!string.IsNullOrWhiteSpace(q)) query = query.Where(g => g.Name.Contains(q));
        if (!string.IsNullOrWhiteSpace(type)) query = query.Where(g => g.Type == type);
        var games = await query.OrderBy(g => g.Name).ToListAsync();
        var usage = await db.TherapySessions.Where(s => s.GameId != null && s.Status == "Completed").GroupBy(s => s.GameId!.Value).Select(g => new { Id = g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Id, x => x.N);
        var all = await db.Games.ToListAsync();
        return Ok(new
        {
            Stats = new { Total = all.Count, Active = all.Count(g => g.IsActive), Disabled = all.Count(g => !g.IsActive) },
            Items = games.Select(g => new { g.Id, g.Name, g.Type, g.Category, g.ImageUrl, g.Url, g.Description, g.IsActive, Plays = usage.GetValueOrDefault(g.Id) })
        });
    }

    [HttpGet("{id:int}")]
    public async Task<IActionResult> Get(int id)
    {
        var g = await db.Games.FindAsync(id);
        if (g == null || (!g.IsActive && User.RoleName() != "Admin")) return NotFound();
        var sessions = await db.TherapySessions.Where(s => s.GameId == id && s.Status != "Scheduled" && s.Status != "Missed").ToListAsync();
        return Ok(new { g.Id, g.Name, g.Type, g.Category, g.ImageUrl, g.Url, g.Description, g.IsActive,
            Usage = new { Plays = sessions.Count(s => s.Status == "Completed"), Unfinished = sessions.Count(s => s.Status == "Incomplete"), AvgScore = sessions.Count == 0 ? 0 : (int)sessions.Average(s => s.Score) } });
    }

    [Authorize(Roles = "Admin"), HttpPost]
    public async Task<IActionResult> Create(GameReq r)
    {
        var g = new Game { Name = r.Name.Trim(), Type = r.Type, Category = r.Category, ImageUrl = r.ImageUrl, Url = r.Url, Description = r.Description, IsActive = r.IsActive };
        db.Games.Add(g);
        await db.SaveChangesAsync();
        await Stats.Audit(db, User.Uid(), $"Added game {g.Name}");
        return Ok(new { g.Id });
    }

    [Authorize(Roles = "Admin"), HttpPut("{id:int}")]
    public async Task<IActionResult> Update(int id, GameReq r)
    {
        var g = await db.Games.FindAsync(id);
        if (g == null) return NotFound();
        g.Name = r.Name.Trim(); g.Type = r.Type; g.Category = r.Category; g.ImageUrl = r.ImageUrl; g.Url = r.Url; g.Description = r.Description; g.IsActive = r.IsActive;
        await db.SaveChangesAsync();
        await Stats.Audit(db, User.Uid(), $"Edited game {g.Name}");
        return Ok(new { g.Id });
    }

    [Authorize(Roles = "Admin"), HttpPut("{id:int}/active")]
    public async Task<IActionResult> SetActive(int id, ActiveReq r)
    {
        var g = await db.Games.FindAsync(id);
        if (g == null) return NotFound();
        g.IsActive = r.IsActive;
        await db.SaveChangesAsync();
        await Stats.Audit(db, User.Uid(), $"{(r.IsActive ? "Enabled" : "Disabled")} game {g.Name}");
        return Ok(new { g.Id, g.IsActive });
    }

    [Authorize(Roles = "Admin"), HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id)
    {
        var g = await db.Games.FindAsync(id);
        if (g == null) return NotFound();
        if (await db.PlanGames.AnyAsync(p => p.GameId == id) || await db.TherapySessions.AnyAsync(s => s.GameId == id))
            return Conflict(new { error = "This game is used in treatment plans or sessions. Disable it instead." });
        db.Games.Remove(g);
        await db.SaveChangesAsync();
        await Stats.Audit(db, User.Uid(), $"Deleted game {g.Name}");
        return NoContent();
    }
}

[ApiController, Route("api/admin"), Authorize(Roles = "Admin")]
public class AdminController(PatchyDb db, IConfiguration cfg) : ControllerBase
{
    [HttpGet("accounts")]
    public async Task<IActionResult> Accounts([FromQuery] string? q, [FromQuery] string? role, [FromQuery] bool? active)
    {
        var query = db.Users.AsQueryable();
        if (!string.IsNullOrWhiteSpace(q)) query = query.Where(u => u.FullName.Contains(q) || u.Email.Contains(q));
        if (!string.IsNullOrWhiteSpace(role)) query = query.Where(u => u.Role == role);
        if (active != null) query = query.Where(u => u.IsActive == active);
        var all = await db.Users.ToListAsync();
        return Ok(new
        {
            Stats = new { Total = all.Count, Active = all.Count(u => u.IsActive), Disabled = all.Count(u => !u.IsActive) },
            Items = await query.OrderBy(u => u.FullName).Select(u => new { u.Id, u.FullName, u.Email, u.Role, u.IsActive, u.CreatedAt }).ToListAsync()
        });
    }

    [HttpGet("accounts/{id:int}")]
    public async Task<IActionResult> Account(int id)
    {
        var u = await db.Users.FindAsync(id);
        if (u == null) return NotFound();
        return Ok(new { u.Id, u.FullName, u.Email, u.Role, u.Phone, u.IsActive, u.CreatedAt, u.DoctorCode, u.Permissions,
            Children = await db.Children.CountAsync(c => c.ParentId == id || c.DoctorId == id) });
    }

    [HttpPut("accounts/{id:int}/active")]
    public async Task<IActionResult> SetActive(int id, ActiveReq r)
    {
        var u = await db.Users.FindAsync(id);
        if (u == null) return NotFound();
        if (u.Id == User.Uid()) return BadRequest(new { error = "You cannot disable your own account." });
        u.IsActive = r.IsActive;
        await db.SaveChangesAsync();
        await Stats.Audit(db, User.Uid(), $"{(r.IsActive ? "Enabled" : "Disabled")} account {u.Email}");
        return Ok(new { u.Id, u.IsActive });
    }

    [HttpDelete("accounts/{id:int}")]
    public async Task<IActionResult> DeleteAccount(int id)
    {
        var u = await db.Users.FindAsync(id);
        if (u == null) return NotFound();
        if (u.Id == User.Uid()) return BadRequest(new { error = "You cannot delete your own account." });
        if (await db.Children.AnyAsync(c => c.ParentId == id || c.DoctorId == id) || await db.Messages.AnyAsync(m => m.SenderId == id) || await db.TreatmentPlans.AnyAsync(p => p.DoctorId == id))
            return Conflict(new { error = "This account has linked patients or history. Disable it instead." });
        db.Notifications.RemoveRange(db.Notifications.Where(n => n.UserId == id));
        db.SupportTickets.RemoveRange(db.SupportTickets.Where(t => t.UserId == id));
        db.Users.Remove(u);
        await db.SaveChangesAsync();
        await Stats.Audit(db, User.Uid(), $"Deleted account {u.Email}");
        return NoContent();
    }

    [HttpPost("invite")]
    public async Task<IActionResult> Invite(InviteReq r)
    {
        var email = r.Email.Trim().ToLowerInvariant();
        if (await db.Users.AnyAsync(u => u.Email == email)) return Conflict(new { error = "This email is already registered." });
        var temp = "Pw-" + Stats.NewCode(8);
        db.Users.Add(new AppUser { FullName = r.FullName.Trim(), Email = email, Role = "Admin", Permissions = r.Permissions, PasswordHash = Passwords.Hash(temp) });
        await db.SaveChangesAsync();
        await Stats.Audit(db, User.Uid(), $"Invited admin {email}");
        // Send the invitation by email here. In development the temporary password is returned instead.
        return Ok(new { invited = true, devTempPassword = cfg.GetValue<bool>("Email:ExposeCodeInResponse") ? temp : null });
    }

    [HttpGet("ai-services")]
    public async Task<IActionResult> AiServices()
    {
        var services = await db.AiServices.ToListAsync();
        var checks = (await db.AiServiceChecks.ToListAsync()).ToLookup(c => c.ServiceId);
        var rows = services.Select(s => { var c = checks[s.Id].ToList();
            return new { s.Id, s.Name, s.Description, s.Status, Requests = c.Count, SuccessRate = c.Count == 0 ? 0 : Math.Round(100.0 * c.Count(x => x.Success) / c.Count, 2), AvgMs = c.Count == 0 ? 0 : (int)c.Average(x => x.ResponseMs) }; }).ToList();
        return Ok(new { Stats = new { Total = rows.Sum(r => r.Requests), NeedAttention = rows.Count(r => r.Status != "Operational"), Healthy = rows.Count(r => r.Status == "Operational"),
            AvgMs = rows.Count == 0 ? 0 : (int)rows.Average(r => r.AvgMs) }, Items = rows });
    }

    [HttpGet("ai-services/{id:int}")]
    public async Task<IActionResult> AiService(int id)
    {
        var s = await db.AiServices.FindAsync(id);
        if (s == null) return NotFound();
        var c = await db.AiServiceChecks.Where(x => x.ServiceId == id).OrderByDescending(x => x.CheckedAt).ToListAsync();
        return Ok(new { s.Id, s.Name, s.Description, s.Status,
            Stats = new { Ok = c.Count(x => x.Success), Failed = c.Count(x => !x.Success), SuccessRate = c.Count == 0 ? 0 : Math.Round(100.0 * c.Count(x => x.Success) / c.Count, 2), AvgMs = c.Count == 0 ? 0 : (int)c.Average(x => x.ResponseMs) },
            LastSuccess = c.FirstOrDefault(x => x.Success)?.CheckedAt, Last = c.FirstOrDefault(),
            Errors = c.Where(x => !x.Success).Take(10).Select(x => new { x.CheckedAt, x.Error }),
            Series = c.Take(24).OrderBy(x => x.CheckedAt).Select(x => new { x.CheckedAt, x.ResponseMs, x.Success }) });
    }

    /// "Check now": probes the service. These services are simulated in this build, so the probe is simulated too.
    [HttpPost("ai-services/{id:int}/check")]
    public async Task<IActionResult> CheckNow(int id)
    {
        var s = await db.AiServices.FindAsync(id);
        if (s == null) return NotFound();
        var rnd = Random.Shared;
        var ok = s.Status != "Down" && rnd.NextDouble() < (s.Status == "Degraded" ? .8 : .98);
        var chk = new AiServiceCheck { ServiceId = id, Success = ok, ResponseMs = rnd.Next(60, s.Status == "Degraded" ? 2400 : 600), Error = ok ? null : "Timeout while contacting the service (simulated)" };
        db.AiServiceChecks.Add(chk);
        await db.SaveChangesAsync();
        return Ok(new { chk.Success, chk.ResponseMs, chk.Error, chk.CheckedAt, Simulated = true });
    }

    [HttpGet("audit")]
    public async Task<IActionResult> Audit()
    {
        var logs = await (from l in db.AuditLogs join u in db.Users on l.ActorId equals (int?)u.Id into g from u in g.DefaultIfEmpty()
                          orderby l.CreatedAt descending select new { l.Id, l.Action, l.CreatedAt, Actor = u == null ? "System" : u.FullName }).Take(100).ToListAsync();
        return Ok(logs);
    }
}
