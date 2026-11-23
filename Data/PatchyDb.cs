using Microsoft.EntityFrameworkCore;
using Patchy.Api.Models;

namespace Patchy.Api.Data;

public class PatchyDb(DbContextOptions<PatchyDb> options) : DbContext(options)
{
    public DbSet<AppUser> Users => Set<AppUser>();
    public DbSet<Child> Children => Set<Child>();
    public DbSet<VisionMeasurement> VisionMeasurements => Set<VisionMeasurement>();
    public DbSet<Game> Games => Set<Game>();
    public DbSet<TreatmentPlan> TreatmentPlans => Set<TreatmentPlan>();
    public DbSet<PlanGame> PlanGames => Set<PlanGame>();
    public DbSet<TherapySession> TherapySessions => Set<TherapySession>();
    public DbSet<Message> Messages => Set<Message>();
    public DbSet<Notification> Notifications => Set<Notification>();
    public DbSet<SupportTicket> SupportTickets => Set<SupportTicket>();
    public DbSet<AiService> AiServices => Set<AiService>();
    public DbSet<AiServiceCheck> AiServiceChecks => Set<AiServiceCheck>();
    public DbSet<EmailCode> EmailCodes => Set<EmailCode>();
    public DbSet<RewardEvent> RewardEvents => Set<RewardEvent>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<AppUser>(e => { e.ToTable("Users"); e.HasIndex(x => x.Email).IsUnique();
            e.HasIndex(x => x.DoctorCode).IsUnique().HasFilter("[DoctorCode] IS NOT NULL"); });
        b.Entity<Child>(e => { e.ToTable("Children"); e.HasIndex(x => x.LoginCode).IsUnique();
            e.HasOne<AppUser>().WithMany().HasForeignKey(x => x.ParentId);
            e.HasOne<AppUser>().WithMany().HasForeignKey(x => x.DoctorId); });
        b.Entity<VisionMeasurement>(e => { e.ToTable("VisionMeasurements"); e.HasOne<Child>().WithMany().HasForeignKey(x => x.ChildId); });
        b.Entity<Game>().ToTable("Games");
        b.Entity<TreatmentPlan>(e => { e.ToTable("TreatmentPlans"); e.HasIndex(x => x.PlanNumber).IsUnique();
            e.HasOne<Child>().WithMany().HasForeignKey(x => x.ChildId);
            e.HasOne<AppUser>().WithMany().HasForeignKey(x => x.DoctorId); });
        b.Entity<PlanGame>(e => { e.ToTable("PlanGames");
            e.HasOne<TreatmentPlan>().WithMany().HasForeignKey(x => x.PlanId);
            e.HasOne<Game>().WithMany().HasForeignKey(x => x.GameId); });
        b.Entity<TherapySession>(e => { e.ToTable("TherapySessions"); e.HasIndex(x => new { x.ChildId, x.Date });
            e.HasOne<Child>().WithMany().HasForeignKey(x => x.ChildId);
            e.HasOne<TreatmentPlan>().WithMany().HasForeignKey(x => x.PlanId);
            e.HasOne<Game>().WithMany().HasForeignKey(x => x.GameId); });
        b.Entity<Message>(e => { e.ToTable("Messages");
            e.HasOne<Child>().WithMany().HasForeignKey(x => x.ChildId);
            e.HasOne<AppUser>().WithMany().HasForeignKey(x => x.SenderId); });
        b.Entity<Notification>(e => { e.ToTable("Notifications"); e.HasOne<AppUser>().WithMany().HasForeignKey(x => x.UserId); });
        b.Entity<SupportTicket>(e => { e.ToTable("SupportTickets"); e.HasIndex(x => x.Number).IsUnique();
            e.HasOne<AppUser>().WithMany().HasForeignKey(x => x.UserId); });
        b.Entity<AiService>().ToTable("AiServices");
        b.Entity<AiServiceCheck>(e => { e.ToTable("AiServiceChecks"); e.HasOne<AiService>().WithMany().HasForeignKey(x => x.ServiceId); });
        b.Entity<EmailCode>(e => { e.ToTable("EmailCodes"); e.HasIndex(x => x.Email); });
        b.Entity<RewardEvent>(e => { e.ToTable("RewardEvents"); e.HasOne<Child>().WithMany().HasForeignKey(x => x.ChildId); });
        b.Entity<AuditLog>().ToTable("AuditLogs");

        // SQL Server rejects multiple cascade paths, so deletes are done explicitly in the controllers.
        foreach (var fk in b.Model.GetEntityTypes().SelectMany(t => t.GetForeignKeys()))
            fk.DeleteBehavior = DeleteBehavior.Restrict;
    }
}
