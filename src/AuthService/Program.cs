using System.Security.Cryptography;
using AuthService.Data;
using AuthService.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.

builder.Services.AddControllers();
// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddOpenApi();

builder.Services.AddDbContext<AuthDbContext>(options =>
    options.UseNpgsql(
        builder.Configuration.GetConnectionString("AuthDb"),
        npgsql => npgsql.MigrationsHistoryTable("__AuthServiceMigrationsHistory")));

builder.Services.AddSingleton<IPasswordHasher<User>, PasswordHasher<User>>();

// Only this service ever sees the private key -- it lives in User Secrets,
// never committed. Booking Service (and anything else that needs to validate
// a token) only ever gets the corresponding public key.
builder.Services.AddSingleton(_ =>
{
    var rsa = RSA.Create();
    rsa.ImportFromPem(builder.Configuration["Auth:JwtPrivateKey"]);
    return new SigningCredentials(new RsaSecurityKey(rsa), SecurityAlgorithms.RsaSha256);
});

builder.Services.AddHealthChecks();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();

    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
    await db.Database.MigrateAsync();
}

app.UseHttpsRedirection();

app.UseAuthorization();

app.MapHealthChecks("/health");
app.MapControllers();

app.Run();
