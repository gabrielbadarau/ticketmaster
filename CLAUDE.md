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

Exact field lists confirmed from the reference article's diagrams (not just its text) — use these as the starting point for EF Core entities:

- **Event**: `id, venueId, performerId, tickets[], name, description, ...`
- **Venue**: `id, location, seatMap, ...`
- **Performer**: `id, ...`
- **Ticket**: `id, eventId, seat, price, status (available|booked), userId`
- **Booking**: `id, userId, tickets`

Booking/reservation concurrency uses a Redis key `{ticketId: userId}` with a 10-minute TTL (the "Ticket Lock (Redis)" component) — this is the mechanism for Deep Dive 1 (preventing double-booking), not a DB-level lock or cron cleanup job.

## Build order

1. **Event Service** — simplest read-only service, get .NET/EF Core basics working end-to-end (working: `GET /events/{id}` returns Event+Venue+Performer+Tickets from real Postgres data via EF Core, dev-seeded on startup. Returns raw entities with `ReferenceHandler.IgnoreCycles` for now — known rough edge, a real response DTO is the next refinement to avoid the `null` cycle-breaking artifacts in the JSON)
2. **Booking Service** — the interesting part: reservation flow, Redis TTL locks, preventing double-booking, Stripe
3. **Search Service** — Postgres full-text → Elasticsearch, CDN/query caching

Solution layout: `Ticketmaster.slnx` (root) with each service under `src/<ServiceName>` — e.g. `src/EventService`. Note: .NET 10's `dotnet new sln` now generates the newer XML-based `.slnx` format by default instead of the classic `.sln`.

## Deployment

Local only, no cloud, to keep cost at zero. Postgres/Redis run via Docker Compose once Docker is available locally (fallback: Homebrew services if Docker setup is deferred). Revisit cloud deployment only if explicitly asked.

## Environment status

- .NET 10 SDK: working (`dotnet --version` → 10.0.302)
- Docker: working (`docker --version` → 29.6.2, `docker compose version` → v5.3.1)

Both confirmed working as of 2026-07-20. Re-check if either command starts failing (e.g. after a machine restart with Docker Desktop not running).
