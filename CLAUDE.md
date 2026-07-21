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

No `Booking` or `User` entities yet — those belong to the Booking Service's concern, deliberately not modeled by Event Service even though it's the same physical database.

Booking/reservation concurrency will use a Redis key `{ticketId: userId}` with a 10-minute TTL (the "Ticket Lock (Redis)" component) once the Booking Service is built — this is the mechanism for Deep Dive 1 (preventing double-booking), not a DB-level lock or cron cleanup job.

## Build order

1. **Event Service** — done: `GET /events/{id}` returns `Event+Venue+Performer+Tickets` from real Postgres data via EF Core, dev-seeded on startup. `GET /events/search` supports `keyword` (ILIKE against name/description), `start`/`end` date range, and `page`/`pageSize` pagination — projects straight to `EventSummaryResponse` in the SQL query itself rather than loading full entities first. Both use response DTOs (`Dtos/`) rather than returning entities directly — avoids leaking EF Core's circular navigation properties into the JSON.
2. **Booking Service** — not started. The interesting part: reservation flow, Redis TTL locks, preventing double-booking, Stripe
3. **Search Service** — not started. Postgres full-text → Elasticsearch, CDN/query caching

Solution layout: `Ticketmaster.slnx` (root) with each service under `src/<ServiceName>` — e.g. `src/EventService`. Note: .NET 10's `dotnet new sln` now generates the newer XML-based `.slnx` format by default instead of the classic `.sln`.

### Event Service structure

```
src/EventService/
  Controllers/EventsController.cs   GET /events/{id}
  Models/                           EF Core entities (Event, Venue, Performer, Ticket, TicketStatus)
  Dtos/EventResponse.cs             API response shape (EventResponse/VenueResponse/PerformerResponse/TicketResponse records)
  Data/EventDbContext.cs            DbContext, DbSets
  Data/DbSeeder.cs                  Dev-only: seeds one test event if Events table is empty
  Migrations/                       EF Core migrations (InitialCreate applied)
```
`Program.cs`: registers `EventDbContext` (Npgsql/Postgres) via DI; in Development, auto-runs `Database.MigrateAsync()` + `DbSeeder.SeedAsync()` on startup (not something a real prod deployment would do — see comment in the file).

## Running the app locally

The API is not a persistent service — it's only reachable while a `dotnet run` process is alive. Every time: start Postgres first, then the service.

```bash
# 1. Postgres (from repo root — only needed once per reboot/Docker restart, stays up after)
docker compose up -d postgres

# 2. Restore EF Core migration tooling (only needed once per clone, or after dotnet-tools.json changes)
dotnet tool restore

# 3. Run the Event Service (from src/EventService/)
cd src/EventService
dotnet run
```
Listens on `http://localhost:5049` by default (the `http` profile in `Properties/launchSettings.json`). Migrations + dev seed data apply automatically on startup in Development — no manual `dotnet ef database update` needed day to day (that command is still how the *first* migration for a new schema change gets generated: `dotnet ef migrations add <Name>`, run from the service's own folder).

Leave that terminal running while testing; `Ctrl+C` to stop. VS Code's Run/Debug panel (`F5`) does the same thing with a debugger attached.

**Testing the running API**: `src/EventService/EventService.http` has saved requests runnable via VS Code's **REST Client** extension (click "Send Request" above a request, or place cursor in one and hit `Cmd+Alt+R`) — or plain `curl http://localhost:5049/events/<id>`.

## Deployment

Local only, no cloud, to keep cost at zero. Postgres/Redis run via Docker Compose once Docker is available locally (fallback: Homebrew services if Docker setup is deferred). Revisit cloud deployment only if explicitly asked.

## Environment status

- .NET 10 SDK: working (`dotnet --version` → 10.0.302)
- Container runtime: **Rancher Desktop** (not Docker Desktop) — `docker --version` → 29.5.3-rd, `docker compose version` → v5.1.4. Fully drop-in compatible with `docker`/`docker compose`; nothing in `docker-compose.yml` or any command in this file needed to change.

Setup note if this ever needs reinstalling: `brew install --cask rancher` (no sudo needed — it's a plain `.app`). On first launch, the setup wizard **must** select **"dockerd (moby)"** as the container engine (not "containerd") — that's what provides the compatible `docker` CLI; the other option only gives you `nerdctl`. Kubernetes stays disabled, not needed here. Its CLI tools install to `~/.rd/bin`, added to shell rc files automatically — only picked up by *new* terminal sessions, not ones already open at install time.
