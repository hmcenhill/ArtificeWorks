# ArtificeWorks

The manufacturing backbone of **Hermannsson Artifice Works** — a virtual factory that builds automata for the work humans shouldn't have to do alone.

Hermannsson produces three configurable product lines from a single shared platform — a common chassis, power core, and control architecture: the **Custodian**, a service automaton for logistics and facility work; the **Delver**, a rugged mining unit built for extraction and hazardous terrain; and the **Courier**, a wheeled delivery automaton for last-mile transport. Every unit ships configure-to-order, its tool-hands, locomotion, and sensor suite swapped from a shared parts catalog — a mine-spec Delver shares 70% of its bill of materials with a hospitality Custodian.

This repository is the event-driven system that runs the factory: work order scheduling, BOM-driven material picking, production and inspection workflows, and shipment scheduling — built as a portfolio project demonstrating event-driven architecture and domain-driven design. See [docs/Plan](docs/Plan/) for the roadmap.

## Documentation

- [Messaging topology](docs/messaging-topology.md) — RabbitMQ exchange, queues, bindings, message shape, and how a correlation id threads one work order's story through both services' logs.
- [Observability](docs/observability.md) — the runbook: which of the four surfaces answers which question, how to bring the telemetry stack up locally, and **"what happened to work order X?"** worked end to end with runnable queries.

## Local infrastructure

This project uses Docker Compose for local development infrastructure.

### Services

- PostgreSQL: `localhost:5432`
- RabbitMQ AMQP: `localhost:5672`
- RabbitMQ Management UI: `http://localhost:15672`
- Redis: `localhost:6379`
- Grafana (traces, metrics, logs): `http://localhost:3000`
- OTLP ingest: `localhost:4317` (gRPC), `localhost:4318` (HTTP)

### Default credentials

#### PostgreSQL

- Database: `artificeworks`
- Username: `postgres`
- Password: `postgres`

#### RabbitMQ

- Username: `guest`
- Password: `guest`

### Start services

```bash
docker compose up -d

```
