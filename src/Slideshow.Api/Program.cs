using System.Security.Claims;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.FileProviders;
using SixLabors.ImageSharp.Memory;
using Slideshow.Api.Images;
using Slideshow.Api.Models;
using Slideshow.Api.Storage;

const long MaxUploadBytes = 10L * 1024 * 1024;
const int MaxCaptionLength = 300;

var passwordHasher = new PasswordHasher<object>();
var hashSubject = new object();

// Hjälpkommando för att skapa lösenordshashen: Slideshow.Api.exe hash
// Läser från konsolen i stället för argument, så lösenordet inte hamnar i kommandohistoriken.
if (args.Length > 0 && string.Equals(args[0], "hash", StringComparison.OrdinalIgnoreCase))
{
    Console.Write("Lösenord: ");
    var entered = Console.ReadLine();

    if (string.IsNullOrWhiteSpace(entered))
    {
        Console.Error.WriteLine("Tomt lösenord, avbryter.");
        return 1;
    }

    Console.WriteLine();
    Console.WriteLine(passwordHasher.HashPassword(hashSubject, entered));
    return 0;
}

var builder = WebApplication.CreateBuilder(args);

var appDataPath = Path.Combine(builder.Environment.ContentRootPath, "App_Data");
var keysPath = Path.Combine(appDataPath, "keys");
Directory.CreateDirectory(keysPath);
// ANCM skapar inte katalogen själv, och stdout-loggen behövs just när appen inte startar.
Directory.CreateDirectory(Path.Combine(appDataPath, "logs"));

// App_Data publiceras aldrig, så hemligheter här överlever en deploy. Det gör inte web.config.
builder.Configuration.AddJsonFile(Path.Combine(appDataPath, "secrets.json"), optional: true, reloadOnChange: true);

// Utan persistens regenereras nycklarna vid varje app pool-recycle och alla cookies blir ogiltiga.
builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(keysPath))
    .SetApplicationName("Slideshow");

// Fas 0 visade tom X-Forwarded-Proto men Request.Scheme == https: Simply terminerar TLS
// direkt i IIS. Utan proxy framför skulle ForwardedHeaders bara göra klient-IP spoofbar.

// IIS-gränsen i web.config är medvetet något högre, så att appen hinner svara 400
// istället för att IIS avvisar med 404.13.
builder.Services.Configure<FormOptions>(o => o.MultipartBodyLengthLimit = MaxUploadBytes);
builder.Services.Configure<IISServerOptions>(o => o.MaxRequestBodySize = MaxUploadBytes);
builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = MaxUploadBytes);

// Taket hindrar en enskild bild från att slå ut hela 32-bitarsprocessen.
SixLabors.ImageSharp.Configuration.Default.MemoryAllocator =
    MemoryAllocator.Create(new MemoryAllocatorOptions { AllocationLimitMegabytes = 128 });

builder.Services.AddSingleton(new AlbumStore(appDataPath));
builder.Services.AddAntiforgery(o => o.HeaderName = "X-CSRF-TOKEN");

builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.Cookie.Name = "slideshow_admin";
        options.Cookie.HttpOnly = true;
        // Lax, inte Strict: med Strict ser man utloggad ut när man klickar in via en delad länk.
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.Cookie.SecurePolicy = builder.Environment.IsDevelopment()
            ? CookieSecurePolicy.SameAsRequest
            : CookieSecurePolicy.Always;
        options.ExpireTimeSpan = TimeSpan.FromDays(30);
        options.SlidingExpiration = true;

        // Ett API ska svara med statuskod, inte omdirigera till en inloggningssida.
        options.Events.OnRedirectToLogin = ctx =>
        {
            ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return Task.CompletedTask;
        };
        options.Events.OnRedirectToAccessDenied = ctx =>
        {
            ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
            return Task.CompletedTask;
        };
    });

builder.Services.AddAuthorization();

builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("login", ctx => RateLimitPartition.GetFixedWindowLimiter(
        ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 5,
            Window = TimeSpan.FromMinutes(5)
        }));
});

var app = builder.Build();
var store = app.Services.GetRequiredService<AlbumStore>();

app.Use(async (ctx, next) =>
{
    ctx.Response.Headers["X-Robots-Tag"] = "noindex, nofollow";
    ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
    ctx.Response.Headers["Referrer-Policy"] = "same-origin";
    await next();
});

app.UseDefaultFiles();
app.UseStaticFiles();

// Bilderna ligger utanför wwwroot. Filnamnen är GUID:er, därför immutable cache.
app.UseStaticFiles(new StaticFileOptions
{
    FileProvider = new PhysicalFileProvider(store.MediaRoot),
    RequestPath = "/media",
    OnPrepareResponse = ctx =>
        ctx.Context.Response.Headers.CacheControl = "public, max-age=31536000, immutable"
});

app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();

var api = app.MapGroup("/api");

api.MapGet("/session", (HttpContext ctx, IAntiforgery antiforgery) =>
{
    var tokens = antiforgery.GetAndStoreTokens(ctx);
    return Results.Ok(new
    {
        isAdmin = IsAdmin(ctx),
        token = tokens.RequestToken
    });
});

api.MapPost("/login", async (LoginRequest body, HttpContext ctx, IConfiguration config) =>
{
    var storedHash = config["Slideshow:AdminPasswordHash"];

    // Hellre neka inloggning än att ta ner hela den publika siten på en saknad hash.
    if (string.IsNullOrWhiteSpace(storedHash))
    {
        return Results.Json(
            new { error = "Ingen adminhash är konfigurerad på servern." },
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }

    var verified = passwordHasher.VerifyHashedPassword(hashSubject, storedHash, body.Password ?? string.Empty);

    if (verified == PasswordVerificationResult.Failed)
    {
        return Results.Json(new { error = "Fel lösenord." }, statusCode: StatusCodes.Status401Unauthorized);
    }

    var identity = new ClaimsIdentity(
        [new Claim(ClaimTypes.Name, "admin")],
        CookieAuthenticationDefaults.AuthenticationScheme);

    await ctx.SignInAsync(
        CookieAuthenticationDefaults.AuthenticationScheme,
        new ClaimsPrincipal(identity),
        new AuthenticationProperties { IsPersistent = true });

    return Results.Ok(new { isAdmin = true });
}).RequireRateLimiting("login");

api.MapPost("/logout", async (HttpContext ctx) =>
{
    await ctx.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    return Results.Ok();
});

api.MapGet("/slideshows", async (HttpContext ctx, AlbumStore albums, CancellationToken ct) =>
{
    var list = await albums.ListAsync(IsAdmin(ctx), ct);

    return Results.Ok(list.Select(a => new
    {
        a.Slug,
        a.Title,
        a.CreatedUtc,
        isDraft = a.PublishedUtc is null,
        slideCount = a.Slides.Count,
        cover = a.Slides.Count > 0 ? $"/media/{a.Slug}/thumb/{a.Slides[0].StoredName}" : null
    }));
});

api.MapGet("/slideshows/{slug}", async (string slug, HttpContext ctx, AlbumStore albums, CancellationToken ct) =>
{
    var album = await albums.GetAsync(slug, ct);
    if (album is null) return Results.NotFound();

    if (album.PublishedUtc is null && !IsAdmin(ctx)) return Results.NotFound();

    return Results.Ok(new
    {
        album.Slug,
        album.Title,
        album.CreatedUtc,
        isDraft = album.PublishedUtc is null,
        slides = album.Slides.Select(s => new
        {
            s.Id,
            s.Caption,
            s.Width,
            s.Height,
            full = $"/media/{album.Slug}/full/{s.StoredName}",
            thumb = $"/media/{album.Slug}/thumb/{s.StoredName}"
        })
    });
});

api.MapPost("/slideshows", async (CreateRequest body, AlbumStore albums, CancellationToken ct) =>
{
    var album = await albums.CreateAsync(body.Title, ct);
    return Results.Ok(new { album.Slug, album.Title });
}).RequireAuthorization();

api.MapPost("/slideshows/{slug}/images", async (
    string slug, IFormFile file, AlbumStore albums, CancellationToken ct) =>
{
    var album = await albums.GetAsync(slug, ct);
    if (album is null) return Results.NotFound();

    if (album.Slides.Count >= AlbumStore.MaxSlidesPerAlbum)
    {
        return Results.BadRequest(new
        {
            error = $"Ett bildspel rymmer högst {AlbumStore.MaxSlidesPerAlbum} bilder."
        });
    }

    if (file.Length == 0) return Results.BadRequest(new { error = "Filen är tom." });

    var storedName = $"{Guid.NewGuid():N}.jpg";
    var fullPath = Path.Combine(albums.FullDirectory(slug), storedName);
    var thumbPath = Path.Combine(albums.ThumbDirectory(slug), storedName);

    using var buffer = new MemoryStream();
    await file.CopyToAsync(buffer, ct);

    try
    {
        var (width, height) = await ImageProcessor.SaveAsync(buffer, fullPath, thumbPath, ct);

        var slide = new Slide
        {
            Id = Guid.NewGuid().ToString("N"),
            StoredName = storedName,
            // Bara metadata och sorteringsnyckel. Används aldrig som sökväg.
            OriginalName = Path.GetFileName(file.FileName),
            Width = width,
            Height = height
        };

        await albums.AddSlideAsync(album, slide, ct);

        return Results.Ok(new { slide.Id, slideCount = album.Slides.Count });
    }
    catch (ImageRejectedException ex)
    {
        Discard(fullPath, thumbPath);
        return Results.BadRequest(new { error = ex.Message });
    }
}).RequireAuthorization();

api.MapPost("/slideshows/{slug}/publish", async (string slug, AlbumStore albums, CancellationToken ct) =>
{
    var album = await albums.GetAsync(slug, ct);
    if (album is null) return Results.NotFound();

    album.PublishedUtc = DateTimeOffset.UtcNow;
    await albums.SaveAsync(album, ct);

    return Results.Ok(new { album.Slug, slideCount = album.Slides.Count });
}).RequireAuthorization();

api.MapPut("/slideshows/{slug}/slides/{id}/caption", async (
    string slug, string id, CaptionRequest body, AlbumStore albums, CancellationToken ct) =>
{
    var album = await albums.GetAsync(slug, ct);
    if (album is null) return Results.NotFound();

    var slide = album.Slides.FirstOrDefault(s => s.Id == id);
    if (slide is null) return Results.NotFound();

    var caption = (body.Caption ?? string.Empty).Trim();
    slide.Caption = caption.Length > MaxCaptionLength ? caption[..MaxCaptionLength] : caption;

    await albums.SaveAsync(album, ct);

    return Results.Ok(new { slide.Caption });
}).RequireAuthorization();

api.MapDelete("/slideshows/{slug}", (string slug, AlbumStore albums) =>
{
    albums.Delete(slug);
    return Results.Ok();
}).RequireAuthorization();

app.MapGet("/s/{slug}", () =>
    Results.File(Path.Combine(app.Environment.WebRootPath, "slideshow.html"), "text/html"));

app.Run();
return 0;

static bool IsAdmin(HttpContext ctx) => ctx.User.Identity?.IsAuthenticated == true;

static void Discard(params string[] paths)
{
    foreach (var path in paths)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (IOException)
        {
        }
    }
}

internal record LoginRequest(string? Password);
internal record CreateRequest(string? Title);
internal record CaptionRequest(string? Caption);
