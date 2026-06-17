# Fakebook - Social Graph Service

## Overview

The Social Graph Service is the core backend service responsible for managing relationships between all entities within the Fakebook social network.

Instead of storing business-specific tables (Users, Posts, Comments, etc.), this service adopts a **TAO-inspired graph model**, where every entity is represented as an **Object** and every relationship is represented as an **Association**.

The service exposes GraphQL APIs for the Gateway and uses Redis as a high-performance cache layer to accelerate graph queries.

---

## Responsibilities

* Manage generic social graph objects
* Manage relationships between objects
* Maintain relationship counters
* Provide cache-first graph queries
* Synchronize data between Redis and PostgreSQL
* Publish and consume graph events through Kafka
* Expose GraphQL endpoints for the API Gateway

---

## Architecture

```text
                GraphQL Gateway
                       │
                       ▼
            Social Graph Service
                       │
        ┌──────────────┴──────────────┐
        ▼                             ▼
     Redis Cache                PostgreSQL
     (TAO Layer)               (Persistent Storage)
```

Query flow:

```text
Client
    │
    ▼
GraphQL
    │
    ▼
Social Graph Service
    │
    ▼
Redis

Cache Hit
    │
    ▼
Return

Cache Miss
    │
    ▼
PostgreSQL
    │
    ▼
Update Redis
    │
    ▼
Return
```

---

## Data Model

The service implements three core TAO components.

### Objects

Stores every entity in the system.

Examples:

* User
* Page
* Group
* Post
* Comment
* Story
* Reel
* Media
* Album
* Event
* Advertisement
* Notification

---

### Associations

Stores relationships between Objects.

Examples:

* Friend
* Follow
* Like
* React
* Share
* Comment On
* Member Of Group
* Block
* Save

---

### Association Counts

Stores precomputed relationship counters for high-performance graph queries.

Examples:

* Follower Count
* Friend Count
* Like Count
* Comment Count
* Group Member Count

---

## Technology Stack

| Component | Technology   |
| --------- | ------------ |
| Language  | C#           |
| Framework | ASP.NET Core |
| Database  | PostgreSQL   |
| Cache     | Redis        |
| ORM       | Prisma       |
| API       | GraphQL      |
| Messaging | Kafka        |

---

## Repository Structure

```text
SocialGraph/

├── src/
│   ├── GraphQL/
│   ├── Services/
│   ├── Repositories/
│   ├── Redis/
│   ├── Database/
│   └── Events/
│
├── database/
│   └── schema.sql
│
├── docs/
│
├── README.md
└── LICENSE
```

---

## Development Roadmap

### Milestone 1 — Schema Design

* Design Object model
* Design Association model
* Design Association Count model
* Validate TAO schema

### Milestone 2 — TAO Storage Layer

* Object cache
* Association cache
* Association Count cache
* Cache-first read path
* LRU cache management

### Milestone 3 — Internal APIs

* Object APIs
* Association APIs
* Association Count APIs

### Milestone 4 — Service Integration

* GraphQL schema
* GraphQL resolvers
* Gateway integration
* Kafka integration

---

## Design Principles

* Generic graph storage model
* Cache-first architecture
* High read performance
* Loose coupling between services
* Horizontal scalability
* TAO-inspired data model

---

## Status

Current Progress:

*  Milestone 1 completed
*  Milestone 2 in progress

---

## Author

**Phong Bá**

Leader – Fakebook Project

Responsible for:

* Social Graph Service
* Media Service
* Notification Service
