# Fudis — Payment confirmation hardening

Objective: replace the `confirm-payment` endpoint with a version that is idempotent, authenticated,
atomic and resilient to notification outages, without changing the business outcome.

Branch: `main` (single deliverable branch)
Delivery strategy: single PR
Test runner: `dotnet test` (xUnit + EF Core against SQL Server LocalDB)

## Specs

- **S1** — "Si la pasarela envía múltiples notificaciones del mismo pago" the endpoint MUST NOT create more
  than one driver earning for the same order. Acceptance: exactly one `DriverEarning` row per order, enforced by
  a database constraint, and a repeated notification returns a successful idempotent response.
- **S2** — The endpoint MUST authenticate the gateway notification. Unauthenticated calls MUST NOT change state.
  Acceptance: signature (HMAC-SHA256) over the canonical payload, timestamp window, constant-time comparison;
  invalid signature returns 401 and leaves the database untouched.
- **S3** — "el backend recibe una notificación de la pasarela" the payment state change, the driver earning, the
  payment record and the restaurant notification MUST be committed together. Acceptance: one transaction; a
  failure in any of them leaves nothing applied.
- **S4** — "registra una ganancia del repartidor equivalente al 70 % de la tarifa de envío" the earning MUST be
  exactly 70% of `DeliveryFee`, rounded to 2 decimals with an explicit, documented rounding policy, and MUST be
  unique per order.
- **S5** — Business rules MUST be validated before money moves. Acceptance: order must be in a payable state,
  amount and currency must match the order, a driver MUST be assigned; each violation returns a specific error
  code and applies nothing.
- **S6** — "notifica al restaurante" the restaurant notification MUST survive a notification outage.
  Acceptance: transactional outbox + retry dispatcher; a failing notifier never rolls back the payment and never
  loses the notification.
- **S7** — The input contract MUST be documented (README) and pinned by tests.
- **S8** — Tests MUST cover the failure modes, not only the happy path. Acceptance: concurrency, duplicate
  delivery, forged signature, stale timestamp, amount mismatch, invalid state, outbox retry.

## Tasks

| ID | Task | Specs | Route | Status | Evidence |
|----|------|-------|-------|--------|----------|
| T1 | Domain model + DbContext + unique indexes + rowversion | S1,S3,S4 | inline | done | migration `InitialCreate` |
| T2 | Gateway signature validation | S2 | inline | done | `GatewaySignatureValidator` |
| T3 | Payment confirmation service (state machine, idempotency, atomic write) | S1,S3,S4,S5 | inline | done | `PaymentConfirmationService` |
| T4 | Transactional outbox + retry dispatcher | S3,S6 | inline | done | `OutboxDispatcher` |
| T5 | Controller + HTTP contract + legacy endpoint kept for comparison | S2,S7 | inline | done | `PaymentsController`, `LegacyPaymentsController` |
| T6 | Integration test suite on SQL Server LocalDB | S1,S2,S5,S6,S8 | inline | done | `dotnet test` |
| T7 | README: findings, assumptions, contract, guarantees, limits, AI note | S7 | inline | done | `README.md` |

## Log

### L1 — 2026-11-05 — Original request (verbatim)

> "Fudis Prueba técnica del postulante / Desarrollador Senior Backend · Confirmación de pagos y asignación de
> pedidos. Evalúa la implementación existente y entrega una mejora que puedas explicar y defender. [...] Identifica
> los problemas técnicos y de negocio que consideres relevantes. Ordénalos por impacto y describe cómo podrían
> manifestarse. Propón e implementa una versión mejorada del flujo. Incluye los cambios de persistencia necesarios
> y explica las garantías que ofrece. Diseña y ejecuta las pruebas que consideres necesarias. Distingue las
> pruebas realizadas de las propuestas pendientes. Explica qué sucede si la pasarela envía múltiples notificaciones
> del mismo pago. Explica cómo mantienes la consistencia entre el pago, las ganancias del repartidor y la
> notificación al restaurante."

### L2 — 2026-11-05 — Analysis: findings ordered by impact

1. **Idempotencia rota por carrera (CRÍTICO, dinero).** `if (order.Status == "PAID") return Ok();` es
   check-then-act: dos notificaciones concurrentes leen `PENDING`, ambas pasan el if, ambas insertan
   `DriverEarning` → el repartidor cobra doble. El guard-clásico no protege; hace falta una restricción UNIQUE en
   base y tratar la violación de unicidad como duplicado, no como error 500.
2. **Webhook sin autenticar (CRÍTICO, seguridad).** Cualquiera puede hacer POST con un `OrderId` y marcar un pedido
   como pagado. Falta firma, ventana de timestamp y protección anti-replay.
3. **Consistencia rota en la notificación (ALTO).** `SaveChangesAsync()` y después `NotifyRestaurant()`: si la
   notificación falla, el endpoint devuelve 500, la pasarela reintenta, el pedido ya está `PAID`, el early return
   responde `Ok()` y **la notificación nunca llega**. Y si la notificación sí se envió pero la respuesta se perdió,
   se reenvía duplicada.
4. **Sin guardas de negocio (ALTO).** Nada impide pagar un pedido `CANCELLED`, con `DriverId` sin asignar (ganancia
   sin destinatario: plata que se pierde), con tarifa 0, ni valida que el monto pagado coincida con el total.
5. **Estado como string mágica sin máquina de estados (MEDIO).** `"PAID"` hardcodeado, sin transiciones válidas ni
   `RowVersion`, por lo que se pierden actualizaciones concurrentes.
6. **Sin registro del evento de la pasarela (MEDIO).** No hay `ProviderEventId` persistido: no se puede auditar ni
   responder "¿recibí dos eventos o uno?".
7. **Redondeo de dinero sin política (MEDIO).** `DeliveryFee * 0.70m` usa `decimal` (bien) pero no define
   `MidpointRounding` ni guarda la moneda.

### L3 — 2026-11-05 — Design decisions

- **Inbox de eventos** (`PaymentInbox`, UNIQUE `(Provider, ProviderEventId)`) para idempotencia a nivel evento.
- **Outbox transaccional** (`RestaurantNotificationOutbox`, UNIQUE `(OrderId, Type)`) + dispatcher con reintentos
  exponenciales. Consistencia sin transacción distribuida: **at-least-once** en la notificación, exactamente una
  vez en el pago y en la ganancia.
- **Una sola transacción EF** (status + earning + inbox + outbox) — 4 escrituras atómicas.
- **RowVersion** en `Order` como segunda barrera de concurrencia (además del índice único).
- **Contrato de entrada**ompuesto por `Provider`, `ProviderEventId`, `OrderId`, `Amount`, `Currency`, `PaidAt`;
  firma y timestamp en headers (`X-Fudis-Signature`, `X-Fudis-Timestamp`).
- **Envelope de error por código** (`ORDER_NOT_PAYABLE`, `AMOUNT_MISMATCH`, `DRIVER_NOT_ASSIGNED`, ...) para que la
  pasarela pueda decidir reintentar o no.
- **Endpoint legacy conservado** en `api/legacy/payments/confirm-payment` como línea base comparable.

### L4 — 2026-11-05 — Assumptions declared (see README)

A1 gateway sends one event per payment, one payment per order. A2 driver is assigned before payment confirmation.
A3 order total is `TotalAmount` + `DeliveryFee` semantics unknown → seed uses explicit `TotalAmount`.
A4 the restaurant side is idempotent by `OrderId` (documented requirement on the consumer).

### L5 — 2026-11-05 — Result

All 7 tasks completed inline (no delegation needed: single coherent change, no parallel units).
Test suite: 16 integration tests over real SQL Server (LocalDB), all green. Evidence in README.
