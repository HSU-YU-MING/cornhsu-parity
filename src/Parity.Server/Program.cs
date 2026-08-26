using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Parity.Server.Data;

// ─────────────────────────────────────────────────────────────
// Parity.Server — 網頁外殼(網頁外殼規畫書 5.1)。
// 鐵則:雲端不跑瀏覽器。唯一會寫入報告的入口是 POST /api/runs,
// 它接受「已完成的報告」,不存在任何「給我 URL 我去掃」的入口。
// M3 起:讀取端點一律要登入(cookie),寫入維持 CI token;邀請制、無開放註冊。
// ─────────────────────────────────────────────────────────────

// 管理指令模式(不起 web host):
//   create-project <name>            → 建專案 + 印一次 CI token
//   create-invite <project> <email> [role] → 印一次邀請連結(bootstrap 第一個 Owner 也走這裡)
if (args.Length >= 1 && args[0] is "create-project" or "create-invite")
{
    return await AdminCommands.RunAsync(args);
}

var builder = WebApplication.CreateBuilder(args);

// 預設只聽本機;部署(M5)用 ASPNETCORE_URLS 覆蓋。
// M1 時代的「非 localhost 拒啟」保險絲已拆——讀取端點自 M3 起一律要登入,
// 對外綁定不再等於裸奔(TLS 與網域是 M5 的事)。
if (builder.Configuration["urls"] is null && Environment.GetEnvironmentVariable("ASPNETCORE_URLS") is null)
    builder.WebHost.UseUrls("http://127.0.0.1:4322");

builder.Services.AddDbContext<ServerDbContext>(o => o.UseSqlite(
    builder.Configuration.GetConnectionString("parity")
    ?? $"Data Source={Path.Combine(builder.Environment.ContentRootPath, "parity-server.db")}"));

builder.Services.AddIdentityCore<AppUser>(o =>
    {
        // 邀請制小工具的務實密碼政策:長度重於字元雜技(好記的長句 > 難記的短亂碼)
        o.Password.RequiredLength = 10;
        o.Password.RequireNonAlphanumeric = false;
        o.Password.RequireUppercase = false;
        o.Password.RequireLowercase = false;
        o.Password.RequireDigit = false;
        o.User.RequireUniqueEmail = true;
        o.Lockout.MaxFailedAccessAttempts = 5; // 內建暴力破解節流
    })
    .AddEntityFrameworkStores<ServerDbContext>()
    .AddSignInManager();

builder.Services.AddAuthentication(IdentityConstants.ApplicationScheme)
    .AddCookie(IdentityConstants.ApplicationScheme, o =>
    {
        o.Cookie.Name = "parity.session";
        o.Cookie.HttpOnly = true;
        o.Cookie.SameSite = SameSiteMode.Strict; // 同源 SPA;Strict 順便當 CSRF 防線
        o.ExpireTimeSpan = TimeSpan.FromDays(30);
        o.SlidingExpiration = true;
        // API 伺服器:未登入回 401/403,不做 302 導頁(前端自己導向 /login)
        o.Events.OnRedirectToLogin = ctx => { ctx.Response.StatusCode = StatusCodes.Status401Unauthorized; return Task.CompletedTask; };
        o.Events.OnRedirectToAccessDenied = ctx => { ctx.Response.StatusCode = StatusCodes.Status403Forbidden; return Task.CompletedTask; };
    });
builder.Services.AddAuthorization();

// 報告是純數值 JSON;20MB 是護欄不是目標(50+ 頁的大站接近時,413 要給出路)
builder.WebHost.ConfigureKestrel(k => k.Limits.MaxRequestBodySize = 20 * 1024 * 1024);

var app = builder.Build();

using (var scope = app.Services.CreateScope())
    scope.ServiceProvider.GetRequiredService<ServerDbContext>().Database.Migrate();

app.UseAuthentication();
app.UseAuthorization();

// 健康檢查(匿名):uptime 監測要 ping 的是「資料庫還活著」,不是「首頁回得了 200」。
// 不洩漏任何內容,只回 ok / 503。
app.MapGet("/healthz", async (ServerDbContext db) =>
{
    try
    {
        await db.Database.ExecuteSqlRawAsync("SELECT 1");
        return Results.Text("ok");
    }
    catch
    {
        return Results.Text("db unreachable", statusCode: StatusCodes.Status503ServiceUnavailable);
    }
});

// ── 認證 ───────────────────────────────────────────────────────
app.MapPost("/api/auth/login", async (
    LoginRequest req, SignInManager<AppUser> signIn, UserManager<AppUser> users, ServerDbContext db) =>
{
    var user = await users.FindByEmailAsync(req.Email.Trim());
    if (user is null)
        return Results.Json(new { error = "email or password is incorrect." }, statusCode: 401);
    var result = await signIn.PasswordSignInAsync(user, req.Password, isPersistent: true, lockoutOnFailure: true);
    if (result.IsLockedOut)
        return Results.Json(new { error = "too many failed attempts — try again in a few minutes." }, statusCode: 401);
    if (!result.Succeeded)
        return Results.Json(new { error = "email or password is incorrect." }, statusCode: 401);
    return Results.Json(await MeAsync(user, db));
});

app.MapPost("/api/auth/logout", async (SignInManager<AppUser> signIn) =>
{
    await signIn.SignOutAsync();
    return Results.NoContent();
}).RequireAuthorization();

app.MapGet("/api/auth/me", async (ClaimsPrincipal principal, UserManager<AppUser> users, ServerDbContext db) =>
{
    var user = await users.GetUserAsync(principal);
    return user is null ? Results.Unauthorized() : Results.Json(await MeAsync(user, db));
}).RequireAuthorization();

// ── 邀請(接受端不需登入——連結本身就是憑證)───────────────────
app.MapGet("/api/invites/{token}", async (string token, ServerDbContext db) =>
{
    var invite = await Invites.FindUsableAsync(db, token, DateTimeOffset.UtcNow);
    return invite is null
        ? Results.Json(new { error = "this invite link is invalid, expired, or already used — ask the project owner for a new one." }, statusCode: 404)
        : Results.Json(new { project = invite.Project!.Name, email = invite.Email, role = invite.Role });
});

app.MapPost("/api/invites/{token}/accept", async (
    string token, AcceptRequest req, ServerDbContext db,
    UserManager<AppUser> users, SignInManager<AppUser> signIn) =>
{
    var invite = await Invites.FindUsableAsync(db, token, DateTimeOffset.UtcNow);
    if (invite is null)
        return Results.Json(new { error = "this invite link is invalid, expired, or already used — ask the project owner for a new one." }, statusCode: 404);

    var user = await users.FindByEmailAsync(invite.Email);
    if (user is null)
    {
        user = new AppUser { Id = Guid.NewGuid(), UserName = invite.Email, Email = invite.Email };
        var created = await users.CreateAsync(user, req.Password);
        if (!created.Succeeded)
            return Results.Json(new { error = string.Join(" ", created.Errors.Select(e => e.Description)) }, statusCode: 400);
    }
    else
    {
        // 既有使用者被重邀(換角色/回鍋):要證明是本人——密碼對得上才接受
        if (!await users.CheckPasswordAsync(user, req.Password))
            return Results.Json(new { error = "an account with this email already exists — enter its password to accept." }, statusCode: 401);
    }

    await Invites.AcceptAsync(db, invite, user.Id, DateTimeOffset.UtcNow);
    await signIn.SignInAsync(user, isPersistent: true);
    return Results.Json(await MeAsync(user, db));
});

// ── 專案管理(Owner 限定)────────────────────────────────────────
app.MapGet("/api/projects/{id:guid}/members", async (Guid id, ClaimsPrincipal principal, ServerDbContext db) =>
{
    var userId = UserId(principal);
    if (!await Membership.IsOwnerAsync(db, userId, id)) return Results.Forbid();
    var members = await db.Members.Where(m => m.ProjectId == id)
        .OrderBy(m => m.CreatedAt)
        .Select(m => new { userId = m.UserId, email = m.User!.Email, role = m.Role, joined = m.CreatedAt })
        .ToListAsync();
    var now = DateTimeOffset.UtcNow;
    var invites = await db.Invites
        .Where(i => i.ProjectId == id && i.AcceptedAt == null && i.ExpiresAt > now)
        .Select(i => new { email = i.Email, role = i.Role, expires = i.ExpiresAt })
        .ToListAsync();
    return Results.Json(new { members, pendingInvites = invites });
}).RequireAuthorization();

app.MapPost("/api/projects/{id:guid}/invites", async (
    Guid id, InviteRequest req, ClaimsPrincipal principal, HttpRequest http, ServerDbContext db) =>
{
    var userId = UserId(principal);
    if (!await Membership.IsOwnerAsync(db, userId, id)) return Results.Forbid();
    if (!ProjectRole.IsValid(req.Role))
        return Results.Json(new { error = "role must be owner, member or viewer." }, statusCode: 400);

    var (invite, token) = Invites.Create(id, req.Email, req.Role, DateTimeOffset.UtcNow);
    db.Invites.Add(invite);
    await db.SaveChangesAsync();
    // 連結親手交給對方(v1 不寄信)——只回這一次
    return Results.Json(new { link = $"{http.Scheme}://{http.Host}/invite/{token}", expires = invite.ExpiresAt });
}).RequireAuthorization();

app.MapDelete("/api/projects/{id:guid}/members/{memberUserId:guid}", async (
    Guid id, Guid memberUserId, ClaimsPrincipal principal, ServerDbContext db) =>
{
    var userId = UserId(principal);
    if (!await Membership.IsOwnerAsync(db, userId, id)) return Results.Forbid();
    if (await Membership.WouldRemoveLastOwnerAsync(db, id, memberUserId))
        return Results.Json(new { error = "cannot remove the last owner — hand ownership to someone first." }, statusCode: 400);
    await db.Members.Where(m => m.ProjectId == id && m.UserId == memberUserId).ExecuteDeleteAsync();
    return Results.NoContent();
}).RequireAuthorization();

app.MapPost("/api/projects/{id:guid}/token/rotate", async (Guid id, ClaimsPrincipal principal, ServerDbContext db) =>
{
    var userId = UserId(principal);
    if (!await Membership.IsOwnerAsync(db, userId, id)) return Results.Forbid();
    var project = await db.Projects.FindAsync(id);
    if (project is null) return Results.NotFound();
    var token = ProjectToken.Generate();
    project.TokenHash = ProjectToken.Hash(token); // 舊 token 立即失效(撤銷 = 換掉)
    await db.SaveChangesAsync();
    return Results.Json(new { token }); // 只回這一次
}).RequireAuthorization();

// ── 報告接收(CI token,與人的登入無關)──────────────────────────
app.MapPost("/api/runs", async (HttpRequest request, ServerDbContext db) =>
{
    var project = await Auth.ResolveProjectAsync(request, db);
    if (project is null)
        return Results.Json(new { error = "missing or unknown token (Authorization: Bearer <project token>)" },
            statusCode: StatusCodes.Status401Unauthorized);

    using var reader = new StreamReader(request.Body);
    var raw = await reader.ReadToEndAsync();

    var meta = new RunMetadata(
        CommitSha: Header(request, "X-Parity-Commit"),
        Branch: Header(request, "X-Parity-Branch"),
        TriggeredBy: Header(request, "X-Parity-Triggered-By"),
        // push 端算好的真實 gate 結果(pass/fail);缺頭 → null → 伺服器退回預設口徑
        GateFailed: Header(request, "X-Parity-Gate") switch
        {
            "fail" => true,
            "pass" => false,
            _ => null,
        },
        RepoUrl: Header(request, "X-Parity-Repo-Url"));

    Run run;
    try
    {
        run = RunIngest.Parse(raw, meta, DateTimeOffset.UtcNow);
    }
    catch (SchemaTooNewException ex)
    {
        return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status422UnprocessableEntity);
    }
    catch (InvalidOperationException ex)
    {
        return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status400BadRequest);
    }

    run.ProjectId = project.Id;
    db.Runs.Add(run);
    await db.SaveChangesAsync();

    return Results.Json(new
    {
        id = run.Id,
        project = project.Name,
        score = run.Score,
        gateFailed = run.GateFailed,
        pages = run.Pages.Count,
        url = $"/runs/{run.Id}",
    }, statusCode: StatusCodes.Status201Created);

    static string? Header(HttpRequest r, string name)
        => r.Headers.TryGetValue(name, out var v) && !string.IsNullOrWhiteSpace(v) ? v.ToString() : null;
});

// ── 讀取(登入 + 成員資格過濾)──────────────────────────────────
app.MapGet("/api/runs", async (ClaimsPrincipal principal, ServerDbContext db, [FromQuery] int limit = 50) =>
{
    var visible = Membership.ProjectIdsFor(db, UserId(principal));
    var runs = await db.Runs.Where(r => visible.Contains(r.ProjectId))
        .OrderByDescending(r => r.CreatedAt)
        .Take(Math.Clamp(limit, 1, 200))
        .Select(r => new
        {
            r.Id,
            r.CreatedAt,
            r.Score,
            r.GateFailed,
            r.CommitSha,
            r.Branch,
            r.TriggeredBy,
            r.RepoUrl,
            Project = r.Project!.Name,
            Pages = r.Pages.Count,
        })
        .ToListAsync();
    return Results.Json(runs);
}).RequireAuthorization();

app.MapGet("/api/branches", async (ClaimsPrincipal principal, ServerDbContext db) =>
    Results.Json(await Queries.BranchesAsync(db, UserId(principal)))).RequireAuthorization();

app.MapGet("/api/overview", async (ClaimsPrincipal principal, ServerDbContext db, [FromQuery] string? branch) =>
    Results.Json(await Queries.OverviewAsync(db, UserId(principal), branch))).RequireAuthorization();

app.MapGet("/api/trend", async (
    Guid project, string route, ClaimsPrincipal principal, ServerDbContext db, [FromQuery] string? branch) =>
    Results.Json(await Queries.TrendAsync(db, UserId(principal), project, route, branch))).RequireAuthorization();

// 「跟上次比變了什麼」——同專案、同分支的前一筆(引擎 BaselineComparer 同語意)
app.MapGet("/api/runs/{id:guid}/changes", async (Guid id, ClaimsPrincipal principal, ServerDbContext db) =>
{
    var changes = await RunCompare.ChangesAsync(db, UserId(principal), id);
    return changes is null ? Results.NotFound() : Results.Json(changes);
}).RequireAuthorization();

app.MapGet("/api/runs/{id:guid}", async (Guid id, ClaimsPrincipal principal, ServerDbContext db) =>
{
    var visible = Membership.ProjectIdsFor(db, UserId(principal));
    var run = await db.Runs.Where(r => r.Id == id && visible.Contains(r.ProjectId))
        .Select(r => new
        {
            r.Id,
            r.CreatedAt,
            r.Score,
            r.GateFailed,
            r.CommitSha,
            r.Branch,
            r.TriggeredBy,
            r.RepoUrl,
            Project = r.Project!.Name,
            r.RawReportGzip,
        })
        .FirstOrDefaultAsync();
    if (run is null) return Results.NotFound();

    // 逐條落差從原文 blob 重建(關聯表刻意不存 Diff,見 Entities.cs)
    var doc = System.Text.Json.JsonSerializer.Deserialize<Parity.Engine.ReportDocument>(
        ReportBlob.Decompress(run.RawReportGzip), Parity.Engine.ReportWire.Compact)!;
    return Results.Json(new
    {
        id = run.Id,
        createdAt = run.CreatedAt,
        score = run.Score,
        gateFailed = run.GateFailed,
        commitSha = run.CommitSha,
        branch = run.Branch,
        triggeredBy = run.TriggeredBy,
        repoUrl = run.RepoUrl,
        project = run.Project,
        pages = doc.Reports.Select(rep => new
        {
            route = rep.Route,
            url = rep.Url,
            score = Parity.Engine.FidelityScore.Compute([rep]),
            designNodes = rep.Summary.DesignNodes,
            matched = rep.Summary.Matched,
            unmatched = rep.Summary.Unmatched,
            critical = rep.Summary.Critical,
            serious = rep.Summary.Serious,
            medium = rep.Summary.Medium,
            minor = rep.Summary.Minor,
            maxSeverity = rep.Summary.MaxSeverity.ToString().ToLowerInvariant(),
            diffs = rep.Nodes.SelectMany(n => n.Diffs.Select(d => new
            {
                designLayer = n.DesignLayer,
                selector = n.Selector,
                matchedBy = n.MatchedBy,
                prop = d.Prop,
                expected = d.Expected,
                actual = d.Actual,
                unit = d.Unit,
                delta = d.Delta,
                severity = d.Severity.ToString().ToLowerInvariant(),
                status = d.Status.ToString().ToLowerInvariant(),
                soft = d.Soft,
            })),
        }),
    });
}).RequireAuthorization();

app.MapGet("/api/runs/{id:guid}/report", async (Guid id, ClaimsPrincipal principal, ServerDbContext db) =>
{
    var visible = Membership.ProjectIdsFor(db, UserId(principal));
    var blob = await db.Runs.Where(r => r.Id == id && visible.Contains(r.ProjectId))
        .Select(r => r.RawReportGzip).FirstOrDefaultAsync();
    return blob is null
        ? Results.NotFound()
        : Results.Content(ReportBlob.Decompress(blob), "application/json; charset=utf-8");
}).RequireAuthorization();

// ── 前端:web/ 的建置產物(vite build → wwwroot)──────────────────
if (File.Exists(Path.Combine(app.Environment.WebRootPath ?? "", "index.html")))
{
    app.UseDefaultFiles();
    app.UseStaticFiles();
    app.MapFallbackToFile("index.html"); // /runs/{id}、/login、/invite/{token} 深連結由前端路由接手
}
else
{
    app.MapGet("/", () => Results.Content(
        "<!doctype html><meta charset=\"utf-8\"><title>Parity</title>" +
        "<p style=\"font-family:system-ui;margin:3rem\">The web UI has not been built. " +
        "Run <code>npm run build</code> in <code>web/</code>, then restart the server.</p>",
        "text/html; charset=utf-8"));
}

app.Run();
return 0;

// ─────────────────────────────────────────────────────────────

static Guid UserId(ClaimsPrincipal principal)
    => Guid.Parse(principal.FindFirstValue(ClaimTypes.NameIdentifier)!);

static async Task<object> MeAsync(AppUser user, ServerDbContext db)
{
    var memberships = await db.Members.Where(m => m.UserId == user.Id)
        .Select(m => new { projectId = m.ProjectId, project = m.Project!.Name, role = m.Role })
        .ToListAsync();
    return new { email = user.Email, memberships };
}

internal sealed record LoginRequest(string Email, string Password);
internal sealed record AcceptRequest(string Password);
internal sealed record InviteRequest(string Email, string Role);

internal static class Auth
{
    /// <summary>Bearer token → hash → 專案。查無 = null(呼叫端回 401,不透露差在哪)。</summary>
    public static async Task<Project?> ResolveProjectAsync(HttpRequest request, ServerDbContext db)
    {
        var header = request.Headers.Authorization.ToString();
        if (!header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) return null;
        var token = header["Bearer ".Length..].Trim();
        if (token.Length is < 20 or > 200) return null; // 護欄:不是我們發的形狀就不用查庫
        var hash = ProjectToken.Hash(token);
        return await db.Projects.FirstOrDefaultAsync(p => p.TokenHash == hash);
    }
}

internal static class AdminCommands
{
    public static async Task<int> RunAsync(string[] args)
    {
        var dbPath = Environment.GetEnvironmentVariable("PARITY_SERVER_DB")
            ?? Path.Combine(Directory.GetCurrentDirectory(), "parity-server.db");
        var options = new DbContextOptionsBuilder<ServerDbContext>()
            .UseSqlite($"Data Source={dbPath}").Options;
        await using var db = new ServerDbContext(options);
        await db.Database.MigrateAsync();

        switch (args[0])
        {
            case "create-project" when args.Length >= 2 && !string.IsNullOrWhiteSpace(args[1]):
                {
                    var token = ProjectToken.Generate();
                    db.Projects.Add(new Project
                    {
                        Id = Guid.NewGuid(),
                        Name = args[1],
                        TokenHash = ProjectToken.Hash(token),
                        CreatedAt = DateTimeOffset.UtcNow,
                    });
                    await db.SaveChangesAsync();
                    Console.WriteLine($"project created: {args[1]}   (db: {dbPath})");
                    Console.WriteLine("\nCI token (shown once — store it in your CI secrets now):");
                    Console.WriteLine($"  {token}");
                    Console.WriteLine("\nnext: create the first owner with  create-invite <project> <email> owner");
                    return 0;
                }
            case "create-invite" when args.Length >= 3:
                {
                    var project = await db.Projects.FirstOrDefaultAsync(p => p.Name == args[1]);
                    if (project is null)
                    {
                        Console.Error.WriteLine($"no project named \"{args[1]}\" (create it with create-project first).");
                        return 2;
                    }
                    var role = args.Length >= 4 ? args[3] : ProjectRole.Owner;
                    if (!ProjectRole.IsValid(role))
                    {
                        Console.Error.WriteLine($"unknown role: {role} (use owner/member/viewer)");
                        return 2;
                    }
                    var (invite, token) = Invites.Create(project.Id, args[2], role, DateTimeOffset.UtcNow);
                    db.Invites.Add(invite);
                    await db.SaveChangesAsync();
                    Console.WriteLine($"invite created: {args[2]} → {project.Name} as {role} (expires in {Invites.Lifetime.TotalDays:0} days)");
                    Console.WriteLine("\nhand this link to them (shown once):");
                    Console.WriteLine($"  http://127.0.0.1:4322/invite/{token}");
                    return 0;
                }
            default:
                Console.Error.WriteLine("""
                    usage:
                      create-project <name>
                      create-invite <project> <email> [owner|member|viewer]
                    """);
                return 2;
        }
    }
}
