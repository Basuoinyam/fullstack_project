using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Patchy.Api.Data;
using Patchy.Api.Services;

var builder = WebApplication.CreateBuilder(args);
var cfg = builder.Configuration;

builder.Services.AddDbContext<PatchyDb>(o => o.UseSqlServer(cfg.GetConnectionString("Default")));
builder.Services.AddSingleton<TokenService>();
builder.Services.AddHostedService<MissedSessionsJob>();
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(o =>
{
    o.TokenValidationParameters = new TokenValidationParameters
    {
        ValidateIssuer = true, ValidIssuer = cfg["Jwt:Issuer"],
        ValidateAudience = true, ValidAudience = cfg["Jwt:Issuer"],
        ValidateIssuerSigningKey = true,
        IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(cfg["Jwt:Key"]!)),
        ClockSkew = TimeSpan.FromMinutes(1)
    };
});
builder.Services.AddAuthorization();
builder.Services.AddCors(o => o.AddDefaultPolicy(p =>
    p.WithOrigins(cfg.GetSection("Cors:Origins").Get<string[]>() ?? [])
     .AllowAnyHeader().AllowAnyMethod()));

var app = builder.Build();

// Create the schema and demo data (retries while SQL Server is still starting in Docker).
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<PatchyDb>();
    for (var attempt = 1; ; attempt++)
    {
        try { await db.Database.EnsureCreatedAsync(); await DbSeeder.SeedAsync(db); break; }
        catch (Exception ex) when (attempt < 15)
        {
            app.Logger.LogWarning("Database not ready (attempt {Attempt}): {Msg}", attempt, ex.Message);
            await Task.Delay(4000);
        }
    }
}

app.UseSwagger();
app.UseSwaggerUI();
app.UseCors();
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();
app.MapGet("/api/health", () => Results.Ok(new { status = "ok", time = DateTime.UtcNow }));
app.Run();
