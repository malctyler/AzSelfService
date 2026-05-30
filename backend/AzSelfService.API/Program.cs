using System.Text;
using AzSelfService.API.Data;
using AzSelfService.API.Security;
using AzSelfService.API.Services;
using Azure.Identity;
using Azure.Security.KeyVault.Secrets;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<JwtOptions>(builder.Configuration.GetSection("JWT"));

builder.Services.AddDbContext<AzSelfServiceDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("DefaultConnection")));

builder.Services.AddScoped<JwtTokenService>();
builder.Services.AddScoped<CustomerCredentialPreflightService>();
builder.Services.AddScoped<ICustomerCredentialProvisioningService, CustomerCredentialProvisioningService>();
builder.Services.AddScoped<KeyVaultReadinessService>();
builder.Services.AddScoped<StorageAccountNameAvailabilityService>();
builder.Services.AddScoped<KeyVaultNameAvailabilityService>();
builder.Services.AddSingleton<ModuleManifestLoader>();
builder.Services.AddHttpClient();

var keyVaultUrl = builder.Configuration["Azure:KeyVault:Url"];
if (!string.IsNullOrWhiteSpace(keyVaultUrl))
{
    builder.Services.AddSingleton(_ => new SecretClient(new Uri(keyVaultUrl), new DefaultAzureCredential()));
}

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        var jwtSection = builder.Configuration.GetSection("JWT");
        var secret = jwtSection["Secret"] ?? string.Empty;
        var issuer = jwtSection["Issuer"];
        var audience = jwtSection["Audience"];

        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateIssuerSigningKey = true,
            ValidateLifetime = true,
            ValidIssuer = issuer,
            ValidAudience = audience,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secret)),
            ClockSkew = TimeSpan.FromSeconds(30)
        };
    });

builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("AdminOnly", policy => policy.RequireRole(AppRoles.Admin));
});
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "AzSelfService API",
        Version = "v1"
    });

    options.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        Name = "Authorization",
        Type = SecuritySchemeType.Http,
        Scheme = "bearer",
        BearerFormat = "JWT",
        In = ParameterLocation.Header,
        Description = "Provide a JWT token as: Bearer {token}"
    });

    options.AddSecurityRequirement(new OpenApiSecurityRequirement
    {
        {
            new OpenApiSecurityScheme
            {
                Reference = new OpenApiReference
                {
                    Type = ReferenceType.SecurityScheme,
                    Id = "Bearer"
                }
            },
            Array.Empty<string>()
        }
    });
});

builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowFrontend", policy =>
    {
        policy.AllowAnyOrigin()
            .AllowAnyMethod()
            .AllowAnyHeader();
    });
});

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseCors("AllowFrontend");
app.UseHttpsRedirection();
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();

app.MapGet("/health", () => Results.Ok(new { status = "OK", utc = DateTime.UtcNow }))
    .WithName("Health")
    .WithOpenApi();

app.MapGet("/health/ready", async (KeyVaultReadinessService readinessService, CancellationToken cancellationToken) =>
    {
        var result = await readinessService.CheckAsync(cancellationToken);
        return result.IsReady
            ? Results.Ok(new { status = "READY", checks = result.Checks, utc = DateTime.UtcNow })
            : Results.Json(
                new { status = "NOT_READY", checks = result.Checks, utc = DateTime.UtcNow },
                statusCode: StatusCodes.Status503ServiceUnavailable);
    })
    .WithName("Readiness")
    .WithOpenApi();

app.Run();
