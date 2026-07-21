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

- **API Gateway** — routing, auth, rate limiting
- **Event Service** — event/venue/performer read model
- **Booking Service** — ticket reservation + purchase flow, Redis distributed locks, Stripe integration
- **Search Service** — event search (Postgres full-text first, Elasticsearch later)

**Data ownership**: one shared PostgreSQL database/schema across all services for now (matches the reference article, keeps focus on .NET + booking concurrency logic first). Database-per-service is a deliberate future refactor, not the starting point — don't split it unprompted.

## Core entities & data model

As implemented in `src/EventService/Models/` (EF Core entity classes, migrated into real Postgres tables):

- **Event**: `Id, Name, Description?, Date, VenueId, PerformerId` + nav `Venue`, `Performer`, `Tickets[]`
- **Venue**: `Id, Name, Address, Capacity, SeatMap?` (raw JSON string for now, not modeled as C# classes — only needed if/when a seat-map UI has to traverse its internal structure)
- **Performer**: `Id, Name, Type`
- **Ticket**: `Id, EventId, Seat, Price, Status (Available|Booked enum), UserId?`

`Booking`/`BookingTicket` entities live in `src/BookingService/Models/` instead — see Booking Service structure below. Booking Service also keeps its own minimal `Ticket`/`TicketStatus` (duplicated from Event Service's, not shared — separate deployable services, deliberately no compile-time coupling between them).

Booking/reservation concurrency uses a Redis key `ticket-lock:{ticketId}` → `userId`, with a 10-minute TTL (`SET key value NX EX 600`) — this is the actual mechanism for Deep Dive 1 (preventing double-booking), not a DB-level lock or cron cleanup job. `Ticket.Status` in Postgres only flips to `Booked` at confirm/payment time (not yet built) — the reserve step never touches it, reservation state lives entirely in Redis.

## Build order

1. **Event Service** — done: `GET /events/{id}` returns `Event+Venue+Performer+Tickets` from real Postgres data via EF Core, dev-seeded on startup. `GET /events/search` supports `keyword` (ILIKE against name/description), `start`/`end` date range, and `page`/`pageSize` pagination — projects straight to `EventSummaryResponse` in the SQL query itself rather than loading full entities first. Both use response DTOs (`Dtos/`) rather than returning entities directly — avoids leaking EF Core's circular navigation properties into the JSON.
2. **Booking Service** — in progress: `POST /bookings/reserve` implemented and verified (Redis TTL lock prevents double-booking, sequential-acquire-with-rollback for multi-ticket requests, DB checked as a second layer after locks succeed). Not yet built: confirm/payment step, Stripe integration, virtual waiting queue.
3. **Search Service** — not started. Postgres full-text → Elasticsearch, CDN/query caching

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
  Controllers/BookingsController.cs   POST /bookings/reserve
  Models/                             Booking, BookingTicket (owned), Ticket/TicketStatus (read-only view of Event Service's table)
  Dtos/ReserveBookingRequest.cs        ReserveBookingRequest/ReserveBookingResponse records
  Data/BookingDbContext.cs             DbContext; Ticket is .ExcludeFromMigrations() since Event Service owns that table's schema
  Migrations/                          EF Core migrations, own history table __BookingServiceMigrationsHistory (kept separate from Event Service's, since both target the same physical database)
```
`Program.cs`: registers `BookingDbContext` (Npgsql/Postgres) and `IConnectionMultiplexer` (StackExchange.Redis, registered as a **singleton** — not scoped like `DbContext` — since the Redis connection is meant to be shared for the app's whole lifetime, not reopened per request) via DI; auto-migrates on startup in Development (no dev-seed needed — reads `Ticket` rows that Event Service already seeded in the shared database).

**Known simplification**: `ReserveBookingRequest.UserId` comes directly in the request body — no auth/API Gateway exists yet to derive it from a real session.

## Running the app locally

The API is not a persistent service — it's only reachable while a `dotnet run` process is alive. Every time: start Postgres + Redis first, then whichever service(s) you're working on.

```bash
# 1. Postgres + Redis (from repo root — only needed once per reboot/container restart, stays up after)
docker compose up -d postgres redis

# 2. Restore EF Core migration tooling (only needed once per clone, or after dotnet-tools.json changes)
dotnet tool restore

# 3. Run a service (from its own folder, e.g. src/EventService/ or src/BookingService/)
cd src/EventService   # or src/BookingService
dotnet run
```
Event Service listens on `http://localhost:5049`, Booking Service on `http://localhost:5290` (both from their own `Properties/launchSettings.json` — auto-assigned per project, no collision). Migrations apply automatically on startup in Development for both — no manual `dotnet ef database update` needed day to day (that command is still how the *first* migration for a new schema change gets generated: `dotnet ef migrations add <Name>`, run from the service's own folder).

Leave the terminal(s) running while testing; `Ctrl+C` to stop. VS Code's Run/Debug panel (`F5`) does the same thing with a debugger attached — pick which project to launch if prompted, since there's now more than one.

**Testing the running API**: each service has its own `.http` file (`src/EventService/EventService.http`, `src/BookingService/BookingService.http`) runnable via VS Code's **REST Client** extension (click "Send Request" above a request, or place cursor in one and hit `Cmd+Alt+R`) — or plain `curl`.

## Deployment

Local only, no cloud, to keep cost at zero. Postgres/Redis run via Docker Compose once Docker is available locally (fallback: Homebrew services if Docker setup is deferred). Revisit cloud deployment only if explicitly asked.

## Environment status

- .NET 10 SDK: working (`dotnet --version` → 10.0.302)
- Container runtime: **Rancher Desktop** (not Docker Desktop) — `docker --version` → 29.5.3-rd, `docker compose version` → v5.1.4. Fully drop-in compatible with `docker`/`docker compose`; nothing in `docker-compose.yml` or any command in this file needed to change.

Setup note if this ever needs reinstalling: `brew install --cask rancher` (no sudo needed — it's a plain `.app`). On first launch, the setup wizard **must** select **"dockerd (moby)"** as the container engine (not "containerd") — that's what provides the compatible `docker` CLI; the other option only gives you `nerdctl`. Kubernetes stays disabled, not needed here. Its CLI tools install to `~/.rd/bin`, added to shell rc files automatically — only picked up by *new* terminal sessions, not ones already open at install time.
