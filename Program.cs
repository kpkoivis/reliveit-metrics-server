using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Npgsql;
using NpgsqlTypes;

// Receives the age up snapshots the game posts and appends them to one Postgres table.
// See README.md for the wire format, configuration and example queries.

const string ApiKeyHeader = "X-Metrics-Key";
const int MaxBatchSize = 100;
const int MaxTextLength = 200;
// numeric has no limit of its own; a balance or price this large is not from the game.
const decimal MaxMoney = 1_000_000_000_000_000M;
// A page view beacon is one small object. Anything larger is not from the beacon.
const int MaxPageViewBody = 4 * 1024;

var builder = WebApplication.CreateBuilder(args);

string connectionString = builder.Configuration.GetConnectionString("Metrics") ?? "";
string apiKey = builder.Configuration["Metrics:ApiKey"] ?? "";
string visitorSalt = builder.Configuration["Metrics:VisitorSalt"] ?? "";

if (string.IsNullOrWhiteSpace(connectionString))
    throw new InvalidOperationException("ConnectionStrings:Metrics is not set.");

if (string.IsNullOrWhiteSpace(apiKey))
    throw new InvalidOperationException("Metrics:ApiKey is not set. It must match MetricsConfig.ApiKey in the game.");

if (string.IsNullOrWhiteSpace(visitorSalt))
    throw new InvalidOperationException("Metrics:VisitorSalt is not set. Any long random string works; changing it resets visitor counting.");

// A full batch is a few tens of kilobytes. Anything much larger is not from the game.
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 256 * 1024);

builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower);

// The Web build posts from whatever origin it is hosted on, and the custom key header
// makes the browser ask first.
builder.Services.AddCors(options => options.AddDefaultPolicy(policy =>
    policy.AllowAnyOrigin().WithMethods("POST").WithHeaders("Content-Type", ApiKeyHeader)));

builder.Services.AddSingleton(_ => NpgsqlDataSource.Create(connectionString));

var app = builder.Build();

app.UseCors();

await using (NpgsqlCommand createSchema = app.Services.GetRequiredService<NpgsqlDataSource>().CreateCommand(Sql.Schema))
{
    await createSchema.ExecuteNonQueryAsync();
}

app.MapGet("/healthz", async (NpgsqlDataSource db, CancellationToken ct) =>
{
    await using NpgsqlCommand ping = db.CreateCommand("select 1");
    await ping.ExecuteScalarAsync(ct);
    return Results.Ok(new { status = "ok" });
});

app.MapPost("/v1/snapshots", async (HttpRequest request, SnapshotBatch batch, NpgsqlDataSource db, ILogger<Program> log, CancellationToken ct) =>
{
    if (!KeyMatches(request.Headers[ApiKeyHeader].ToString(), apiKey))
        return Results.Unauthorized();

    if (batch.Snapshots is null || batch.Snapshots.Count == 0)
        return Results.BadRequest(new { error = "The batch has no snapshots." });

    if (batch.Snapshots.Count > MaxBatchSize)
        return Results.BadRequest(new { error = $"A batch may hold at most {MaxBatchSize} snapshots." });

    // A snapshot that does not validate is skipped rather than failing the batch: the game
    // drops a batch the server refuses, and one bad row should not take good ones with it.
    List<ValidSnapshot> valid = new();
    int rejected = 0;

    foreach (Snapshot? snapshot in batch.Snapshots)
    {
        string? problem = Validate(snapshot, out ValidSnapshot? ok);

        if (problem is null)
        {
            valid.Add(ok!);
        }
        else
        {
            rejected++;
            log.LogWarning("Rejected snapshot {Id}: {Problem}", snapshot?.Id, problem);
        }
    }

    int inserted = 0;

    if (valid.Count > 0)
    {
        await using NpgsqlConnection connection = await db.OpenConnectionAsync(ct);
        await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync(ct);
        await using NpgsqlBatch insert = new(connection, transaction);

        foreach (ValidSnapshot s in valid)
            insert.BatchCommands.Add(InsertCommand(s));

        // The game resends a batch whose answer it never saw. "on conflict do nothing"
        // makes that harmless, and the affected row count is the rows that were new.
        inserted = await insert.ExecuteNonQueryAsync(ct);
        await transaction.CommitAsync(ct);
    }

    Snapshot? first = valid.Count > 0 ? valid[0].Source : null;
    log.LogInformation("Stored {Accepted} snapshots from {Platform} / {Build} ({Duplicates} duplicates, {Rejected} rejected)",
        inserted, first?.Platform ?? "?", first?.Build ?? "?", valid.Count - inserted, rejected);

    return Results.Ok(new { accepted = inserted, duplicates = valid.Count - inserted, rejected });
});

// The page view beacon on kurkisoft.com. Unlike the snapshots there is no key: the page is
// public, so a key in it would be too. Validation, the nginx rate limit and the likely_bot
// flag are what keep the table honest; filter on likely_bot when querying.
app.MapPost("/v1/pageviews", async (HttpRequest request, NpgsqlDataSource db, ILogger<Program> log, CancellationToken ct) =>
{
    // navigator.sendBeacon cannot preflight, so the page posts its JSON as text/plain and
    // the body is read by hand here.
    if (request.ContentLength is > MaxPageViewBody)
        return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);

    string body;
    using (StreamReader reader = new(request.Body, Encoding.UTF8))
        body = await reader.ReadToEndAsync(ct);

    if (body.Length > MaxPageViewBody)
        return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);

    PageView? page;

    try
    {
        page = JsonSerializer.Deserialize<PageView>(body, Sql.JsonOptions);
    }
    catch (JsonException)
    {
        return Results.BadRequest(new { error = "The body is not JSON." });
    }

    string? problem = ValidatePageView(page, out ValidPageView? ok);

    if (problem is not null)
    {
        log.LogWarning("Rejected page view: {Problem}", problem);
        return Results.BadRequest(new { error = problem });
    }

    ValidPageView v = ok!;
    string userAgent = Truncate(request.Headers.UserAgent.ToString());
    bool likelyBot = userAgent.Length == 0 || Patterns.BotUserAgent.IsMatch(userAgent);

    await using NpgsqlCommand insert = db.CreateCommand(Sql.InsertPageView);
    insert.Parameters.Add(new() { Value = VisitorHash(visitorSalt, ClientAddress(request), userAgent) });
    insert.Parameters.Add(new() { Value = v.Event });
    insert.Parameters.Add(new() { Value = v.Path });
    insert.Parameters.Add(Text(v.Referrer));
    insert.Parameters.Add(new() { NpgsqlDbType = NpgsqlDbType.Integer, Value = (object?)v.Viewport ?? DBNull.Value });
    insert.Parameters.Add(Text(userAgent.Length == 0 ? null : userAgent));
    insert.Parameters.Add(new() { Value = likelyBot });
    await insert.ExecuteNonQueryAsync(ct);

    return Results.Ok(new { stored = true });
});

app.Run();

static bool KeyMatches(string given, string expected)
{
    return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(given), Encoding.UTF8.GetBytes(expected));
}

// Returns what is wrong with the snapshot, or null - and the cleaned up snapshot - when
// nothing is.
static string? Validate(Snapshot? s, out ValidSnapshot? valid)
{
    valid = null;

    if (s is null)
        return "null entry";

    if (!Guid.TryParse(s.Id, out Guid id))
        return "id is not a guid";

    if (!DateTimeOffset.TryParse(s.RecordedAt, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTimeOffset recordedAt))
        return "recorded_at is not a timestamp";

    if (string.IsNullOrWhiteSpace(s.UserId) || TooLong(s.UserId))
        return "user_id is missing or too long";

    if (string.IsNullOrWhiteSpace(s.GameId) || TooLong(s.GameId))
        return "game_id is missing or too long";

    if (s.AgeMonths is not (>= 0 and <= 2400))
        return "age_months is missing or out of range";

    List<Activity> sideActivities = (s.SideActivities ?? new()).Where(a => a is not null).Select(a => a!).ToList();
    List<string> iapPurchases = (s.IapPurchases ?? new()).Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p!).ToList();

    List<ShopPurchase> shopPurchases = (s.ShopPurchases ?? new()).Where(p => p is not null).Select(p => p!).ToList();
    List<string> activities = (s.Activities ?? new()).Where(a => !string.IsNullOrWhiteSpace(a)).Select(a => a!).ToList();

    if (sideActivities.Count > 16 || iapPurchases.Count > 32 || shopPurchases.Count > 64 || activities.Count > 64)
        return "too many side activities, purchases or activities";

    if (s.BankBalance is decimal balance && Math.Abs(balance) >= MaxMoney)
        return "bank_balance is out of range";

    if (shopPurchases.Any(p => p.Cost is decimal cost && Math.Abs(cost) >= MaxMoney))
        return "a shop purchase cost is out of range";

    IEnumerable<string?> texts = new[] { s.TreeId, s.TreeItemId, s.AppVersion, s.Platform, s.Build }
        .Concat(iapPurchases)
        .Concat(activities)
        .Concat(shopPurchases.SelectMany(p => new[] { p.Name, p.Category, p.Subcategory }))
        .Concat(sideActivities.Append(s.MainActivity ?? new Activity(null, null, null))
            .SelectMany(a => new[] { a.Type, a.Kind, a.Name }));

    if (texts.Any(TooLong))
        return "a text field is too long";

    valid = new ValidSnapshot(id, recordedAt.ToUniversalTime(), s, sideActivities, iapPurchases, shopPurchases, activities);
    return null;
}

static bool TooLong(string? text) => text is not null && text.Length > MaxTextLength;

static string Truncate(string text) => text.Length <= MaxTextLength ? text : text[..MaxTextLength];

// Returns what is wrong with the page view, or null - and the cleaned up page view - when
// nothing is.
static string? ValidatePageView(PageView? p, out ValidPageView? valid)
{
    valid = null;

    if (p is null)
        return "null body";

    if (string.IsNullOrWhiteSpace(p.Path) || TooLong(p.Path) || !p.Path.StartsWith('/'))
        return "path is missing, too long or not absolute";

    string eventName = string.IsNullOrWhiteSpace(p.Event) ? "pageview" : p.Event;

    if (!Patterns.EventName.IsMatch(eventName))
        return "event is not a short lowercase name";

    // Only the referrer's origin is kept: the full url is someone else's structure and can
    // carry personal tokens.
    string? referrer = null;

    if (!string.IsNullOrWhiteSpace(p.Referrer))
    {
        if (!Uri.TryCreate(p.Referrer, UriKind.Absolute, out Uri? uri) || uri.Scheme is not ("http" or "https"))
            return "referrer is not an http url";

        referrer = uri.GetLeftPart(UriPartial.Authority);

        if (TooLong(referrer))
            return "referrer is too long";
    }

    if (p.Viewport is < 0 or > 20000)
        return "viewport is out of range";

    valid = new ValidPageView(eventName, p.Path, referrer, p.Viewport);
    return null;
}

// nginx passes the client's address in X-Forwarded-For; without one (a direct request on
// the LAN) the socket address is used.
static string ClientAddress(HttpRequest request)
{
    string forwarded = request.Headers["X-Forwarded-For"].ToString();

    if (!string.IsNullOrWhiteSpace(forwarded))
        return forwarded.Split(',')[0].Trim();

    return request.HttpContext.Connection.RemoteIpAddress?.ToString() ?? "";
}

// The client's address is never stored; what is stored is a hash that rotates with the utc
// day, so visitors can be counted within a day but not followed across days.
static string VisitorHash(string salt, string address, string userAgent)
{
    byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{salt}|{DateTime.UtcNow:yyyy-MM-dd}|{address}|{userAgent}"));
    return Convert.ToHexStringLower(hash);
}

static NpgsqlBatchCommand InsertCommand(ValidSnapshot v)
{
    Snapshot s = v.Source;

    NpgsqlBatchCommand command = new(Sql.Insert);

    command.Parameters.Add(new() { Value = v.Id });
    command.Parameters.Add(new() { Value = v.RecordedAt });
    command.Parameters.Add(Text(s.UserId));
    command.Parameters.Add(Text(s.GameId));
    command.Parameters.Add(new() { Value = (int)s.AgeMonths!.Value });
    command.Parameters.Add(Text(s.TreeId));
    command.Parameters.Add(Text(s.TreeItemId));
    command.Parameters.Add(Text(s.MainActivity?.Type));
    command.Parameters.Add(Text(s.MainActivity?.Kind));
    command.Parameters.Add(Text(s.MainActivity?.Name));
    command.Parameters.Add(new() { NpgsqlDbType = NpgsqlDbType.Jsonb, Value = JsonSerializer.Serialize(v.SideActivities, Sql.JsonOptions) });
    command.Parameters.Add(new() { NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Text, Value = v.IapPurchases.ToArray() });
    command.Parameters.Add(new() { Value = s.Autopilot ?? false });
    command.Parameters.Add(Text(s.AppVersion));
    command.Parameters.Add(Text(s.Platform));
    command.Parameters.Add(Text(s.Build));
    command.Parameters.Add(new() { Value = s.SchemaVersion ?? 1 });
    command.Parameters.Add(new() { NpgsqlDbType = NpgsqlDbType.Numeric, Value = (object?)s.BankBalance ?? DBNull.Value });
    command.Parameters.Add(new() { NpgsqlDbType = NpgsqlDbType.Jsonb, Value = JsonSerializer.Serialize(v.ShopPurchases, Sql.JsonOptions) });
    command.Parameters.Add(new() { NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Text, Value = v.Activities.ToArray() });

    return command;
}

static NpgsqlParameter Text(string? value) => new() { NpgsqlDbType = NpgsqlDbType.Text, Value = (object?)value ?? DBNull.Value };

// The wire format. Property names arrive in snake_case (age_months, tree_item_id, ...).
// Every field is nullable so that a missing one is a validation result, not a parse error.

record SnapshotBatch(List<Snapshot?>? Snapshots);

record Activity(string? Type, string? Kind, string? Name);

record ShopPurchase(string? Name, string? Category, string? Subcategory, decimal? Cost);

record Snapshot(
    string? Id,
    int? SchemaVersion,
    string? RecordedAt,
    string? UserId,
    string? GameId,
    long? AgeMonths,
    string? TreeId,
    string? TreeItemId,
    Activity? MainActivity,
    List<Activity?>? SideActivities,
    List<string?>? IapPurchases,
    bool? Autopilot,
    string? AppVersion,
    string? Platform,
    string? Build,
    decimal? BankBalance,
    List<ShopPurchase?>? ShopPurchases,
    List<string?>? Activities);

record ValidSnapshot(Guid Id, DateTimeOffset RecordedAt, Snapshot Source, List<Activity> SideActivities, List<string> IapPurchases,
    List<ShopPurchase> ShopPurchases, List<string> Activities);

record PageView(string? Path, string? Referrer, int? Viewport, string? Event);

record ValidPageView(string Event, string Path, string? Referrer, int? Viewport);

static class Patterns
{
    // The self-identifying crawlers and tools. A bot faking a real browser string slips
    // through this and has to be caught as an outlier in the queries instead.
    public static readonly Regex BotUserAgent = new(
        "bot|crawl|spider|scrape|slurp|headless|lighthouse|pagespeed|pingdom|uptime|monitor|preview|facebookexternalhit|embedly|curl|wget|python|java/|libwww|httpclient|okhttp|axios|go-http",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static readonly Regex EventName = new("^[a-z0-9][a-z0-9-]{0,49}$", RegexOptions.Compiled);
}

static class Sql
{
    public static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    // One row per age up. recorded_at is the device clock and can be wrong; received_at is
    // ours. The main activity is flattened into columns because it is what most questions
    // group by; side activities stay a json array of {type, kind, name}.
    public const string Schema = """
        create table if not exists age_up_snapshots (
            id                  uuid primary key,
            received_at         timestamptz not null default now(),
            recorded_at         timestamptz not null,
            user_id             text not null,
            game_id             text not null,
            age_months          integer not null,
            tree_id             text,
            tree_item_id        text,
            main_activity_type  text,
            main_activity_kind  text,
            main_activity_name  text,
            side_activities     jsonb not null default '[]',
            iap_purchases       text[] not null default '{}',
            autopilot           boolean not null default false,
            app_version         text,
            platform            text,
            build               text,
            schema_version      integer not null
        );

        -- Added after the first release; "if not exists" upgrades a table made before them.
        -- shop_purchases is a json array of {name, category, subcategory, cost}.
        alter table age_up_snapshots add column if not exists bank_balance numeric;
        alter table age_up_snapshots add column if not exists shop_purchases jsonb not null default '[]';
        alter table age_up_snapshots add column if not exists activities text[] not null default '{}';

        create index if not exists age_up_snapshots_user_idx on age_up_snapshots (user_id, recorded_at);
        create index if not exists age_up_snapshots_game_idx on age_up_snapshots (game_id, age_months);
        create index if not exists age_up_snapshots_received_idx on age_up_snapshots (received_at);

        -- One row per page view or click beacon from kurkisoft.com. visitor_hash is
        -- sha256(salt | utc day | address | user agent): it counts a visitor within one day
        -- and cannot be linked across days, and the address itself is never stored.
        create table if not exists page_views (
            id              bigint generated always as identity primary key,
            received_at     timestamptz not null default now(),
            visitor_hash    text not null,
            event           text not null,
            path            text not null,
            referrer        text,
            viewport        integer,
            user_agent      text,
            likely_bot      boolean not null default false
        );

        create index if not exists page_views_received_idx on page_views (received_at);
        create index if not exists page_views_event_idx on page_views (event, path);
        """;

    public const string Insert = """
        insert into age_up_snapshots (
            id, recorded_at, user_id, game_id, age_months, tree_id, tree_item_id,
            main_activity_type, main_activity_kind, main_activity_name,
            side_activities, iap_purchases, autopilot, app_version, platform, build, schema_version,
            bank_balance, shop_purchases, activities)
        values ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11, $12, $13, $14, $15, $16, $17, $18, $19, $20)
        on conflict (id) do nothing
        """;

    public const string InsertPageView = """
        insert into page_views (visitor_hash, event, path, referrer, viewport, user_agent, likely_bot)
        values ($1, $2, $3, $4, $5, $6, $7)
        """;
}
