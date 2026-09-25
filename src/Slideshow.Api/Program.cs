using System.Globalization;
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

// Besökarens nivå: 0 utloggad, 1 Extended, 2 Family, 3 admin. Ett bildspel syns
// för den som har minst albumets egen nivå, och bara admin får ändra något.
const string AccessClaim = "access";
const int AdminLevel = 3;
const string MediaAccessItem = "mediaAccess";

var passwordHasher = new PasswordHasher<object>();
var hashSubject = new object();

// Hjälpkommando för att skapa lösenordshashen: Slideshow.Api.exe hash
// Läser från konsolen i stället för argument, så lösenordet inte hamnar i kommandohistoriken.
if (args.Length > 0 && string.Equals(args[0], "hash", StringComparison.OrdinalIgnoreCase))
{
    Console.Write("Password: ");
    var entered = Console.ReadLine();

    if (string.IsNullOrWhiteSpace(entered))
    {
        Console.Error.WriteLine("Empty password, aborting.");
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

builder.Services.AddAuthorization(options =>
    options.AddPolicy("admin", policy =>
        policy.RequireClaim(AccessClaim, AdminLevel.ToString(CultureInfo.InvariantCulture))));

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

app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();

// Vakten läser ctx.User och måste därför ligga efter UseAuthentication. Utan den
// vore låsta bildspel bara dolda i gränssnittet — slugen går att gissa ur titeln.
app.Use(async (ctx, next) =>
{
    if (ctx.Request.Path.StartsWithSegments("/media", out var rest) && !await MediaAllowed(rest, ctx, store))
    {
        ctx.Response.StatusCode = StatusCodes.Status404NotFound;
        return;
    }

    await next();
});

// Bilderna ligger utanför wwwroot. Filnamnen är GUID:er, därför immutable cache.
app.UseStaticFiles(new StaticFileOptions
{
    FileProvider = new PhysicalFileProvider(store.MediaRoot),
    RequestPath = "/media",
    OnPrepareResponse = ctx =>
    {
        var access = ctx.Context.Items[MediaAccessItem] as AlbumAccess? ?? AlbumAccess.Public;
        // Låsta bildspel får bara ligga i besökarens egen cache, aldrig i en delad.
        var scope = access == AlbumAccess.Public ? "public" : "private";
        ctx.Context.Response.Headers.CacheControl = $"{scope}, max-age=31536000, immutable";
    }
});

var api = app.MapGroup("/api");

api.MapGet("/session", (HttpContext ctx, IAntiforgery antiforgery) =>
{
    var tokens = antiforgery.GetAndStoreTokens(ctx);
    return Results.Ok(new
    {
        isAdmin = IsAdmin(ctx),
        level = LevelOf(ctx),
        token = tokens.RequestToken
    });
});

api.MapPost("/login", async (LoginRequest body, HttpContext ctx, IConfiguration config) =>
{
    // Högsta nivån först, så att samma lösenord på två nivåer ger den högre.
    (string Key, int Level)[] candidates =
    [
        ("Slideshow:AdminPasswordHash", AdminLevel),
        ("Slideshow:FamilyPasswordHash", (int)AlbumAccess.Family),
        ("Slideshow:ExtendedPasswordHash", (int)AlbumAccess.Extended)
    ];

    var password = body.Password ?? string.Empty;
    var granted = 0;
    var anyConfigured = false;

    foreach (var (key, level) in candidates)
    {
        var storedHash = config[key];
        if (string.IsNullOrWhiteSpace(storedHash)) continue;

        anyConfigured = true;

        if (passwordHasher.VerifyHashedPassword(hashSubject, storedHash, password) != PasswordVerificationResult.Failed)
        {
            granted = level;
            break;
        }
    }

    // Hellre neka inloggning än att ta ner hela den publika siten på en saknad hash.
    if (!anyConfigured)
    {
        return Results.Json(
            new { error = "No password hash is configured on the server." },
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }

    // Samma svar oavsett nivå: annars går det att kartlägga vilka lösenord som finns.
    if (granted == 0)
    {
        return Results.Json(new { error = "Wrong password." }, statusCode: StatusCodes.Status401Unauthorized);
    }

    var identity = new ClaimsIdentity(
        [
            new Claim(ClaimTypes.Name, granted == AdminLevel ? "admin" : $"level{granted}"),
            new Claim(AccessClaim, granted.ToString(CultureInfo.InvariantCulture))
        ],
        CookieAuthenticationDefaults.AuthenticationScheme);

    await ctx.SignInAsync(
        CookieAuthenticationDefaults.AuthenticationScheme,
        new ClaimsPrincipal(identity),
        new AuthenticationProperties { IsPersistent = true });

    return Results.Ok(new { isAdmin = granted == AdminLevel, level = granted });
}).RequireRateLimiting("login");

api.MapPost("/logout", async (HttpContext ctx) =>
{
    await ctx.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    return Results.Ok();
});

api.MapGet("/slideshows", async (HttpContext ctx, AlbumStore albums, CancellationToken ct) =>
{
    var list = await albums.ListAsync(IsAdmin(ctx), ct);
    var level = LevelOf(ctx);

    // Titel och omslag visas även för låsta bildspel. Själva bilderna gör det inte.
    return Results.Ok(list.Select(a => new
    {
        a.Slug,
        a.Title,
        a.CreatedUtc,
        isDraft = a.PublishedUtc is null,
        access = (int)a.Access,
        locked = level < (int)a.Access,
        slideCount = a.Slides.Count,
        cover = a.Slides.Count > 0 ? $"/media/{a.Slug}/thumb/{a.Slides[0].StoredName}" : null
    }));
});

api.MapGet("/slideshows/{slug}", async (string slug, HttpContext ctx, AlbumStore albums, CancellationToken ct) =>
{
    var album = await albums.GetAsync(slug, ct);
    if (album is null) return Results.NotFound();

    if (album.PublishedUtc is null && !IsAdmin(ctx)) return Results.NotFound();

    if (LevelOf(ctx) < (int)album.Access)
    {
        // Titeln syns redan på startsidan, så den kan följas med. Inga bild-URL:er.
        return Results.Json(
            new { locked = true, access = (int)album.Access, album.Title },
            statusCode: StatusCodes.Status403Forbidden);
    }

    return Results.Ok(new
    {
        album.Slug,
        album.Title,
        album.CreatedUtc,
        isDraft = album.PublishedUtc is null,
        access = (int)album.Access,
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
    var access = body.Access ?? AlbumAccess.Public;
    if (!Enum.IsDefined(access)) return Results.BadRequest(new { error = "Unknown access level." });

    var album = await albums.CreateAsync(body.Title, access, ct);
    return Results.Ok(new { album.Slug, album.Title, access = (int)album.Access });
}).RequireAuthorization("admin");

api.MapPost("/slideshows/{slug}/images", async (
    string slug, IFormFile file, AlbumStore albums, CancellationToken ct) =>
{
    var album = await albums.GetAsync(slug, ct);
    if (album is null) return Results.NotFound();

    if (album.Slides.Count >= AlbumStore.MaxSlidesPerAlbum)
    {
        return Results.BadRequest(new
        {
            error = $"A slideshow holds at most {AlbumStore.MaxSlidesPerAlbum} images."
        });
    }

    if (file.Length == 0) return Results.BadRequest(new { error = "The file is empty." });

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
}).RequireAuthorization("admin");

api.MapPost("/slideshows/{slug}/publish", async (string slug, AlbumStore albums, CancellationToken ct) =>
{
    var album = await albums.GetAsync(slug, ct);
    if (album is null) return Results.NotFound();

    album.PublishedUtc = DateTimeOffset.UtcNow;
    await albums.SaveAsync(album, ct);

    return Results.Ok(new { album.Slug, slideCount = album.Slides.Count });
}).RequireAuthorization("admin");

api.MapPut("/slideshows/{slug}/access", async (
    string slug, AccessRequest body, AlbumStore albums, CancellationToken ct) =>
{
    if (body.Access is not { } access || !Enum.IsDefined(access))
    {
        return Results.BadRequest(new { error = "Unknown access level." });
    }

    var album = await albums.GetAsync(slug, ct);
    if (album is null) return Results.NotFound();

    album.Access = access;
    await albums.SaveAsync(album, ct);

    return Results.Ok(new { access = (int)album.Access });
}).RequireAuthorization("admin");

api.MapPut("/slideshows/order", async (OrderRequest body, AlbumStore albums, CancellationToken ct) =>
{
    await albums.SetOrderAsync(body.Slugs ?? [], ct);
    return Results.Ok();
}).RequireAuthorization("admin");

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
}).RequireAuthorization("admin");

api.MapDelete("/slideshows/{slug}", (string slug, AlbumStore albums) =>
{
    albums.Delete(slug);
    return Results.Ok();
}).RequireAuthorization("admin");

app.MapGet("/s/{slug}", () =>
    Results.File(Path.Combine(app.Environment.WebRootPath, "slideshow.html"), "text/html"));

app.Run();
return 0;

static int LevelOf(HttpContext ctx) =>
    int.TryParse(ctx.User.FindFirstValue(AccessClaim), CultureInfo.InvariantCulture, out var level) ? level : 0;

static bool IsAdmin(HttpContext ctx) => LevelOf(ctx) >= AdminLevel;

static async Task<bool> MediaAllowed(PathString rest, HttpContext ctx, AlbumStore albums)
{
    var parts = rest.Value?.Split('/', StringSplitOptions.RemoveEmptyEntries);
    if (parts is not { Length: 3 }) return false;

    var gate = await albums.GetGateAsync(parts[0], ctx.RequestAborted);
    if (gate is null) return false;

    if (!gate.IsPublished && !IsAdmin(ctx)) return false;

    ctx.Items[MediaAccessItem] = gate.Access;

    if (LevelOf(ctx) >= (int)gate.Access) return true;

    // Omslaget ska synas i listan även för låsta bildspel. Bara den ena tumnageln —
    // hela thumb-katalogen skulle läcka innehållet.
    return parts[1] == "thumb" && parts[2] == gate.CoverStoredName;
}

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
internal record CreateRequest(string? Title, AlbumAccess? Access);
internal record CaptionRequest(string? Caption);
internal record AccessRequest(AlbumAccess? Access);
internal record OrderRequest(string[]? Slugs);
