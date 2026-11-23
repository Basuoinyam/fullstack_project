using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Patchy.Api.Data;
using Patchy.Api.Models;
using Patchy.Api.Services;

namespace Patchy.Api.Controllers;

public record SendCodeReq(string Email, string Purpose);
public record RegisterReq(string FullName, string Email, string Password, string Role, string Code, string? Phone);
public record LoginReq(string Email, string Password);
public record ChildLoginReq(string Code);
public record ResetReq(string Email, string Code, string NewPassword);
public record ChangePasswordReq(string OldPassword, string NewPassword);
public record UpdateProfileReq(string FullName, string? Phone);

[ApiController, Route("api/auth")]
public class AuthController(PatchyDb db, TokenService tokens, IConfiguration cfg, ILogger<AuthController> log) : ControllerBase
{
    static object UserDto(AppUser u) => new { u.Id, u.FullName, u.Email, u.Role, u.Phone, u.DoctorCode };

    [HttpPost("send-code")]
    public async Task<IActionResult> SendCode(SendCodeReq r)
    {
        var email = r.Email.Trim().ToLowerInvariant();
        var exists = await db.Users.AnyAsync(u => u.Email == email);
        if (r.Purpose == "register" && exists) return Conflict(new { error = "This email is already registered." });
        // For password reset we answer the same way whether or not the account exists (no account enumeration).
        if (r.Purpose == "reset" && !exists) return Ok(new { sent = true });

        var code = Stats.NewCode();
        db.EmailCodes.Add(new EmailCode { Email = email, Code = code, Purpose = r.Purpose, ExpiresAt = DateTime.UtcNow.AddMinutes(10) });
        await db.SaveChangesAsync();
        // Plug a real email provider (SMTP / SendGrid) in here. For development the code is written to the log.
        log.LogWarning("[DEV EMAIL] {Purpose} code for {Email}: {Code}", r.Purpose, email, code);
        return Ok(new { sent = true, devCode = cfg.GetValue<bool>("Email:ExposeCodeInResponse") ? code : null });
    }

    async Task<bool> UseCode(string email, string code, string purpose)
    {
        var c = await db.EmailCodes.Where(x => x.Email == email && x.Purpose == purpose && !x.Used && x.Code == code && x.ExpiresAt > DateTime.UtcNow)
            .OrderByDescending(x => x.Id).FirstOrDefaultAsync();
        if (c == null) return false;
        c.Used = true;
        return true;
    }

    [HttpPost("register")]
    public async Task<IActionResult> Register(RegisterReq r)
    {
        var email = r.Email.Trim().ToLowerInvariant();
        if (r.Role is not ("Doctor" or "Parent")) return BadRequest(new { error = "Role must be Doctor or Parent." });
        if (r.Password.Length < 8) return BadRequest(new { error = "Password must be at least 8 characters." });
        if (await db.Users.AnyAsync(u => u.Email == email)) return Conflict(new { error = "This email is already registered." });
        if (!await UseCode(email, r.Code, "register")) return BadRequest(new { error = "Invalid or expired verification code." });

        var user = new AppUser { FullName = r.FullName.Trim(), Email = email, PasswordHash = Passwords.Hash(r.Password), Role = r.Role, Phone = r.Phone };
        if (r.Role == "Doctor")
        {
            do user.DoctorCode = "DR" + Stats.NewCode(4); while (await db.Users.AnyAsync(u => u.DoctorCode == user.DoctorCode));
        }
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return Ok(new { token = tokens.Create(user), user = UserDto(user) });
    }

    [HttpPost("login")]
    public async Task<IActionResult> Login(LoginReq r)
    {
        var email = r.Email.Trim().ToLowerInvariant();
        var user = await db.Users.FirstOrDefaultAsync(u => u.Email == email);
        if (user == null || !Passwords.Verify(r.Password, user.PasswordHash)) return Unauthorized(new { error = "Wrong email or password." });
        if (!user.IsActive) return StatusCode(403, new { error = "This account is disabled." });
        return Ok(new { token = tokens.Create(user), user = UserDto(user) });
    }

    /// The child types the personal code the doctor/parent gave them.
    [HttpPost("child-login")]
    public async Task<IActionResult> ChildLogin(ChildLoginReq r)
    {
        var child = await db.Children.FirstOrDefaultAsync(c => c.LoginCode == r.Code.Trim());
        if (child == null) return Unauthorized(new { error = "Code not found." });
        var parent = await db.Users.FindAsync(child.ParentId);
        if (parent == null || !parent.IsActive) return StatusCode(403, new { error = "This account is disabled." });
        var token = tokens.Create(new AppUser { Id = 0, FullName = child.FullName }, child.Id, "Child");
        return Ok(new { token, user = new { Id = child.Id, FullName = child.FullName, Email = "", Role = "Child", Phone = (string?)null, DoctorCode = (string?)null }, childId = child.Id });
    }

    [HttpPost("reset-password")]
    public async Task<IActionResult> Reset(ResetReq r)
    {
        var email = r.Email.Trim().ToLowerInvariant();
        if (r.NewPassword.Length < 8) return BadRequest(new { error = "Password must be at least 8 characters." });
        var user = await db.Users.FirstOrDefaultAsync(u => u.Email == email);
        if (user == null || !await UseCode(email, r.Code, "reset")) return BadRequest(new { error = "Invalid or expired verification code." });
        user.PasswordHash = Passwords.Hash(r.NewPassword);
        await db.SaveChangesAsync();
        return Ok(new { reset = true });
    }

    [Authorize, HttpGet("me")]
    public async Task<IActionResult> Me()
    {
        if (User.RoleName() == "Child") { var c = await db.Children.FindAsync(User.ChildClaim()); return c == null ? Unauthorized() : Ok(new { Id = c.Id, FullName = c.FullName, Email = "", Role = "Child", Phone = (string?)null, DoctorCode = (string?)null, childId = c.Id }); }
        var u = await db.Users.FindAsync(User.Uid());
        return u == null ? Unauthorized() : Ok(UserDto(u));
    }

    [Authorize(Roles = "Admin,Doctor,Parent"), HttpPut("me")]
    public async Task<IActionResult> UpdateMe(UpdateProfileReq r)
    {
        var u = await db.Users.FindAsync(User.Uid());
        if (u == null) return NotFound();
        u.FullName = r.FullName.Trim(); u.Phone = r.Phone;
        await db.SaveChangesAsync();
        return Ok(UserDto(u));
    }

    [Authorize(Roles = "Admin,Doctor,Parent"), HttpPost("change-password")]
    public async Task<IActionResult> ChangePassword(ChangePasswordReq r)
    {
        var u = await db.Users.FindAsync(User.Uid());
        if (u == null || !Passwords.Verify(r.OldPassword, u.PasswordHash)) return BadRequest(new { error = "Current password is wrong." });
        if (r.NewPassword.Length < 8) return BadRequest(new { error = "Password must be at least 8 characters." });
        u.PasswordHash = Passwords.Hash(r.NewPassword);
        await db.SaveChangesAsync();
        return Ok(new { changed = true });
    }
}
