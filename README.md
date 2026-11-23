# Scaling ladder

A shipment-tracking API taken from slow to fast one change at a time, with every step measured by
the same load test: indexes, connection pooling, a Redis cache with invalidation, stampede
protection, then a load balancer in front of several instances. For each step: what it bought, what
it cost, and where the bottleneck moved.

**Status:** in progress. See the [implementation plan](docs/IMPLEMENTATION_PLAN.md).

## Run the tests

```bash
dotnet test        # needs the .NET 10 SDK and Docker (for Testcontainers)
```
