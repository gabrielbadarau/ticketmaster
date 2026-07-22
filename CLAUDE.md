# Ticketmaster Clone

A learning project: implementing a Ticketmaster-style ticket booking platform to learn **.NET and system design**, built by a developer with strong JS/TS/React/Node background but new to .NET.

**Learning mode**: explain .NET/C# concepts and system design tradeoffs as we go (analogies to Node/Express are welcome). Don't just hand over finished code without walking through the reasoning — the point of this project is the learning, not just the artifact.

Reference spec: [HelloInterview Ticketmaster breakdown](https://www.hellointerview.com/learn/system-design/problem-breakdowns/ticketmaster) — functional/non-functional requirements, data model, API design, and the deep dives (booking concurrency, real-time seat updates, search) all come from here unless we deviate deliberately.

## Tech stack

- **.NET 10** — backend, ASP.NET Core Web API
- **PostgreSQL** — primary datastore
- **Redis** — distributed locks (ticket reservations with TTL), caching, virtual waiting-queue (sorted sets)
- **Elasticsearch** — full-text event search (later phase)
- **Stripe** — payment processing
- **React** — frontend, built later once the API works, mainly to exercise it

## Architecture

Microservices (chosen deliberately for system-design learning value, even though it's more upfront plumbing than a monolith):

- **API Gateway** — routing, rate limiting; routes to Auth Service too now
- **Auth Service** — user registration/login, issues JWTs
- **Event Service** — event/venue/performer read model
- **Booking Service** — ticket reservation + purchase flow, Redis distributed locks, Stripe integration, JWT-authenticated
- **Search Service** — event search (Postgres full-text first, Elasticsearch later)

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

1. **Event Service** — done: `GET /events/{id}` returns `Event+Venue+Performer+Tickets` from real Postgres data via EF Core, dev-seeded on startup. `GET /events/search` supports `keyword` (ILIKE against name/description), `start`/`end` date range, and `page`/`pageSize` pagination — projects straight to `EventSummaryResponse` in the SQL query itself rather than loading full entities first. Both use response DTOs (`Dtos/`) rather than returning entities directly — avoids leaking EF Core's circular navigation properties into the JSON.
2. **Booking Service** — done, including Deep Dive 3's virtual waiting queue and real JWT auth: `POST /bookings/reserve` (Redis TTL lock prevents double-booking, sequential-acquire-with-rollback for multi-ticket requests, DB checked as a second layer after locks succeed, gated on queue admission for events that have one enabled), `POST /bookings/{id}/pay` (creates+confirms a Stripe PaymentIntent using the test card token `pm_card_visa`, since there's no frontend yet to collect real card details; also checks the caller owns the booking), the `POST /webhooks/stripe` webhook (the actual source of truth — verifies the Stripe signature, flips `Ticket.Status`/`Booking.Status`, releases the Redis lock, idempotent via `Booking.Status` check, deliberately **not** behind `[Authorize]` since Stripe calls it directly with no JWT), and the queue itself (`POST /queue/{eventId}/enable|join`, `GET /queue/{eventId}/status`, a `BackgroundService` admitting the front of the queue periodically). `UserId` no longer travels in any request body/query string anywhere — every mutating endpoint requires a valid JWT and derives the caller's identity from its `sub` claim. All verified end-to-end against real Stripe test-mode API calls, real timed admission cycles, and real issued/validated/tampered JWTs, not just written.
3. **Search Service** — deliberately not split out as its own service. `GET /events/search` lives inside Event Service instead (see below) — a known deviation from the original plan, kept simple since there's no Elasticsearch/independent-scaling need yet to justify the split. Revisit if/when Elasticsearch + CDC actually get added, since that's the point where Search's needs genuinely diverge from Event Service's.
4. **API Gateway** — routing done via YARP, verified end-to-end (all five route groups proxy correctly to the right service). Rate limiting done too — global, per-client-IP fixed-window limiter (`Microsoft.AspNetCore.RateLimiting`, built into ASP.NET Core since .NET 7, no extra package), `429` + `Retry-After` on rejection, verified end-to-end including the window actually resetting.
5. **Auth Service** — done: `POST /auth/register` (hashes via `PasswordHasher<T>`, unique index on `Email` guarantees no duplicate accounts even under concurrent registration attempts), `POST /auth/login` (verifies, issues a JWT signed with a shared HMAC-SHA256 secret). Event Service stays fully public/unauthenticated (matches the spec's "prioritize availability for searching & viewing events") — only Booking Service's mutating endpoints require a token.

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
```
`Program.cs`: registers `EventDbContext` (Npgsql/Postgres) via DI; in Development, auto-runs `Database.MigrateAsync()` + `DbSeeder.SeedAsync()` on startup (not something a real prod deployment would do — see comment in the file).

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
`Program.cs`: registers `BookingDbContext` (Npgsql/Postgres), `IConnectionMultiplexer` (StackExchange.Redis, **singleton** — not scoped like `DbContext` — since the Redis connection is meant to be shared for the app's whole lifetime, not reopened per request), `IStripeClient` (also singleton, same reasoning), JWT Bearer authentication (`AddAuthentication().AddJwtBearer(...)`, validated against the same shared signing key Auth Service signs with), and `QueueAdmissionService` (via `AddHostedService`, not `AddSingleton` — that's what actually makes the host start/stop it) via DI; auto-migrates on startup in Development (no dev-seed needed — reads `Ticket` rows that Event Service already seeded in the shared database).

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

**JWT issuance**: `POST /auth/login` verifies via `PasswordHasher<T>.VerifyHashedPassword` (treats `Success` and `SuccessRehashNeeded` both as success — a known simplification, a fully hardened version would re-hash and persist on `SuccessRehashNeeded`) and, if valid, signs a JWT with `System.IdentityModel.Tokens.Jwt` — HMAC-SHA256, one shared symmetric secret (`Auth:JwtSigningKey`, via **.NET User Secrets**, same pattern as the Stripe keys — set to the *identical* value in both `AuthService`'s and `BookingService`'s secret stores, since symmetric signing means whoever validates needs the exact same key whoever signed used). Claims: `sub` (the user's `Id`) and `email`, both standard JWT-registered claim names. 1-hour expiry. `ValidateIssuer`/`ValidateAudience` both disabled on the validating side (Booking Service) — a known simplification, since we never set `iss`/`aud` claims when issuing.

Login returns distinct messages for "no account with this email" vs. "incorrect password" — a deliberate choice for this project (real systems often return the same message for both, so a caller can't enumerate registered emails by trying logins; not a concern worth the UX cost here). Unique index on `Users.Email` (not just an application-level existence check) is what actually prevents a duplicate account if two registrations for the same email land at the same instant — same reasoning as the Redis TTL lock preventing double-booking, just a plain relational constraint instead of Redis, since this is a single-database concern.

## Running the app locally

The API is not a persistent service — it's only reachable while a `dotnet run` process is alive. Every time: start Postgres + Redis first, then whichever service(s) you're working on.

```bash
# 1. Postgres + Redis (from repo root — only needed once per reboot/container restart, stays up after)
docker compose up -d postgres redis

# 2. Restore EF Core migration tooling (only needed once per clone, or after dotnet-tools.json changes)
dotnet tool restore

# 3. Run a service (from its own folder, e.g. src/EventService/, src/BookingService/, src/ApiGateway/, or src/AuthService/)
cd src/EventService   # or src/BookingService, src/ApiGateway, or src/AuthService
dotnet run
```
Event Service listens on `http://localhost:5049`, Booking Service on `http://localhost:5290`, API Gateway on `http://localhost:5269`, Auth Service on `http://localhost:5003` (all from their own `Properties/launchSettings.json` — auto-assigned per project, no collision). Migrations apply automatically on startup in Development for Event, Booking, and Auth Service — no manual `dotnet ef database update` needed day to day (that command is still how the *first* migration for a new schema change gets generated: `dotnet ef migrations add <Name>`, run from the service's own folder). The Gateway needs the other three already running to actually proxy anywhere — it has no database/migrations of its own. To use Booking Service's protected endpoints, Auth Service needs to be running too, to register/log in and get a token in the first place.

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
