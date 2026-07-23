# Ticketmaster Clone

A learning project: implementing a Ticketmaster-style ticket booking platform to learn **.NET and system design**, built by a developer with strong JS/TS/React/Node background but new to .NET.

**Learning mode**: explain .NET/C# concepts and system design tradeoffs as we go (analogies to Node/Express are welcome). Don't just hand over finished code without walking through the reasoning — the point of this project is the learning, not just the artifact.

Reference spec: [HelloInterview Ticketmaster breakdown](https://www.hellointerview.com/learn/system-design/problem-breakdowns/ticketmaster) — functional/non-functional requirements, data model, API design, and the deep dives (booking concurrency, real-time seat updates, search) all come from here unless we deviate deliberately.

## Tech stack

- **.NET 10** — backend, ASP.NET Core Web API
- **PostgreSQL** — primary datastore
- **Redis** — distributed locks (ticket reservations with TTL), caching, virtual waiting-queue (sorted sets)
- **Elasticsearch** — full-text event search, kept in sync with Postgres via CDC (logical replication)
- **Stripe** — payment processing
- **React** — frontend, built later once the API works, mainly to exercise it

## Architecture

Microservices (chosen deliberately for system-design learning value, even though it's more upfront plumbing than a monolith):

- **API Gateway** — routing, rate limiting; routes to Auth Service too now
- **Auth Service** — user registration/login, issues JWTs
- **Event Service** — event/venue/performer read model
- **Booking Service** — ticket reservation + purchase flow, Redis distributed locks, Stripe integration, JWT-authenticated
- **Search Service** — event search, backed by Elasticsearch (see Event Service structure below); not a separate deployable

**Data ownership**: one shared PostgreSQL database/schema across all services for now (matches the reference article, keeps focus on .NET + booking concurrency logic first). Database-per-service is a deliberate future refactor, not the starting point — don't split it unprompted.

## Core entities & data model

As implemented in `src/EventService/Models/` (EF Core entity classes, migrated into real Postgres tables):

- **Event**: `Id, Name, Description?, Date, VenueId, PerformerId` + nav `Venue`, `Performer`, `Tickets[]`
- **Venue**: `Id, Name, Address, Capacity, SeatMap?` (raw JSON string for now, not modeled as C# classes — only needed if/when a seat-map UI has to traverse its internal structure)
- **Performer**: `Id, Name, Type`
- **Ticket**: `Id, EventId, Seat, Price, Status (Available|Booked enum), UserId?`

**User**: `Id, Email, PasswordHash` — lives in `src/AuthService/Models/` (its own table, own migration history). No other service has a `Users` table; a JWT's `sub` claim (the user's `Id`) is all any other service needs, since JWTs are self-contained and verifiable without a database lookup.

`Booking`/`BookingTicket` entities live in `src/BookingService/Models/` instead — see Booking Service structure below. Booking Service also keeps its own minimal `Ticket`/`TicketStatus` (duplicated from Event Service's, not shared — separate deployable services, deliberately no compile-time coupling between them).

Booking/reservation concurrency uses a Redis key `ticket-lock:{ticketId}` → `userId`, with a 10-minute TTL (`SET key value NX EX 600`) — this is the actual mechanism for Deep Dive 1 (preventing double-booking), not a DB-level lock or cron cleanup job. `Ticket.Status` in Postgres flips to `Booked` (and `UserId` gets set) only once Stripe confirms payment via webhook — the reserve step never touches either column, reservation state lives entirely in Redis until then.

## Build order

1. **Event Service** — done: `GET /events/{id}` returns `Event+Venue+Performer+Tickets` from real Postgres data via EF Core, dev-seeded on startup. `GET /events/search` supports `keyword`, `start`/`end` date range, and `page`/`pageSize` pagination — now backed by **Elasticsearch** (kept in sync via CDC, see below) instead of Postgres `ILIKE`, and `keyword` matches across name/description **and** venue/performer name (an improvement `ILIKE` never covered). Both endpoints use response DTOs (`Dtos/`) rather than returning entities directly — avoids leaking EF Core's circular navigation properties into the JSON.
2. **Booking Service** — done, including Deep Dive 3's virtual waiting queue and real JWT auth: `POST /bookings/reserve` (Redis TTL lock prevents double-booking, sequential-acquire-with-rollback for multi-ticket requests, DB checked as a second layer after locks succeed, gated on queue admission for events that have one enabled), `POST /bookings/{id}/pay` (creates+confirms a Stripe PaymentIntent using the test card token `pm_card_visa`, since there's no frontend yet to collect real card details; also checks the caller owns the booking), the `POST /webhooks/stripe` webhook (the actual source of truth — verifies the Stripe signature, flips `Ticket.Status`/`Booking.Status`, releases the Redis lock, idempotent via `Booking.Status` check, deliberately **not** behind `[Authorize]` since Stripe calls it directly with no JWT), and the queue itself (`POST /queue/{eventId}/enable|join`, `GET /queue/{eventId}/status`, a `BackgroundService` admitting the front of the queue periodically). `UserId` no longer travels in any request body/query string anywhere — every mutating endpoint requires a valid JWT and derives the caller's identity from its `sub` claim. All verified end-to-end against real Stripe test-mode API calls, real timed admission cycles, and real issued/validated/tampered JWTs, not just written.
3. **Search Service** — deliberately not split out as its own service, even now that Elasticsearch + CDC are in. `GET /events/search` lives inside Event Service instead (see below) — a known deviation from the original plan, kept simple since there's no independent-scaling need yet to justify a separate deployable. Revisit if Search's traffic/scaling profile ever genuinely diverges from Event Service's.
4. **API Gateway** — routing done via YARP, verified end-to-end (all five route groups proxy correctly to the right service). Rate limiting done too — global, per-client-IP fixed-window limiter (`Microsoft.AspNetCore.RateLimiting`, built into ASP.NET Core since .NET 7, no extra package), `429` + `Retry-After` on rejection, verified end-to-end including the window actually resetting. Active health checks done too — YARP polls each backend's `/health` every 5s, verified via real logs (probing, `Healthy`/`Unhealthy` state transitions all observed firing correctly); see caveat below about single-destination clusters.
5. **Auth Service** — done: `POST /auth/register` (hashes via `PasswordHasher<T>`, unique index on `Email` guarantees no duplicate accounts even under concurrent registration attempts), `POST /auth/login` (verifies, issues a JWT signed with **RSA (RS256)** — asymmetric, not a shared secret; see below). Event Service stays fully public/unauthenticated (matches the spec's "prioritize availability for searching & viewing events") — only Booking Service's mutating endpoints require a token.

## Next up (not started yet)

- **API Gateway-only access** discussed but *not* implemented — in real deployments this is enforced at the network/infra layer (private subnets, security groups, Kubernetes `NetworkPolicy`/`ClusterIP`), not application code, and isn't meaningfully demonstrable on one local machine where every port is already only reachable from `localhost`. This is exactly why Booking Service validates JWTs itself rather than trusting "reached me = came through the gateway" — that per-service validation is the real safeguard regardless of network topology (defense in depth), and it's already done.
- **JWKS endpoint** (`/.well-known/jwks.json`) on Auth Service — would let validating services fetch/cache the public key automatically instead of it being manually copied into config, and enable key rotation without touching every service by hand. Not done; the public key is currently just pasted into Booking Service's `appsettings.json` directly.

Solution layout: `Ticketmaster.slnx` (root) with each service under `src/<ServiceName>` — e.g. `src/EventService`. Note: .NET 10's `dotnet new sln` now generates the newer XML-based `.slnx` format by default instead of the classic `.sln`.

### Event Service structure

```
src/EventService/
  Controllers/EventsController.cs   GET /events/{id}, GET /events/search
  Models/                           EF Core entities (Event, Venue, Performer, Ticket, TicketStatus)
  Dtos/                             API response shapes (EventResponse, EventSummaryResponse + nested records)
  Data/EventDbContext.cs            DbContext, DbSets
  Data/DbSeeder.cs                  Dev-only: seeds 5 varied events if Events table is empty
  Migrations/                       EF Core migrations (InitialCreate applied), history table __EFMigrationsHistory
  Search/EventSearchDocument.cs     Denormalized Elasticsearch document shape (Event+Venue+Performer flattened)
  Search/EventSearchSyncService.cs  BackgroundService: CDC from Postgres -> the "events" Elasticsearch index
```
`Program.cs`: registers `EventDbContext` (Npgsql/Postgres) via DI; in Development, auto-runs `Database.MigrateAsync()` + `DbSeeder.SeedAsync()` on startup (not something a real prod deployment would do — see comment in the file). Also registers `ElasticsearchClient` (singleton, same reasoning as Booking Service's Redis/Stripe clients) and `EventSearchSyncService` via `AddHostedService`.

**Search, via Elasticsearch + CDC**: `GET /events/search` queries Elasticsearch instead of Postgres. Getting here needed two real pieces of new infrastructure, both free/local (`docker-compose.yml`):
- **Elasticsearch** (`docker.elastic.co/elasticsearch/elasticsearch:8.15.0`) — single-node, `xpack.security.enabled=false` (no auth/TLS, fine for local-only; never do this in a real deployment), `9200:9200`. Client pinned to the matching `Elastic.Clients.Elasticsearch` 8.15.10 (not the newer 9.x the SDK installs by default) to avoid a needless client/server version-mismatch risk, same reasoning as the Stripe API-version gotcha.
- **Postgres logical replication** — `wal_level=logical` set via a `command:` override on the `postgres` service (default is `replica`, which only supports whole-cluster physical replication, not row-level CDC). This is what actually enables Postgres to stream row-level WAL changes out.

**`EventSearchSyncService`** (a `BackgroundService`, same shape as Booking Service's `QueueAdmissionService`) does three things on startup, in order:
1. **Ensures a Postgres publication and replication slot exist** (`event_search_publication` / `event_search_slot`) — checked via plain SQL against `pg_publication`/`pg_replication_slots` over a regular `NpgsqlConnection`, since the replication-protocol connection used for streaming can only run a handful of dedicated replication commands, not arbitrary SQL.
2. **Backfills**: reads every existing `Event` (joined with `Venue`+`Performer` via EF Core) and indexes it into Elasticsearch. CDC only captures changes from the moment the slot is created onward — data that already existed needs this one-time catch-up pass. Runs on every startup, not just the first; a plain `IndexAsync` (index-by-id) is naturally idempotent, so re-running it against already-current documents is harmless.
3. **Tails the replication stream forever** (`Npgsql.Replication`, `pgoutput` plugin — no extra NuGet package needed, this ships in the base `Npgsql` package already referenced transitively via `Npgsql.EntityFrameworkCore.PostgreSQL`): on insert/update, reads only the changed row's `Id` off the wire, then **re-reads the full Event+Venue+Performer join from Postgres** and upserts that denormalized document into Elasticsearch; on delete, removes the document by id. Reconstructing the join purely from WAL deltas across three separate tables would be far more complex for no real benefit at this scale — re-fetching is simpler and the source of truth (Postgres) is always one query away.

**Document shape** (`EventSearchDocument`): deliberately denormalized — `VenueName`/`VenueAddress`/`VenueCapacity`/`PerformerName`/`PerformerType` flattened directly onto the event document, since Elasticsearch has no concept of a SQL join. The document id is the Event's own `Id`, so re-indexing is always a plain upsert. The index itself is never explicitly created — Elasticsearch auto-creates it with dynamic mapping on the first document indexed (during backfill), which is enough at this scale; a real deployment would want an explicit mapping (to control analyzers, avoid unnecessary keyword sub-fields, guarantee `Date` is recognized as `date` and not `text`) rather than relying on dynamic date detection.

**Query shape**: `keyword` becomes a `bool` query with `should` clauses (`match`) against `name`, `description`, `venueName`, and `performerName` (`MinimumShouldMatch(1)`) — actual full-text matching via Elasticsearch's inverted index, not a substring scan. `start`/`end` become a `filter` clause (`range`/`date_range`) — filters don't contribute to relevance scoring and are cacheable, which is the correct clause type for a hard date boundary. Sorted by `date`, paginated via `from`/`size`.

- **Gotcha hit and fixed**: reading a replicated row's `Id` column via `ReplicationValue.Get<Guid>(ct)` threw `System.InvalidCastException: Reading as 'System.Guid' is not supported for fields having DataTypeName 'text'` — `pgoutput` streams column values in text format by default, so Npgsql reports the field's type as `text` at this layer regardless of the column's real Postgres type (`uuid`). Fixed by reading as `string` via `Get<string>(ct)` and parsing with `Guid.Parse(...)` instead, which works regardless of wire format.
- Verified end-to-end, not just written: publication/slot auto-created on first run and correctly reused (not re-created) on subsequent restarts; initial backfill of all seeded events confirmed via direct Elasticsearch queries; `GET /events/search` returning real Elasticsearch-backed results (including a keyword match against a venue name, which the old `ILIKE` version could never do); and a real insert, update, and delete performed directly against Postgres via `psql` — each one observed propagating into Elasticsearch within ~2 seconds with the app already running, no restart needed.
- **Known limitation, deliberate for now**: the whole pipeline runs inside Event Service's own process as a `BackgroundService`, sharing its lifecycle — if Event Service is down, indexing stops (though search reads still work fine off the last-synced Elasticsearch state). A real deployment would likely run this as its own worker process so search indexing survives Event Service restarts/deploys independently.

### Booking Service structure

```
src/BookingService/
  Controllers/BookingsController.cs       POST /bookings/reserve, POST /bookings/{id}/pay
  Controllers/StripeWebhookController.cs  POST /webhooks/stripe — the real source of truth for confirming a booking
  Controllers/QueueController.cs          POST /queue/{eventId}/enable, POST /queue/{eventId}/join, GET /queue/{eventId}/status
  QueueAdmissionService.cs                BackgroundService — periodically admits the front of each active queue, independent of any request
  Models/                                 Booking, BookingTicket (owned), Ticket/TicketStatus (read-only view of Event Service's table)
  Extensions/ClaimsPrincipalExtensions.cs GetUserId() — reads the sub claim off the validated JWT, shared by BookingsController + QueueController
  Dtos/                                   ReserveBookingRequest/Response, PayBookingResponse, JoinQueueResponse, QueueStatusResponse
  Data/BookingDbContext.cs                DbContext; Ticket is .ExcludeFromMigrations() since Event Service owns that table's schema
  Migrations/                             EF Core migrations, own history table __BookingServiceMigrationsHistory (kept separate from Event Service's, since both target the same physical database)
```
`Program.cs`: registers `BookingDbContext` (Npgsql/Postgres), `IConnectionMultiplexer` (StackExchange.Redis, **singleton** — not scoped like `DbContext` — since the Redis connection is meant to be shared for the app's whole lifetime, not reopened per request), `IStripeClient` (also singleton, same reasoning), JWT Bearer authentication (`AddAuthentication().AddJwtBearer(...)`, validated against Auth Service's **public key** — see Auth Service structure below), and `QueueAdmissionService` (via `AddHostedService`, not `AddSingleton` — that's what actually makes the host start/stop it) via DI; auto-migrates on startup in Development (no dev-seed needed — reads `Ticket` rows that Event Service already seeded in the shared database).

**Auth**: `[Authorize]` on both `BookingsController` and `QueueController` (class-level) — `StripeWebhookController` deliberately has none, since Stripe calls it directly with no JWT. `Reserve`/`Join`/`Status` derive the caller's `userId` from `User.GetUserId()` instead of accepting it as input; `Pay` additionally checks `booking.UserId == User.GetUserId()` before allowing payment — being authenticated only proves *who* you are, not that you're allowed to act on *this specific* booking, so without that ownership check anyone with a valid token could pay for any booking just by knowing its id.
- **Gotcha hit and fixed**: `FindFirstValue(JwtRegisteredClaimNames.Sub)` returned `null` even though the JWT genuinely had a `sub` claim — ASP.NET Core's JWT Bearer handler remaps standard short claim names to legacy long-form URIs by default (a holdover from WS-Federation-era claims systems), so there's no claim literally named `"sub"` after validation unless you opt out. Fixed via `options.MapInboundClaims = false;` on `JwtBearerOptions`, which keeps claim types exactly as issued.

**Virtual waiting queue** (Deep Dive 3), Redis-only, no new Postgres tables:
- `queue:{eventId}` — sorted set, member = userId, score = join timestamp (earliest = lowest score = front of line)
- `active-queues` — plain set of eventIds with a non-empty queue, so the background admission loop never has to scan Postgres/Redis for "which events have people waiting"
- `queue-enabled-events` — plain set of eventIds with the queue actually turned on via `POST /queue/{eventId}/enable`; most events never call this and `Reserve` skips the admission check entirely for them (stands in for the spec's "admin-enabled" framing — no real admin/auth system exists yet to gate this behind)
- `admitted:{eventId}:{userId}` — string key with a 10-minute TTL (same window as the ticket lock, for consistency, otherwise unrelated), set by `QueueAdmissionService` when it pops that user off the queue. Not deleted on a successful reservation — admission is a *time window* to make attempts in, not a single-use token
- `QueueAdmissionService.ExecuteAsync` loop (interval/batch size configurable via `Queue:AdmissionIntervalSeconds`/`Queue:AdmissionBatchSize`) uses `SortedSetPopAsync` (Redis `ZPOPMIN` with a count) to atomically admit a batch at a time
- Deliberately **not built**: SSE/WebSocket push for queue position (Deep Dive 3's separate "Good Solution" for live seat updates) — clients would poll `GET /queue/{eventId}/status` instead. Same idea, just polling instead of a push channel.

**Stripe setup** (test mode):
- Secret key + webhook signing secret are stored via **.NET User Secrets** (`dotnet user-secrets set "Stripe:SecretKey" "sk_test_..."` / `"Stripe:WebhookSecret" "whsec_..."`, run from `src/BookingService/`) — never in `appsettings.*.json`, never committed. Requires `dotnet user-secrets init` once per project (already done — adds a `UserSecretsId` GUID to `BookingService.csproj`, which is just a pointer, not sensitive itself).
- Local webhook delivery needs the **Stripe CLI** forwarding events to the running service, since Stripe's servers can't reach `localhost` directly: `stripe listen --forward-to localhost:5290/webhooks/stripe` (run from anywhere, needs `stripe login` once). It prints a webhook signing secret on each run — usually stable across restarts, but if webhook signature verification starts failing, re-check it matches the stored `Stripe:WebhookSecret`.
- **Gotcha hit and fixed**: `PaymentIntentService` has both a parameterless constructor and one taking `IStripeClient`. Registering the Stripe client as `AddSingleton(new StripeClient(...))` (concrete type) silently left DI unable to satisfy the `IStripeClient` constructor, so it fell back to the parameterless one — resulting in a client with no API key, only failing at actual request time with a confusing "No API key provided" error. Fix: register as `AddSingleton<IStripeClient>(...)`, matching the interface the DI-resolved constructor actually asks for.
- **Gotcha hit and fixed**: webhook signature verification failed with what looked like an invalid-signature error, but the real cause (only visible in `StripeException.Message`) was an **API version mismatch** — this Stripe account's default API version differs from what the installed `Stripe.net` package expects. Fixed by passing `throwOnApiVersionMismatch: false` to `EventUtility.ConstructEvent` — safe here since the webhook only reads a few basic, stable `PaymentIntent` fields (`Metadata`, `Id`, `Status`), not something likely to have changed shape between versions.
- Testing without a frontend: `POST /bookings/{id}/pay` creates *and* confirms the PaymentIntent server-side using Stripe's dedicated always-succeeds test token `pm_card_visa` — this is what a real frontend's Stripe.js/Elements flow would otherwise do client-side.

### API Gateway structure

```
src/ApiGateway/
  Program.cs          AddReverseProxy().LoadFromConfig(...) + MapReverseProxy() — no controllers, no EF Core, nothing of its own
  appsettings.json     ReverseProxy:Routes / ReverseProxy:Clusters — the actual routing table
```
Built on **YARP** (`Yarp.ReverseProxy`, Microsoft's own open-source .NET reverse proxy), not AWS API Gateway or any cloud service — stays consistent with this project's local-only/zero-cost constraint. Scaffolded from the `web` (Empty) template rather than `webapi`, since a pure reverse proxy has no business logic of its own — notably no `Microsoft.OpenApi` dependency at all, so no version-pinning gotcha to deal with this time.

**Rate limiting**: `Microsoft.AspNetCore.RateLimiting` (built into ASP.NET Core, no NuGet package needed), configured directly in `Program.cs` — a global `PartitionedRateLimiter`, partitioned per client IP (no real auth yet to key on anything more meaningful), using the Fixed Window algorithm (`RateLimitPartition.GetFixedWindowLimiter`, `QueueLimit: 0` — reject immediately rather than queueing excess requests). Limit/window tunable via `RateLimiting:PermitLimit`/`RateLimiting:WindowSeconds` (code fallback defaults 100/60s; Development overrides to 5/10s for easy manual testing). Rejects with `429` + a `Retry-After` header via `options.OnRejected`. `app.UseRateLimiter()` must run before `app.MapReverseProxy()` in the pipeline, or requests would already be forwarded before ever being checked.

Route table: `/events/**` → Event Service (`localhost:5049`), `/bookings/**` + `/queue/**` + `/webhooks/**` → Booking Service (`localhost:5290`), `/auth/**` → Auth Service (`localhost:5003`), all via one shared cluster per backend service. Listens on `http://localhost:5269`. `{**catch-all}` in each route's `Match.Path` is YARP's catch-all route parameter — swallows everything after the prefix, so e.g. both `/events/search?keyword=x` and `/events/<guid>` match the same route.

**Active health checks**: each cluster's config (`event-service`, `booking-service`, `auth-service` in `appsettings.json`) has a `HealthCheck.Active` block (`Enabled: true`, `Interval: 5s`, `Timeout: 3s`, `Policy: ConsecutiveFailures`, `Path: /health`) plus a `Metadata` block setting `ConsecutiveFailuresHealthPolicy.Threshold: 2`. All three backend services (`Event`, `Booking`, `Auth`) gained a bare `builder.Services.AddHealthChecks()` + `app.MapHealthChecks("/health")` in their own `Program.cs` for this to probe against — anonymous, no `[Authorize]`, same reasoning as the Stripe webhook endpoint. No `.AddActiveHealthChecks()` builder call needed on the Gateway side in the installed `Yarp.ReverseProxy` version — `AddReverseProxy()` registers the active health check monitor unconditionally, and the per-cluster config just turns it on; confirmed via real startup logs showing YARP probing all three destinations every 5 seconds and logging `Healthy`/`Unhealthy` state transitions.
- **Known limitation, deliberate for now**: every cluster has exactly **one** destination. Marking that single destination unhealthy gives YARP nowhere else to route to — so a request during an outage still fails (a `502` from the very first request that hits the dead destination, before the health monitor even notices; unclear if it improves after the `Unhealthy` transition given YARP's documented fallback to treat all destinations as available again when none are healthy). The actual client-facing payoff of active health checks — silently rerouting traffic away from a bad instance with zero visible failure — only shows up once a cluster has 2+ destinations (e.g. multiple replicas of a service behind one cluster entry). Kept anyway since it's correctly wired, costs nothing at runtime, and demonstrates the real mechanism a production load balancer uses; revisit if a service ever gets a second instance.

### Auth Service structure

```
src/AuthService/
  Controllers/AuthController.cs   POST /auth/register, POST /auth/login — both anonymous, no [Authorize] here
  Models/User.cs                  Id, Email, PasswordHash
  Data/AuthDbContext.cs           DbContext; unique index on Email
  Dtos/AuthDtos.cs                RegisterRequest/Response, LoginRequest/Response
  Migrations/                     EF Core migrations, own history table __AuthServiceMigrationsHistory
```
`Program.cs`: registers `AuthDbContext` (Npgsql/Postgres) and `IPasswordHasher<User>` (`PasswordHasher<User>`, **singleton** — stateless, thread-safe, no per-request state needed) via DI; auto-migrates on startup in Development. `PasswordHasher<T>`/`IPasswordHasher<T>` need no NuGet package at all — already part of the ASP.NET Core shared framework, same story as `Microsoft.AspNetCore.RateLimiting`.

**JWT issuance**: `POST /auth/login` verifies via `PasswordHasher<T>.VerifyHashedPassword` (treats `Success` and `SuccessRehashNeeded` both as success — a known simplification, a fully hardened version would re-hash and persist on `SuccessRehashNeeded`) and, if valid, signs a JWT with `System.IdentityModel.Tokens.Jwt` — **RSA (RS256), asymmetric, not a shared secret**. Claims: `sub` (the user's `Id`) and `email`, both standard JWT-registered claim names. 1-hour expiry. `ValidateIssuer`/`ValidateAudience` both disabled on the validating side (Booking Service) — a known simplification, since we never set `iss`/`aud` claims when issuing.

**Asymmetric key storage** — the actual point of switching off symmetric HMAC: only Auth Service ever holds the private key, so only it can ever sign a token; every other service holds just the public key, which can only validate, never forge.
- Key pair generated once via `openssl genrsa -out private.pem 2048` + `openssl rsa -in private.pem -pubout -out public.pem` (standard PKCS#8/X.509 PEM, no custom format).
- **Private key** → Auth Service's **.NET User Secrets** only (`Auth:JwtPrivateKey`, full PEM text as the value) — never committed, never leaves that one project. Loaded once at startup into a singleton `SigningCredentials` (`RSA.Create()` + `rsa.ImportFromPem(...)` + `new RsaSecurityKey(rsa)` + `SecurityAlgorithms.RsaSha256`), injected into `AuthController` rather than re-parsed on every login call.
- **Public key** → Booking Service's **committed `appsettings.json`** (`Auth:JwtPublicKey`), in plain sight, on purpose — a public key is safe to publish widely by definition, so it doesn't belong in a secrets store at all; putting it there would suggest it needs the same protection as the private key, which it structurally does not. Loaded the same way (`RSA.Create()` + `ImportFromPem(...)` + `RsaSecurityKey`), but since only the public key material was ever imported, that `RSA` instance is *structurally* incapable of signing anything — not a policy choice, a mathematical one.
- No new NuGet packages needed for any of this — `System.Security.Cryptography.RSA` is part of the .NET base class library, and `RsaSecurityKey`/`SigningCredentials` were already available transitively via packages both services already referenced.

Login returns distinct messages for "no account with this email" vs. "incorrect password" — a deliberate choice for this project (real systems often return the same message for both, so a caller can't enumerate registered emails by trying logins; not a concern worth the UX cost here). Unique index on `Users.Email` (not just an application-level existence check) is what actually prevents a duplicate account if two registrations for the same email land at the same instant — same reasoning as the Redis TTL lock preventing double-booking, just a plain relational constraint instead of Redis, since this is a single-database concern.

## Running the app locally

The API is not a persistent service — it's only reachable while a `dotnet run` process is alive. Every time: start Postgres + Redis + Elasticsearch first, then whichever service(s) you're working on.

```bash
# 1. Postgres + Redis + Elasticsearch (from repo root — only needed once per reboot/container restart, stays up after)
docker compose up -d postgres redis elasticsearch

# 2. Restore EF Core migration tooling (only needed once per clone, or after dotnet-tools.json changes)
dotnet tool restore

# 3. Run a service (from its own folder, e.g. src/EventService/, src/BookingService/, src/ApiGateway/, or src/AuthService/)
cd src/EventService   # or src/BookingService, src/ApiGateway, or src/AuthService
dotnet run
```
Event Service listens on `http://localhost:5049`, Booking Service on `http://localhost:5290`, API Gateway on `http://localhost:5269`, Auth Service on `http://localhost:5003` (all from their own `Properties/launchSettings.json` — auto-assigned per project, no collision). Migrations apply automatically on startup in Development for Event, Booking, and Auth Service — no manual `dotnet ef database update` needed day to day (that command is still how the *first* migration for a new schema change gets generated: `dotnet ef migrations add <Name>`, run from the service's own folder). The Gateway needs the other three already running to actually proxy anywhere — it has no database/migrations of its own. To use Booking Service's protected endpoints, Auth Service needs to be running too, to register/log in and get a token in the first place. Event Service additionally needs Elasticsearch up (and Postgres's `wal_level=logical`, already baked into `docker-compose.yml`) for `EventSearchSyncService` to start cleanly and for `GET /events/search` to return anything.

Leave the terminal(s) running while testing; `Ctrl+C` to stop. VS Code's Run/Debug panel (`F5`) does the same thing with a debugger attached — pick which project to launch if prompted, since there's now more than one.

**Testing the running API**: each service has its own `.http` file (`src/EventService/EventService.http`, `src/BookingService/BookingService.http`, `src/ApiGateway/ApiGateway.http`, `src/AuthService/AuthService.http`) runnable via VS Code's **REST Client** extension (click "Send Request" above a request, or place cursor in one and hit `Cmd+Alt+R`) — or plain `curl`. Once the Gateway's running, prefer hitting *it* (`5269`) over the individual services directly, to actually exercise the routing. Booking Service's `.http` file has `@token`/`@token2` variables at the top — register + log in via `AuthService.http` first and paste real tokens in before its protected requests will work.

**Testing Booking Service's payment flow specifically** additionally needs `stripe listen --forward-to localhost:5290/webhooks/stripe` running in its own terminal (forwards Stripe's webhook events to local — Stripe's servers can't reach `localhost` directly). Without it, `POST /bookings/{id}/pay` still succeeds (Stripe itself processes the payment), but the booking never actually gets confirmed in our own database, since that only happens via the webhook.

## Deployment

Local only, no cloud, to keep cost at zero. Postgres/Redis run via Docker Compose once Docker is available locally (fallback: Homebrew services if Docker setup is deferred). Revisit cloud deployment only if explicitly asked.

## Environment status

- .NET 10 SDK: working (`dotnet --version` → 10.0.302)
- Container runtime: **Rancher Desktop** (not Docker Desktop) — `docker --version` → 29.5.3-rd, `docker compose version` → v5.1.4. Fully drop-in compatible with `docker`/`docker compose`; nothing in `docker-compose.yml` or any command in this file needed to change.

Setup note if this ever needs reinstalling: `brew install --cask rancher` (no sudo needed — it's a plain `.app`). On first launch, the setup wizard **must** select **"dockerd (moby)"** as the container engine (not "containerd") — that's what provides the compatible `docker` CLI; the other option only gives you `nerdctl`. Kubernetes stays disabled, not needed here. Its CLI tools install to `~/.rd/bin`, added to shell rc files automatically — only picked up by *new* terminal sessions, not ones already open at install time. `docker compose` (the plugin form) needs a fresh terminal to resolve too; `docker-compose` (hyphenated, older standalone binary) works immediately in any shell as a fallback.

**Known quirk**: Rancher Desktop's backend VM can end up stopped (`rdctl api /v1/backend_state` shows `"vmState":"DISABLED"`) while the app window/process is still open — `docker`/`docker compose` then fail with a "daemon not running" style error even though the app looks fine. Fix: `rdctl shutdown` (fully quits it) then relaunch the app fresh; `rdctl start` alone won't restart an already-stopped backend unless you actually change a setting.
