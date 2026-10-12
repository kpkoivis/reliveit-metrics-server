# Reliveit metrics server

Receives the snapshot the game posts each time a player ages up, and the report it posts
each time the store confirms an in-app purchase, and appends them to two Postgres tables,
`age_up_snapshots` and `iap_purchases`. That is all it does.

The game side lives in the `reliveit` repo under `Assets/Scripts/Metrics/`; this repo is only
the server, so the Linux box needs nothing from the game.

## Deploy on Ubuntu behind nginx

The server is one self-contained Linux binary run by systemd on 192.168.11.155, next to a
Postgres from apt. Nothing .NET needs installing there. The nginx on the public host proxies
`https://data.kurkisoft.com` to it; https is required because Android blocks plain `http`,
and so does the Web build served from kurkisoft.com.

**1. Build the binary** on a machine with the .NET 10 SDK (your dev machine, or the server itself
after `apt install dotnet-sdk-10.0`, which on Ubuntu 24.04 comes from the backports or
Microsoft package feed), from a clone of this repo:

```sh
dotnet publish -c Release -r linux-x64 --self-contained true -p:PublishSingleFile=true -o publish
```

Use `linux-arm64` instead if `uname -m` on the server says `aarch64`.

**2. Set up Postgres** on the server:

```sh
sudo apt install postgresql
sudo -u postgres psql -c "create role metrics login password 'CHOOSE-A-PASSWORD'"
sudo -u postgres psql -c "create database metrics owner metrics"
```

**3. Install the server.** Copy `publish/MetricsServer`, `publish/appsettings.json` and the
`deploy/` folder to the server, then:

```sh
sudo useradd --system --no-create-home --shell /usr/sbin/nologin reliveit-metrics
sudo install -d /opt/reliveit-metrics
sudo install -m 755 MetricsServer /opt/reliveit-metrics/
sudo install -m 644 appsettings.json /opt/reliveit-metrics/

sudo install -m 600 deploy/reliveit-metrics.env.example /etc/reliveit-metrics.env
sudo nano /etc/reliveit-metrics.env        # the database password and the API key

sudo install -m 644 deploy/reliveit-metrics.service /etc/systemd/system/
sudo systemctl daemon-reload
sudo systemctl enable --now reliveit-metrics

curl http://192.168.11.155:3020/healthz   # {"status":"ok"}
```

The table and its indexes are created on startup if they are missing. `journalctl -u
reliveit-metrics` shows one line per stored batch and one per rejected snapshot, with the
reason; a 401 from a wrong key is not logged, the game just keeps retrying.

**4. Route it through nginx** on the host that serves kurkisoft.com. `deploy/nginx.conf` is
the `data.kurkisoft.com` server block plus the rate limit line, with the steps to install it
and have certbot add https. Once that is done:

```sh
curl -i https://data.kurkisoft.com/v1/snapshots -X POST -H "Content-Type: application/json" -d '{}'
```

That last request should answer `HTTP/1.1 401` with an empty body: it reached the server and was refused for having no
key.

**5. Point the game at it:**

```csharp
// reliveit repo: Assets/Scripts/Metrics/MetricsConfig.cs
public static readonly string EndpointUrl = "https://data.kurkisoft.com/v1/snapshots";
public static readonly string ApiKey = "the Metrics__ApiKey value";
```

While `EndpointUrl` is empty the game records and sends nothing.

**Updating** is copying a new binary over `/opt/reliveit-metrics/MetricsServer` and
`sudo systemctl restart reliveit-metrics`. The game queues snapshots while the server is
down and sends them once it is back.

### With Docker instead

`docker-compose.yml` runs the server and Postgres together. Change its `ports` line to
`192.168.11.155:3020:8080` so nginx can reach it, then:

```sh
cp .env.example .env      # set POSTGRES_PASSWORD and METRICS_API_KEY
docker compose up -d --build
```

The nginx step is the same.

### Locally

```sh
ConnectionStrings__Metrics="Host=localhost;Database=metrics;Username=metrics;Password=..." \
Metrics__ApiKey="..." \
dotnet run
```

## What protects it

- **The key** (`X-Metrics-Key` header). It ships inside every build of the game, so it
  keeps casual junk out and nothing more. Do not treat rows as trustworthy.
- **Size limits**: 256 KB per request, 100 snapshots per batch, 200 characters per text
  field.
- **Rate limiting is nginx's job**, where the client's real IP address is known. The server
  has none of its own. `deploy/nginx.conf` sets it up.

## API

`POST /v1/snapshots` with header `X-Metrics-Key` and a JSON body:

```json
{
  "snapshots": [
    {
      "id": "ef10ad5c-8283-44a9-ba17-25cd8a3fdde1",
      "schema_version": 1,
      "recorded_at": "2026-10-05T08:54:25.687Z",
      "user_id": "af2c9da1-b6c4-4758-aa59-9fa4fa5bcf67",
      "game_id": "k3J9xQ",
      "age_months": 222,
      "tree_id": "career_tree",
      "tree_item_id": "career_item_1",
      "main_activity": { "type": "Job", "kind": "Baker", "name": "Baker" },
      "side_activities": [ { "type": "Hobby", "kind": null, "name": "Drawing" } ],
      "iap_purchases": [ "com.kurkisoft.reliveit.iap.agedown" ],
      "bank_balance": 15230.50,
      "shop_purchases": [ { "name": "Rusty bike", "category": "SimpleTransport", "subcategory": "Bicycle", "cost": 45.00 } ],
      "activities": [ "ListenToClassicalMusicActivity", "GoForAWalkActivity" ],
      "autopilot": false,
      "app_version": "1.0.5",
      "platform": "Android",
      "build": "Pre-18"
    }
  ]
}
```

| Response | Meaning |
| --- | --- |
| `200 {"accepted": 2, "duplicates": 0, "rejected": 1}` | Stored. A snapshot that fails validation is skipped and logged, not fatal to the batch. |
| `400` | Unreadable body, empty batch, or more than 100 snapshots. The game drops the batch. |
| `401` | Missing or wrong key. The game keeps the batch and retries. |
| `413` | Body over 256 KB. |

`id` is generated by the game per snapshot. Posting the same snapshot twice stores it once,
which is what makes the game's retries safe.

`POST /v1/purchases` takes the same header, limits and responses, with a body of purchase
reports. The game posts one as soon as the store confirms a purchase, not with the next
snapshot:

```json
{
  "purchases": [
    {
      "id": "0b8e4bd8-7d55-4a0e-9c55-2bfa0e0f3f6b",
      "schema_version": 1,
      "recorded_at": "2026-10-12T09:00:00.000Z",
      "user_id": "af2c9da1-b6c4-4758-aa59-9fa4fa5bcf67",
      "game_id": "k3J9xQ",
      "age_months": 222,
      "product_id": "com.kurkisoft.reliveit.iap.noads",
      "price": 2.99,
      "currency": "EUR",
      "store": "GooglePlay",
      "order_id": "GPA.1234-5678-9012-34567",
      "purchase_token": "the token from Google",
      "app_version": "1.0.6",
      "platform": "Android",
      "build": "Production"
    }
  ]
}
```

A purchase is stored once per `id` and once per `purchase_token`, so a resend and the same
order reported twice are both counted as duplicates.

`GET /healthz` returns `200` when the database answers.

## What the columns mean

| Column | Notes |
| --- | --- |
| `user_id` | Random per install. Not tied to a device, account or person. |
| `game_id` | One life. A user has many. |
| `age_months` | The age the character aged up **from**. A turn is 6 months. The turn a player quits on is never aged up from, so it is not reported. |
| `recorded_at` / `received_at` | Device clock (can be wrong) / server clock. A turn played offline arrives later, so the two can be far apart. |
| `tree_id`, `tree_item_id` | The tree of life item being researched. Null when there is none, as in juvy. |
| `main_activity_*` | `kind` is only set for jobs, schools and lifetime activities. |
| `side_activities` | JSON array of `{type, kind, name}`. Empty slots are left out. |
| `iap_purchases` | Products bought since the previous snapshot, not everything owned. |
| `bank_balance` | In-game money at the end of the turn. Null on rows from game versions before it was added. |
| `shop_purchases` | JSON array of `{name, category, subcategory, cost}`: items bought from the in-game shop during the turn. |
| `activities` | Activities started from the activities panel during the turn, by class name, in order and with repeats. Activities offered by an owned asset are not included. |
| `autopilot` | The turn was played by autopilot, not the player. |
| `build` | `Development`, `Production`, `Stakeholder`, `Pre-18`, or a combination such as `Production, pre-18`. Filter on it. |

`iap_purchases`, one row per confirmed purchase:

| Column | Notes |
| --- | --- |
| `user_id` | The same random per-install id as in the snapshots, so the two tables join on it. Null when the player has no statistics: everyone in the EU/EEA, the UK and Switzerland, and players elsewhere who turned statistics off. Those rows are bare sales records. |
| `game_id`, `age_months` | The life being played and the character's age when the purchase was made. Null when bought outside a life, and on bare sales records. |
| `price`, `currency` | What the store showed the player, before Google's cut and taxes. Null if the game never received the product list. Prices in different currencies cannot be summed as they are. |
| `store` | `GooglePlay` on Android. Purchases from the editor's fake store carry its own name, or null. |
| `order_id` | Google's order id (`GPA.…`), the one Play Console and refunds use. Null for license-tester and editor purchases. |
| `purchase_token` | Google's key for the purchase, for checking it with the Play Developer API later. Unique when present. |
| `build` | Filter on `Production` for real revenue: development builds buy from the editor's fake store or with test accounts. |

## Example queries

Where lives end (the last turn each life reported):

```sql
select floor(last_age / 12) as age_years, count(*) as lives_ended_here
from (select game_id, max(age_months) as last_age from age_up_snapshots group by game_id) as lives
group by 1 order by 1;
```

Which tree of life items get picked:

```sql
select tree_id, tree_item_id, count(distinct game_id) as games
from age_up_snapshots
where tree_item_id is not null
group by 1, 2 order by games desc;
```

What players spend their turns on:

```sql
select main_activity_type, main_activity_kind, count(*) as turns
from age_up_snapshots
where not autopilot
group by 1, 2 order by turns desc;

select side->>'type' as type, side->>'name' as name, count(*) as turns
from age_up_snapshots, jsonb_array_elements(side_activities) as side
group by 1, 2 order by turns desc;
```

What gets bought, and at what age:

```sql
select product, count(*) as purchases, round(avg(age_months) / 12.0, 1) as avg_age_years
from age_up_snapshots, unnest(iap_purchases) as product
group by product;

select item->>'category' as category, item->>'name' as name, count(*) as bought,
       round(avg((item->>'cost')::numeric)) as avg_cost
from age_up_snapshots, jsonb_array_elements(shop_purchases) as item
group by 1, 2 order by bought desc;
```

Which activities get played, and how money grows with age:

```sql
select activity, count(*) as times_played
from age_up_snapshots, unnest(activities) as activity
where not autopilot
group by 1 order by 2 desc;

select floor(age_months / 12) as age_years, percentile_cont(0.5) within group (order by bank_balance) as median_balance
from age_up_snapshots
where bank_balance is not null
group by 1 order by 1;
```

Revenue per product and currency, and who buys:

```sql
select product_id, currency, count(*) as purchases, sum(price) as revenue
from iap_purchases
where build like 'Production%'
group by 1, 2 order by purchases desc;

select count(distinct p.user_id) as buyers,
       (select count(distinct user_id) from age_up_snapshots where build like 'Production%') as players
from iap_purchases p
where p.build like 'Production%';
```

At what age in a life, and how long into play, people buy:

```sql
select product_id, round(avg(age_months) / 12.0, 1) as avg_age_years
from iap_purchases
where age_months is not null
group by 1;

select p.product_id,
       percentile_cont(0.5) within group (order by p.recorded_at - first_seen.at) as median_time_to_purchase
from iap_purchases p
join (select user_id, min(recorded_at) as at from age_up_snapshots group by user_id) as first_seen using (user_id)
group by 1;
```

## Changing the format

Adding a field: add it to `MetricsSnapshot` (or `MetricsPurchase`) in the game, then to the
`Snapshot` (or `Purchase`) record, the table and the insert here. The server ignores fields it does not know, so the two sides can
ship in either order.

Renaming or removing a field: bump `MetricsSnapshot.CurrentSchemaVersion` and branch on
`schema_version` here, because old builds of the game keep posting the old shape.
