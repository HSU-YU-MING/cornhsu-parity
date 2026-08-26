using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Parity.Server.Data;

// ─────────────────────────────────────────────────────────────
// Parity.Server — 網頁外殼 M1(網頁外殼規畫書 5.1)。
// 鐵則:雲端不跑瀏覽器。唯一會寫入資料的入口是 POST /api/runs,
// 它接受「已完成的報告」,不存在任何「給我 URL 我去掃」的入口——
// 這個 API 面本身就是鐵則的執行機制(規畫書 5.1)。
// M1 綁 127.0.0.1(同 parity serve 的原則);對外部署是 M5,帳號權限是 M3。
// ─────────────────────────────────────────────────────────────

// create-project 模式:dotnet run -- create-project "名字" → 印一次 token(只印這次)
if (args.Length >= 1 && args[0] == "create-project")
{
    return await AdminCommands.CreateProjectAsync(args.Skip(1).ToArray());
}

var builder = WebApplication.CreateBuilder(args);

// M1 預設只聽本機——報告含站點結構,跟 parity serve 同一個理由不讓區網掃到。
// 部署(M5)時用 ASPNETCORE_URLS 覆蓋。
if (builder.Configuration["urls"] is null && Environment.GetEnvironmentVariable("ASPNETCORE_URLS") is null)
    builder.WebHost.UseUrls("http://127.0.0.1:4322");

builder.Services.AddDbContext<ServerDbContext>(o => o.UseSqlite(
    builder.Configuration.GetConnectionString("parity")
    ?? $"Data Source={Path.Combine(builder.Environment.ContentRootPath, "parity-server.db")}"));

// 報告是純數值 JSON,實測 21 頁的 dogfooding 報告 ~200KB;20MB 是護欄不是目標
builder.WebHost.ConfigureKestrel(k => k.Limits.MaxRequestBodySize = 20 * 1024 * 1024);

var app = builder.Build();

// 從第一天就走 migrations(Parity.Storage 的 EnsureCreated 接管成本,這裡不再付一次)
using (var scope = app.Services.CreateScope())
    scope.ServiceProvider.GetRequiredService<ServerDbContext>().Database.Migrate();

// ── 報告接收(唯一的寫入口;Bearer token = 專案身分)─────────────
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
        TriggeredBy: Header(request, "X-Parity-Triggered-By"));

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

// ── 讀取(M1:本機瀏覽,無帳號;M3 加 Identity 後上鎖)────────────
app.MapGet("/api/runs", async (ServerDbContext db, [FromQuery] int limit = 50) =>
{
    var runs = await db.Runs.OrderByDescending(r => r.CreatedAt)
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
            Project = r.Project!.Name,
            Pages = r.Pages.Count,
        })
        .ToListAsync();
    return Results.Json(runs);
});

app.MapGet("/api/runs/{id:guid}", async (Guid id, ServerDbContext db) =>
{
    var run = await db.Runs.Where(r => r.Id == id)
        .Select(r => new
        {
            r.Id,
            r.CreatedAt,
            r.Score,
            r.GateFailed,
            r.CommitSha,
            r.Branch,
            r.TriggeredBy,
            Project = r.Project!.Name,
            Pages = r.Pages.Select(p => new
            {
                p.Route,
                p.Url,
                p.Score,
                p.DesignNodes,
                p.Matched,
                p.Unmatched,
                p.Critical,
                p.Serious,
                p.Medium,
                p.Minor,
                p.MaxSeverity,
                Diffs = p.Diffs.Select(d => new
                {
                    d.DesignLayer,
                    d.Selector,
                    d.Prop,
                    d.Expected,
                    d.Actual,
                    d.Unit,
                    d.Delta,
                    d.Severity,
                    d.Status,
                    d.Soft,
                    d.MatchedBy,
                }),
            }),
        })
        .FirstOrDefaultAsync();
    return run is null ? Results.NotFound() : Results.Json(run);
});

// ── 最陽春頁面(M1 的「最醜能動版」;M2 換 React 投資 UI)──────────
app.MapGet("/", () => Results.Content(M1Page.Html, "text/html; charset=utf-8"));
app.MapGet("/runs/{id:guid}", (Guid id) => Results.Content(M1Page.RunHtml(id), "text/html; charset=utf-8"));

app.Run();
return 0;

// ─────────────────────────────────────────────────────────────

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
    /// <summary>建專案 + 發 token。token 只印這一次,庫裡只有 hash。</summary>
    public static async Task<int> CreateProjectAsync(string[] args)
    {
        if (args.Length < 1 || string.IsNullOrWhiteSpace(args[0]))
        {
            Console.Error.WriteLine("usage: dotnet run --project src/Parity.Server -- create-project <name>");
            return 2;
        }

        var dbPath = Environment.GetEnvironmentVariable("PARITY_SERVER_DB")
            ?? Path.Combine(Directory.GetCurrentDirectory(), "parity-server.db");
        var options = new DbContextOptionsBuilder<ServerDbContext>()
            .UseSqlite($"Data Source={dbPath}").Options;
        await using var db = new ServerDbContext(options);
        await db.Database.MigrateAsync();

        var token = ProjectToken.Generate();
        db.Projects.Add(new Project
        {
            Id = Guid.NewGuid(),
            Name = args[0],
            TokenHash = ProjectToken.Hash(token),
            CreatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();

        Console.WriteLine($"project created: {args[0]}");
        Console.WriteLine($"db: {dbPath}");
        Console.WriteLine();
        Console.WriteLine("API token (shown once — store it in your CI secrets now):");
        Console.WriteLine($"  {token}");
        Console.WriteLine();
        Console.WriteLine("CI usage:  parity push --server <url>   (token via PARITY_TOKEN env var)");
        return 0;
    }
}

/// <summary>M1 的靜態頁:零建置、fetch API 渲染。M2 由 web/(React + Vite)取代。</summary>
internal static class M1Page
{
    public const string Html = """
        <!doctype html><html lang="en"><head><meta charset="utf-8">
        <title>Parity — runs</title>
        <style>
          body { font-family: system-ui, sans-serif; margin: 2rem auto; max-width: 60rem; padding: 0 1rem; }
          table { border-collapse: collapse; width: 100%; }
          th, td { text-align: left; padding: .45rem .7rem; border-bottom: 1px solid #ddd; }
          .fail { color: #b91c1c; font-weight: 600; } .pass { color: #15803d; font-weight: 600; }
          a { color: inherit; }
        </style></head><body>
        <h1>Parity — received runs</h1>
        <table id="t"><thead><tr>
          <th>time (UTC)</th><th>project</th><th>score</th><th>gate</th><th>pages</th><th>branch</th><th>commit</th>
        </tr></thead><tbody></tbody></table>
        <script>
          fetch('/api/runs').then(r => r.json()).then(runs => {
            const tb = document.querySelector('#t tbody');
            for (const r of runs) {
              const tr = document.createElement('tr');
              const cells = [
                `<a href="/runs/${r.id}">${r.createdAt.replace('T',' ').slice(0,19)}</a>`,
                r.project, `${r.score}/100`,
                r.gateFailed ? '<span class="fail">FAIL</span>' : '<span class="pass">PASS</span>',
                r.pages, r.branch ?? '—', r.commitSha ? r.commitSha.slice(0,7) : '—',
              ];
              tr.innerHTML = cells.map(c => `<td>${c}</td>`).join('');
              tb.appendChild(tr);
            }
            if (!runs.length) tb.innerHTML = '<tr><td colspan="7">no runs yet — `parity push` one</td></tr>';
          });
        </script></body></html>
        """;

    public static string RunHtml(Guid id) => $$"""
        <!doctype html><html lang="en"><head><meta charset="utf-8">
        <title>Parity — run</title>
        <style>
          body { font-family: system-ui, sans-serif; margin: 2rem auto; max-width: 70rem; padding: 0 1rem; }
          table { border-collapse: collapse; width: 100%; margin-bottom: 1.5rem; }
          th, td { text-align: left; padding: .35rem .6rem; border-bottom: 1px solid #eee; font-size: .92rem; }
          h2 { margin-top: 2rem; }
          .sev-critical { color: #b91c1c; font-weight: 600; } .sev-serious { color: #c2410c; font-weight: 600; }
          .sev-medium { color: #a16207; } .sev-minor { color: #64748b; }
          code { background: #f3f4f6; padding: 0 .3em; }
        </style></head><body>
        <p><a href="/">← runs</a></p><div id="out">loading…</div>
        <script>
          fetch('/api/runs/{{id}}').then(r => r.ok ? r.json() : Promise.reject(r.status)).then(run => {
            let h = `<h1>${run.project} — ${run.score}/100 ${run.gateFailed ? '❌' : '✅'}</h1>
              <p>${run.createdAt.replace('T',' ').slice(0,19)} UTC · ${run.branch ?? ''} ${run.commitSha ? run.commitSha.slice(0,7) : ''}</p>`;
            for (const p of run.pages) {
              h += `<h2>${p.route} — ${p.score}/100 (matched ${p.matched}/${p.designNodes})</h2>`;
              if (!p.diffs.length) { h += '<p>no diffs 🎉</p>'; continue; }
              h += '<table><thead><tr><th>layer</th><th>prop</th><th>expected → actual</th><th>severity</th></tr></thead><tbody>'
                + p.diffs.map(d => `<tr><td>${d.designLayer}<br><code>${d.selector}</code></td><td>${d.prop}</td>
                    <td>${d.expected} → ${d.actual}${d.unit ?? ''}</td>
                    <td class="sev-${d.severity}">${d.severity}${d.soft ? ' (soft)' : ''}</td></tr>`).join('')
                + '</tbody></table>';
            }
            document.querySelector('#out').innerHTML = h;
          }).catch(() => document.querySelector('#out').textContent = 'run not found');
        </script></body></html>
        """;
}
