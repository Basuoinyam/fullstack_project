using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Patchy.Api.Data;
using Patchy.Api.Models;
using Patchy.Api.Services;

namespace Patchy.Api.Controllers;

public record SendMessageReq(int ChildId, string Text);
public record TicketReq(string Title, string Category, string Type, string Priority, string Description, string? Impact);
public record TicketStatusReq(string Status);

[ApiController, Route("api/messages"), Authorize(Roles = "Doctor,Parent")]
public class MessagesController(PatchyDb db) : ControllerBase
{
    /// One conversation per child: doctor <-> parent.
    [HttpGet("conversations")]
    public async Task<IActionResult> Conversations([FromQuery] string? q, [FromQuery] string? filter)
    {
        var kids = await Access.Visible(db, User).ToListAsync();
        var ids = kids.Select(k => k.Id).ToList();
        var msgs = (await db.Messages.Where(m => ids.Contains(m.ChildId)).ToListAsync()).ToLookup(m => m.ChildId);
        var parentIds = kids.Select(k => k.ParentId).Distinct().ToList();
        var parents = await db.Users.Where(u => parentIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.FullName);
        var me = User.Uid();
        var rows = kids.Select(k =>
        {
            var last = msgs[k.Id].OrderByDescending(m => m.SentAt).FirstOrDefault();
            return new { ChildId = k.Id, ChildName = k.FullName, ParentName = parents.GetValueOrDefault(k.ParentId), LastMessage = last?.Text, LastAt = last?.SentAt,
                Unread = msgs[k.Id].Count(m => !m.IsRead && m.SenderId != me) };
        });
        if (!string.IsNullOrWhiteSpace(q)) rows = rows.Where(r => r.ChildName.Contains(q, StringComparison.OrdinalIgnoreCase) || (r.ParentName ?? "").Contains(q, StringComparison.OrdinalIgnoreCase));
        if (filter == "unread") rows = rows.Where(r => r.Unread > 0);
        if (filter == "read") rows = rows.Where(r => r.Unread == 0);
        return Ok(rows.OrderByDescending(r => r.LastAt));
    }

    [HttpGet("{childId:int}")]
    public async Task<IActionResult> Thread(int childId)
    {
        if (await Access.GetChild(db, User, childId) == null) return NotFound();
        var me = User.Uid();
        var items = await db.Messages.Where(m => m.ChildId == childId).OrderBy(m => m.SentAt).ToListAsync();
        var senderIds = items.Select(i => i.SenderId).Distinct().ToList();
        var senders = await db.Users.Where(u => senderIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.FullName);
        return Ok(items.Select(m => new { m.Id, m.Text, m.SentAt, m.IsRead, Mine = m.SenderId == me, SenderName = senders.GetValueOrDefault(m.SenderId) }));
    }

    [HttpPost]
    public async Task<IActionResult> Send(SendMessageReq r)
    {
        var child = await Access.GetChild(db, User, r.ChildId);
        if (child == null) return NotFound();
        if (string.IsNullOrWhiteSpace(r.Text) || r.Text.Length > 2000) return BadRequest(new { error = "Message is empty or too long." });
        var m = new Message { ChildId = child.Id, SenderId = User.Uid(), Text = r.Text.Trim() };
        db.Messages.Add(m);
        await db.SaveChangesAsync();
        var to = User.RoleName() == "Doctor" ? child.ParentId : child.DoctorId;
        if (to != null) await Stats.Notify(db, to.Value, "New message", $"New message about {child.FullName}.", "message");
        return Ok(new { m.Id, m.SentAt });
    }

    [HttpPost("{childId:int}/read")]
    public async Task<IActionResult> MarkRead(int childId)
    {
        if (await Access.GetChild(db, User, childId) == null) return NotFound();
        var me = User.Uid();
        await db.Messages.Where(m => m.ChildId == childId && m.SenderId != me && !m.IsRead).ExecuteUpdateAsync(s => s.SetProperty(m => m.IsRead, true));
        return NoContent();
    }
}

[ApiController, Route("api/notifications"), Authorize(Roles = "Admin,Doctor,Parent")]
public class NotificationsController(PatchyDb db) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List() =>
        Ok(await db.Notifications.Where(n => n.UserId == User.Uid()).OrderByDescending(n => n.CreatedAt).Take(100).ToListAsync());

    [HttpPost("{id:int}/read")]
    public async Task<IActionResult> Read(int id)
    {
        await db.Notifications.Where(n => n.Id == id && n.UserId == User.Uid()).ExecuteUpdateAsync(s => s.SetProperty(n => n.IsRead, true));
        return NoContent();
    }

    [HttpPost("read-all")]
    public async Task<IActionResult> ReadAll()
    {
        await db.Notifications.Where(n => n.UserId == User.Uid() && !n.IsRead).ExecuteUpdateAsync(s => s.SetProperty(n => n.IsRead, true));
        return NoContent();
    }
}

[ApiController, Route("api/support"), Authorize(Roles = "Admin,Doctor,Parent")]
public class SupportController(PatchyDb db) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List([FromQuery] string? q, [FromQuery] string? status, [FromQuery] string? type, [FromQuery] string? priority)
    {
        var query = db.SupportTickets.AsQueryable();
        if (User.RoleName() != "Admin") query = query.Where(t => t.UserId == User.Uid());
        if (!string.IsNullOrWhiteSpace(q)) query = query.Where(t => t.Title.Contains(q) || t.Number.Contains(q));
        if (!string.IsNullOrWhiteSpace(status)) query = query.Where(t => t.Status == status);
        if (!string.IsNullOrWhiteSpace(type)) query = query.Where(t => t.Type == type);
        if (!string.IsNullOrWhiteSpace(priority)) query = query.Where(t => t.Priority == priority);
        var items = await (from t in query join u in db.Users on t.UserId equals u.Id orderby t.CreatedAt descending
                           select new { t.Id, t.Number, t.Title, t.Category, t.Type, t.Priority, t.Status, t.CreatedAt, From = u.FullName, FromRole = u.Role }).ToListAsync();
        var all = await db.SupportTickets.ToListAsync();
        return Ok(new { Stats = new { All = all.Count, UnderReview = all.Count(t => t.Status == "UnderReview"), New = all.Count(t => t.Status == "New"), Urgent = all.Count(t => t.Priority == "High" && t.Status != "Resolved") }, Items = items });
    }

    [HttpGet("{id:int}")]
    public async Task<IActionResult> Get(int id)
    {
        var t = await db.SupportTickets.FindAsync(id);
        if (t == null || (User.RoleName() != "Admin" && t.UserId != User.Uid())) return NotFound();
        var u = await db.Users.FindAsync(t.UserId);
        return Ok(new { t.Id, t.Number, t.Title, t.Category, t.Type, t.Priority, t.Description, t.Impact, t.Status, t.CreatedAt, From = u?.FullName, FromRole = u?.Role });
    }

    [HttpPost]
    public async Task<IActionResult> Create(TicketReq r)
    {
        var next = 1000 + await db.SupportTickets.CountAsync() + 1;
        while (await db.SupportTickets.AnyAsync(t => t.Number == $"SR-{next}")) next++;
        var t = new SupportTicket { Number = $"SR-{next}", UserId = User.Uid(), Title = r.Title.Trim(), Category = r.Category, Type = r.Type, Priority = r.Priority, Description = r.Description, Impact = r.Impact };
        db.SupportTickets.Add(t);
        await db.SaveChangesAsync();
        foreach (var admin in await db.Users.Where(u => u.Role == "Admin" && u.IsActive).ToListAsync())
            db.Notifications.Add(new Notification { UserId = admin.Id, Kind = "support", Title = "New support request", Body = $"{t.Number}: {t.Title}" });
        await db.SaveChangesAsync();
        return Ok(new { t.Id, t.Number });
    }

    [Authorize(Roles = "Admin"), HttpPut("{id:int}/status")]
    public async Task<IActionResult> SetStatus(int id, TicketStatusReq r)
    {
        var t = await db.SupportTickets.FindAsync(id);
        if (t == null) return NotFound();
        if (r.Status is not ("New" or "UnderReview" or "Resolved")) return BadRequest(new { error = "Unknown status." });
        t.Status = r.Status;
        await db.SaveChangesAsync();
        await Stats.Notify(db, t.UserId, "Support request updated", $"{t.Number} is now {r.Status}.", "support");
        await Stats.Audit(db, User.Uid(), $"Ticket {t.Number} -> {r.Status}");
        return Ok(new { t.Id, t.Status });
    }
}
