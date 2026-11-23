using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Patchy.Api.Data;
using Patchy.Api.Models;

namespace Patchy.Api.Services;

public static class Passwords
{
    const int Iterations = 120_000;
    public static string Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, 32);
        return $"v1.{Iterations}.{Convert.ToBase64String(salt)}.{Convert.ToBase64String(hash)}";
    }
    public static bool Verify(string password, string stored)
    {
        var p = stored.Split('.');
        if (p.Length != 4 || p[0] != "v1") return false;
        var salt = Convert.FromBase64String(p[2]);
        var expected = Convert.FromBase64String(p[3]);
        var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, int.Parse(p[1]), HashAlgorithmName.SHA256, expected.Length);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }
}

public class TokenService(IConfiguration cfg)
{
    public string Create(AppUser user, int? childId = null, string? roleOverride = null)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new(ClaimTypes.Role, roleOverride ?? user.Role),
            new(ClaimTypes.Name, user.FullName),
        };
        if (childId != null) claims.Add(new Claim("childId", childId.Value.ToString()));
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(cfg["Jwt:Key"]!));
        var token = new JwtSecurityToken(cfg["Jwt:Issuer"], cfg["Jwt:Issuer"], claims,
            expires: DateTime.UtcNow.AddMinutes(int.Parse(cfg["Jwt:ExpiresMinutes"] ?? "480")),
            signingCredentials: new SigningCredentials(key, SecurityAlgorithms.HmacSha256));
        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}

public static class ClaimsExt
{
    public static int Uid(this ClaimsPrincipal u) => int.Parse(u.FindFirstValue(ClaimTypes.NameIdentifier)!);
    public static string RoleName(this ClaimsPrincipal u) => u.FindFirstValue(ClaimTypes.Role) ?? "";
    public static int? ChildClaim(this ClaimsPrincipal u) => int.TryParse(u.FindFirstValue("childId"), out var x) ? x : null;
}

public static class Access
{
    /// Returns the child only if the caller is allowed to see it (admin / linked doctor / own parent / the child itself).
    public static async Task<Child?> GetChild(PatchyDb db, ClaimsPrincipal u, int childId)
    {
        var c = await db.Children.FindAsync(childId);
        if (c == null) return null;
        var ok = u.RoleName() switch
        {
            "Admin" => true,
            "Doctor" => c.DoctorId == u.Uid(),
            "Parent" => c.ParentId == u.Uid(),
            "Child" => u.ChildClaim() == childId,
            _ => false
        };
        return ok ? c : null;
    }

    public static IQueryable<Child> Visible(PatchyDb db, ClaimsPrincipal u) => u.RoleName() switch
    {
        "Admin" => db.Children,
        "Doctor" => db.Children.Where(c => c.DoctorId == u.Uid()),
        "Parent" => db.Children.Where(c => c.ParentId == u.Uid()),
        "Child" => db.Children.Where(c => c.Id == u.ChildClaim()),
        _ => db.Children.Where(c => false)
    };
}

public static class Stats
{
    /// Adherence = completed sessions / sessions that were due (planned, up to today, extras excluded).
    public static int Adherence(IEnumerable<TherapySession> sessions)
    {
        var today = DateTime.UtcNow.Date;
        var due = sessions.Where(s => !s.IsExtra && s.Date.Date <= today && (s.Status != "Scheduled" || s.Date.Date < today)).ToList();
        if (due.Count == 0) return 0;
        return (int)Math.Round(100.0 * due.Count(s => s.Status == "Completed") / due.Count);
    }

    public static string FollowUpFor(int adherence) => adherence >= 80 ? "OnTrack" : adherence >= 60 ? "NeedsReview" : "AtRisk";

    public static async Task Notify(PatchyDb db, int userId, string title, string body, string kind = "info")
    {
        db.Notifications.Add(new Notification { UserId = userId, Title = title, Body = body, Kind = kind });
        await db.SaveChangesAsync();
    }

    public static async Task Audit(PatchyDb db, int? actor, string action)
    {
        db.AuditLogs.Add(new AuditLog { ActorId = actor, Action = action });
        await db.SaveChangesAsync();
    }

    public static string NewCode(int digits = 6)
    {
        var max = (int)Math.Pow(10, digits);
        return RandomNumberGenerator.GetInt32(max / 10, max).ToString();
    }
}
