using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Npgsql;
using NpgsqlTypes;

// Receives the age up snapshots the game posts and appends them to one Postgres table.
// See README.md for the wire format, configuration and example queries.

const string ApiKeyHeader = "X-Metrics-Key";
const int MaxBatchSize = 100;
const int MaxTextLength = 200;
// numeric has no limit of its own; a balance or price this large is not from the game.
const decimal MaxMoney = 1_000_000_000_000_000M;

var builder = WebApplication.CreateBuilder(args);

string connectionString = builder.Configuration.GetConnectionString("Metrics") ?? "";
string apiKey = builder.Configuration["Metrics:ApiKey"] ?? "";

if (string.IsNullOrWhiteSpace(connectionString))
    throw new InvalidOperationException("ConnectionStrings:Metrics is not set.");

if (string.IsNullOrWhiteSpace(apiKey))
    throw new InvalidOperationException("Metrics:ApiKey is not set. It must match MetricsConfig.ApiKey in the game.");

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

app.MapPost("/v1/purchases", async (HttpRequest request, PurchaseBatch batch, NpgsqlDataSource db, ILogger<Program> log, CancellationToken ct) =>
{
    if (!KeyMatches(request.Headers[ApiKeyHeader].ToString(), apiKey))
        return Results.Unauthorized();

    if (batch.Purchases is null || batch.Purchases.Count == 0)
        return Results.BadRequest(new { error = "The batch has no purchases." });

    if (batch.Purchases.Count > MaxBatchSize)
        return Results.BadRequest(new { error = $"A batch may hold at most {MaxBatchSize} purchases." });

    List<ValidPurchase> valid = new();
    int rejected = 0;

    foreach (Purchase? purchase in batch.Purchases)
    {
        string? problem = ValidatePurchase(purchase, out ValidPurchase? ok);

        if (problem is null)
        {
            valid.Add(ok!);
        }
        else
        {
            rejected++;
            log.LogWarning("Rejected purchase {Id}: {Problem}", purchase?.Id, problem);
        }
    }

    int inserted = 0;

    if (valid.Count > 0)
    {
        await using NpgsqlConnection connection = await db.OpenConnectionAsync(ct);
        await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync(ct);
        await using NpgsqlBatch insert = new(connection, transaction);

        foreach (ValidPurchase p in valid)
            insert.BatchCommands.Add(InsertPurchaseCommand(p));

        // Either unique key makes a row a duplicate: the report's own id (a resend) or the
        // store's purchase token (the same order reported twice).
        inserted = await insert.ExecuteNonQueryAsync(ct);
        await transaction.CommitAsync(ct);
    }

    foreach (ValidPurchase p in valid)
        log.LogInformation("Purchase {Product} {Price} {Currency} from {Build}", p.Source.ProductId, p.Source.Price, p.Source.Currency, p.Source.Build);

    return Results.Ok(new { accepted = inserted, duplicates = valid.Count - inserted, rejected });
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

static string? ValidatePurchase(Purchase? p, out ValidPurchase? valid)
{
    valid = null;

    if (p is null)
        return "null entry";

    if (!Guid.TryParse(p.Id, out Guid id))
        return "id is not a guid";

    if (!DateTimeOffset.TryParse(p.RecordedAt, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTimeOffset recordedAt))
        return "recorded_at is not a timestamp";

    // user_id is optional: a player with statistics off (and everyone in the EU, the UK and
    // Switzerland) reports purchases as bare sales records, with no install id, game or age.
    if (TooLong(p.UserId))
        return "user_id is too long";

    if (string.IsNullOrWhiteSpace(p.ProductId) || TooLong(p.ProductId))
        return "product_id is missing or too long";

    if (p.AgeMonths is long age && age is < 0 or > 2400)
        return "age_months is out of range";

    if (p.Price is decimal price && (price < 0 || price >= MaxMoney))
        return "price is out of range";

    // Google's purchase tokens run to a few hundred characters.
    if (p.PurchaseToken is { Length: > 2000 })
        return "purchase_token is too long";

    if (new[] { p.GameId, p.Currency, p.Store, p.OrderId, p.AppVersion, p.Platform, p.Build }.Any(TooLong))
        return "a text field is too long";

    valid = new ValidPurchase(id, recordedAt.ToUniversalTime(), p);
    return null;
}

static NpgsqlBatchCommand InsertPurchaseCommand(ValidPurchase v)
{
    Purchase p = v.Source;

    NpgsqlBatchCommand command = new(Sql.InsertPurchase);

    command.Parameters.Add(new() { Value = v.Id });
    command.Parameters.Add(new() { Value = v.RecordedAt });
    command.Parameters.Add(Text(string.IsNullOrWhiteSpace(p.UserId) ? null : p.UserId));
    command.Parameters.Add(Text(p.GameId));
    command.Parameters.Add(new() { NpgsqlDbType = NpgsqlDbType.Integer, Value = p.AgeMonths is long age ? (int)age : DBNull.Value });
    command.Parameters.Add(Text(p.ProductId));
    command.Parameters.Add(new() { NpgsqlDbType = NpgsqlDbType.Numeric, Value = (object?)p.Price ?? DBNull.Value });
    command.Parameters.Add(Text(p.Currency));
    command.Parameters.Add(Text(p.Store));
    command.Parameters.Add(Text(string.IsNullOrWhiteSpace(p.OrderId) ? null : p.OrderId));
    command.Parameters.Add(Text(string.IsNullOrWhiteSpace(p.PurchaseToken) ? null : p.PurchaseToken));
    command.Parameters.Add(Text(p.AppVersion));
    command.Parameters.Add(Text(p.Platform));
    command.Parameters.Add(Text(p.Build));
    command.Parameters.Add(new() { Value = p.SchemaVersion ?? 1 });

    return command;
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

record PurchaseBatch(List<Purchase?>? Purchases);

record Purchase(
    string? Id,
    int? SchemaVersion,
    string? RecordedAt,
    string? UserId,
    string? GameId,
    long? AgeMonths,
    string? ProductId,
    decimal? Price,
    string? Currency,
    string? Store,
    string? OrderId,
    string? PurchaseToken,
    string? AppVersion,
    string? Platform,
    string? Build);

record ValidPurchase(Guid Id, DateTimeOffset RecordedAt, Purchase Source);

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

        -- One row per in-app purchase the store confirmed, posted as it happens. price is in
        -- currency, as the store showed it to the player (before Google's cut and taxes).
        create table if not exists iap_purchases (
            id                  uuid primary key,
            received_at         timestamptz not null default now(),
            recorded_at         timestamptz not null,
            user_id             text,
            game_id             text,
            age_months          integer,
            product_id          text not null,
            price               numeric,
            currency            text,
            store               text,
            order_id            text,
            purchase_token      text,
            app_version         text,
            platform            text,
            build               text,
            schema_version      integer not null
        );

        -- Sales records without statistics carry no install id. A table made by the first
        -- release of this endpoint required one.
        alter table iap_purchases alter column user_id drop not null;

        create unique index if not exists iap_purchases_token_idx on iap_purchases (purchase_token) where purchase_token is not null;
        create index if not exists iap_purchases_user_idx on iap_purchases (user_id, recorded_at);
        create index if not exists iap_purchases_received_idx on iap_purchases (received_at);
        """;

    public const string InsertPurchase = """
        insert into iap_purchases (
            id, recorded_at, user_id, game_id, age_months, product_id, price, currency, store,
            order_id, purchase_token, app_version, platform, build, schema_version)
        values ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11, $12, $13, $14, $15)
        on conflict do nothing
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
}
