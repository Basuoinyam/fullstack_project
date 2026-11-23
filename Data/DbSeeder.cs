using Microsoft.EntityFrameworkCore;
using Patchy.Api.Models;
using Patchy.Api.Services;

namespace Patchy.Api.Data;

/// Demo data. Every account uses the password:  Patchy@123
public static class DbSeeder
{
    public static async Task SeedAsync(PatchyDb db)
    {
        if (await db.Users.AnyAsync()) return;
        var pw = Passwords.Hash("Patchy@123");
        var rnd = new Random(7);

        var admin = new AppUser { FullName = "Maya Kareem", Email = "admin@patchy.health", PasswordHash = pw, Role = "Admin", Permissions = "accounts,games,ai,support" };
        var doctor = new AppUser { FullName = "Dr. Omar Nasser", Email = "doctor@patchy.health", PasswordHash = pw, Role = "Doctor", DoctorCode = "DR2048", Phone = "+20 100 000 0001" };
        var p1 = new AppUser { FullName = "Sarah Haddad", Email = "parent@patchy.health", PasswordHash = pw, Role = "Parent", Phone = "+20 100 000 0002" };
        var p2 = new AppUser { FullName = "Noor Tarek", Email = "noor@patchy.health", PasswordHash = pw, Role = "Parent" };
        var p3 = new AppUser { FullName = "Hala Adel", Email = "hala@patchy.health", PasswordHash = pw, Role = "Parent" };
        db.Users.AddRange(admin, doctor, p1, p2, p3);
        await db.SaveChangesAsync();

        var games = new[]
        {
            new Game { Name = "Focus Forest", Type = "Therapy", Category = "Visual tracking", Description = "Follow the glowing firefly through the forest." },
            new Game { Name = "Orbit Match", Type = "Therapy", Category = "Binocular focus", Description = "Match planets while keeping both eyes engaged." },
            new Game { Name = "Bubble Harbor", Type = "Therapy", Category = "Hand-eye coordination", Description = "Pop the bubbles in the right order." },
            new Game { Name = "Shape Safari", Type = "Therapy", Category = "Recognition", Description = "Spot the odd shape or colour." },
            new Game { Name = "Gabor Garden", Type = "Therapy", Category = "Perceptual learning", Description = "Find the faint striped pattern. Difficulty adapts to the child." },
            new Game { Name = "Star Check", Type = "Test", Category = "Vision test", Description = "A short playful acuity check." },
        };
        db.Games.AddRange(games);
        await db.SaveChangesAsync();

        var kids = new[]
        {
            (name: "Lina Haddad", age: 7, eye: "Left", parent: p1, plan: "TP-2048", rate: 0.91, acuity: new[] { "20/100", "20/80", "20/60", "20/50" }),
            (name: "Youssef Salem", age: 9, eye: "Right", parent: p2, plan: "TP-2039", rate: 0.64, acuity: new[] { "20/80", "20/70", "20/60", "20/60" }),
            (name: "Mariam Adel", age: 6, eye: "Left", parent: p3, plan: "TP-2027", rate: 0.84, acuity: new[] { "20/70", "20/60", "20/50", "20/40" }),
        };

        var planNo = 2048;
        foreach (var k in kids)
        {
            var child = new Child
            {
                FullName = k.name, BirthDate = DateTime.UtcNow.Date.AddYears(-k.age).AddMonths(-3), ParentId = k.parent.Id, DoctorId = doctor.Id,
                LazyEye = k.eye, Degree = "Moderate", Cause = "Strabismus", TreatmentStart = DateTime.UtcNow.Date.AddDays(-42),
                LoginCode = k.name.StartsWith("Lina") ? "123456" : Stats.NewCode(), Points = 1840
            };
            db.Children.Add(child);
            await db.SaveChangesAsync();

            for (var i = 0; i < k.acuity.Length; i++)
                db.VisionMeasurements.Add(new VisionMeasurement { ChildId = child.Id, Date = DateTime.UtcNow.Date.AddDays(-42 + i * 14),
                    LeftEye = k.eye == "Left" ? k.acuity[i] : "20/20", RightEye = k.eye == "Right" ? k.acuity[i] : "20/20" });

            var plan = new TreatmentPlan { PlanNumber = k.plan, ChildId = child.Id, DoctorId = doctor.Id, StartDate = DateTime.UtcNow.Date.AddDays(-42),
                EndDate = DateTime.UtcNow.Date.AddDays(45), DailyMinutes = 25, UsesPatch = true, UsesGlasses = true, PatchHours = 2, Status = "Active" };
            db.TreatmentPlans.Add(plan);
            await db.SaveChangesAsync();
            db.PlanGames.AddRange(new PlanGame { PlanId = plan.Id, GameId = games[0].Id, Minutes = 10 },
                                  new PlanGame { PlanId = plan.Id, GameId = games[4].Id, Minutes = 10 },
                                  new PlanGame { PlanId = plan.Id, GameId = games[3].Id, Minutes = 5 });

            for (var d = -42; d <= 45; d++)
            {
                var day = DateTime.UtcNow.Date.AddDays(d);
                var s = new TherapySession { ChildId = child.Id, PlanId = plan.Id, GameId = games[d % 3 == 0 ? 4 : 0].Id, Date = day };
                if (d < 0)
                {
                    var done = rnd.NextDouble() < k.rate;
                    s.Status = done ? "Completed" : (rnd.NextDouble() < .4 ? "Incomplete" : "Missed");
                    if (s.Status != "Missed")
                    {
                        s.StartedAt = day.AddHours(17); s.DurationSeconds = done ? 1500 + rnd.Next(-120, 120) : 900;
                        s.EndedAt = s.StartedAt.Value.AddSeconds(s.DurationSeconds); s.Score = done ? rnd.Next(70, 100) : rnd.Next(40, 60);
                        s.AttentionAvg = rnd.Next(70, 97); s.PatchConfirmed = true; s.FaceLostCount = rnd.Next(0, 4);
                    }
                }
                db.TherapySessions.Add(s);
            }
            await db.SaveChangesAsync();

            var all = await db.TherapySessions.Where(s => s.ChildId == child.Id).ToListAsync();
            child.FollowUp = Stats.FollowUpFor(Stats.Adherence(all));
            db.RewardEvents.Add(new RewardEvent { ChildId = child.Id, Points = 1840, Reason = "Starter points" });
            db.Messages.AddRange(
                new Message { ChildId = child.Id, SenderId = doctor.Id, Text = "Please keep the patch on during the full game.", SentAt = DateTime.UtcNow.AddHours(-5), IsRead = true },
                new Message { ChildId = child.Id, SenderId = k.parent.Id, Text = "Thank you, doctor. I will keep the schedule consistent.", SentAt = DateTime.UtcNow.AddHours(-2) });
            db.Notifications.Add(new Notification { UserId = k.parent.Id, Kind = "report", Title = "Progress report ready", Body = $"{k.name}'s six-week report is ready." });
            planNo++;
        }

        db.Notifications.Add(new Notification { UserId = doctor.Id, Kind = "report", Title = "Progress report ready", Body = "Weekly reports are ready for review." });

        var services = new[]
        {
            ("AI Adherence & Reports", "Builds adherence summaries and weekly reports.", "Operational"),
            ("Computer Vision", "Face, patch, distance and attention checks (runs in the browser).", "Operational"),
            ("AI Companion (Pip)", "Friendly companion for children.", "Operational"),
            ("Prescription OCR", "Reads prescription photos.", "Degraded"),
            ("Doctor & Parent Assistant", "Answers questions using the child's data.", "Operational"),
        };
        foreach (var (name, desc, status) in services)
        {
            var svc = new AiService { Name = name, Description = desc, Status = status };
            db.AiServices.Add(svc);
            await db.SaveChangesAsync();
            for (var i = 0; i < 40; i++)
                db.AiServiceChecks.Add(new AiServiceCheck { ServiceId = svc.Id, CheckedAt = DateTime.UtcNow.AddHours(-i * 3),
                    Success = rnd.NextDouble() < (status == "Degraded" ? .93 : .995), ResponseMs = rnd.Next(60, status == "Degraded" ? 2200 : 500) });
        }

        db.SupportTickets.AddRange(
            new SupportTicket { Number = "SR-1001", UserId = p2.Id, Title = "Prescription scan not reading", Category = "OCR", Type = "Bug", Priority = "High", Description = "The scan fails on a clear photo.", Status = "New" },
            new SupportTicket { Number = "SR-1002", UserId = doctor.Id, Title = "Add a new exercise", Category = "Games", Type = "Request", Priority = "Low", Description = "Could we add a dot-to-dot game?", Status = "UnderReview" });
        db.AuditLogs.Add(new AuditLog { ActorId = admin.Id, Action = "Seeded demo data" });
        await db.SaveChangesAsync();
    }
}
