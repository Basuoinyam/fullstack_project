namespace Patchy.Api.Models;

public class AppUser
{
    public int Id { get; set; }
    public string FullName { get; set; } = "";
    public string Email { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    public string Role { get; set; } = "Parent";          // Admin | Doctor | Parent
    public string? Phone { get; set; }
    public string? DoctorCode { get; set; }               // doctors only; parents use it to link a child
    public string? Permissions { get; set; }              // admins only (comma separated)
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class Child
{
    public int Id { get; set; }
    public string FullName { get; set; } = "";
    public DateTime BirthDate { get; set; }
    public int ParentId { get; set; }
    public int? DoctorId { get; set; }
    public string LazyEye { get; set; } = "Left";         // Left | Right
    public string? Degree { get; set; }                   // Mild | Moderate | Severe
    public string? Cause { get; set; }                    // Strabismus | Refractive | ...
    public DateTime? TreatmentStart { get; set; }
    public DateTime? TreatmentEnd { get; set; }
    public string Status { get; set; } = "Ongoing";       // Ongoing | Paused | Completed
    public string FollowUp { get; set; } = "OnTrack";     // OnTrack | NeedsReview | AtRisk
    public string LoginCode { get; set; } = "";           // personal code the child types to enter
    public int Points { get; set; }
}

public class VisionMeasurement
{
    public int Id { get; set; }
    public int ChildId { get; set; }
    public DateTime Date { get; set; }
    public string LeftEye { get; set; } = "";             // e.g. 20/50
    public string RightEye { get; set; } = "";
    public string? PrescriptionImageUrl { get; set; }
    public string? Notes { get; set; }
}

public class Game
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string Type { get; set; } = "Therapy";         // Therapy | Test
    public string? Category { get; set; }
    public string? ImageUrl { get; set; }
    public string? Url { get; set; }
    public string? Description { get; set; }
    public bool IsActive { get; set; } = true;
}

public class TreatmentPlan
{
    public int Id { get; set; }
    public string PlanNumber { get; set; } = "";
    public int ChildId { get; set; }
    public int DoctorId { get; set; }
    public DateTime StartDate { get; set; }
    public DateTime EndDate { get; set; }
    public int DailyMinutes { get; set; }
    public bool UsesPatch { get; set; }
    public bool UsesGlasses { get; set; }
    public double PatchHours { get; set; }
    public string Status { get; set; } = "Active";        // Active | Complete | AtRisk
    public string? ResultNote { get; set; }
}

public class PlanGame
{
    public int Id { get; set; }
    public int PlanId { get; set; }
    public int GameId { get; set; }
    public int Minutes { get; set; }
}

public class TherapySession
{
    public int Id { get; set; }
    public int ChildId { get; set; }
    public int? PlanId { get; set; }
    public int? GameId { get; set; }
    public DateTime Date { get; set; }                    // scheduled day
    public DateTime? StartedAt { get; set; }
    public DateTime? EndedAt { get; set; }
    public string Status { get; set; } = "Scheduled";     // Scheduled | Completed | Incomplete | Missed
    public int DurationSeconds { get; set; }
    public int Score { get; set; }
    public double AttentionAvg { get; set; }              // 0-100, scores only (raw video never leaves the device)
    public int FaceLostCount { get; set; }
    public bool PatchConfirmed { get; set; }
    public bool IsExtra { get; set; }
}

public class Message
{
    public int Id { get; set; }
    public int ChildId { get; set; }                      // conversation = doctor <-> parent about one child
    public int SenderId { get; set; }
    public string Text { get; set; } = "";
    public DateTime SentAt { get; set; } = DateTime.UtcNow;
    public bool IsRead { get; set; }
}

public class Notification
{
    public int Id { get; set; }
    public int UserId { get; set; }
    public string Title { get; set; } = "";
    public string Body { get; set; } = "";
    public string Kind { get; set; } = "info";            // info | session | message | report | support
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public bool IsRead { get; set; }
}

public class SupportTicket
{
    public int Id { get; set; }
    public string Number { get; set; } = "";
    public int UserId { get; set; }
    public string Title { get; set; } = "";
    public string Category { get; set; } = "General";
    public string Type { get; set; } = "Question";        // Question | Bug | Request
    public string Priority { get; set; } = "Medium";      // Low | Medium | High
    public string Description { get; set; } = "";
    public string? Impact { get; set; }
    public string Status { get; set; } = "New";           // New | UnderReview | Resolved
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class AiService
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string Status { get; set; } = "Operational";   // Operational | Degraded | Down
}

public class AiServiceCheck
{
    public int Id { get; set; }
    public int ServiceId { get; set; }
    public DateTime CheckedAt { get; set; } = DateTime.UtcNow;
    public bool Success { get; set; }
    public int ResponseMs { get; set; }
    public string? Error { get; set; }
}

public class EmailCode
{
    public int Id { get; set; }
    public string Email { get; set; } = "";
    public string Code { get; set; } = "";
    public string Purpose { get; set; } = "register";     // register | reset
    public DateTime ExpiresAt { get; set; }
    public bool Used { get; set; }
}

public class RewardEvent
{
    public int Id { get; set; }
    public int ChildId { get; set; }
    public int Points { get; set; }                       // negative when redeemed
    public string Reason { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class AuditLog
{
    public int Id { get; set; }
    public int? ActorId { get; set; }
    public string Action { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
