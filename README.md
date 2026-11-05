# Fudis — Confirmación de pagos

Evaluación técnica: evaluación del endpoint `confirm-payment` y entrega de una versión endurecida del flujo.

**Stack:** C# / .NET 8 · Entity Framework Core 8.0.11 · SQL Server (LocalDB) · xUnit 2.9.2
**Pruebas:** 24/24 en verde sobre SQL Server real (no mocks de base de datos).

---

## 1. TL;DR

El fragmento original funciona en el camino feliz y **no está listo para producción**. Los dos problemas que
mueven dinero son:

1. **La idempotencia no aguanta concurrencia.** `if (order.Status == "PAID") return Ok();` es *check-then-act*.
   Dos notificaciones de la misma pasarela que llegan simultáneas leen `PENDING`, las dos pasan el `if`, las dos
   insertan `DriverEarning` → **el repartidor cobra doble**. Reproducido en un test (`ConcurrencyTests.
   WithoutTheUniqueIndex_TheOriginalSchemaPaysTheDriverTwice`).
2. **La notificación al restaurante se pierde.** `SaveChangesAsync()` y después `NotifyRestaurant()`: si la
   notificación falla, el endpoint devuelve 500, la pasarela reintenta, el pedido ya quedó `PAID`, el early return
   responde `Ok()` y **el restaurante nunca se entera**. Y en el caso inverso (notificación enviada, respuesta
   perdida) se vuelve a enviar. Reproducido en `LegacyEndpointTests.LegacyEndpoint_WhenTheRestaurantServiceFails_
   LosesTheNotification_AndFailsTheRequest`.

La solución reemplaza el check-then-act por **garantías de base de datos** (índices únicos, `rowversion`) y por un
**outbox transaccional** que hace que la notificación sobreviva a la caída del servicio del restaurante.

---

## 2. Hallazgos, ordenados por impacto

| # | Impacto | Hallazgo | Cómo se manifiesta |
|---|---------|----------|--------------------|
| 1 | **Crítico — dinero** | Idempotencia rota por carrera. El guard de estado no serializa escrituras concurrentes. | Dos notificaciones concurrentes del mismo pago → 2 filas en `DriverEarnings` → el repartidor recibe el doble. En un sistema real: reclamos de repartidores, ajuste manual de saldos. |
| 2 | **Crítico — seguridad** | El webhook no está autenticado. `PaymentNotification` no tiene firma, timestamp ni nada que lo amarre a la pasarela. | Cualquiera que conozca un `OrderId` hace POST y marca el pedido como pagado → pedidos gratis; o genera ganancias falsas. Un `OrderId` es un entero adivinable. |
| 3 | **Alto — consistencia** | Notificación fuera de la transacción y luego de `SaveChangesAsync()`. | Caída del servicio del restaurante → pago aplicado + notificación perdida + 500 que dispara reintentos infinitos de la pasarela. Notificación entregada pero respuesta perdida → notificación duplicada. |
| 4 | **Alto — negocio** | No hay guardas de negocio: se paga un pedido `CANCELLED`, un pedido sin repartidor asignado, un pedido con tarifa 0, y no se valida que el monto pagado sea el esperado. | Un pedido cancelado "pagado" que dispara la operación logística. Una ganancia para el repartidor `0`/nulo = plata que la plataforma retiene sin registro. Un pago parcial marcado como completo = pérdida de plata. |
| 5 | **Medio — concurrencia** | `Order` sin control de concurrencia optimista; el estado es un `string` sin máquina de estados. | Dos escrituras simultáneas se pisan silenciosamente. `"PAID"`, `"paid"`, `"Paid"` son tres estados distintos para la base. Cualquier estado puede saltar a `PAID`. |
| 6 | **Medio — auditoría** | No se persiste el evento de la pasarela (id del evento, monto, moneda, momento). | Ante un reclamo no se puede responder "¿cuántos eventos recibimos para este pedido?". No hay forma de distinguir "reintento" de "doble cobro". |
| 7 | **Medio — dinero** | `DeliveryFee * 0.70m` sin política de redondeo declarada y sin moneda. | El 70% es un número mágico. El redondeo implícito no está documentado y los centavos se discuten entre plataforma y repartidor. |
| 8 | **Bajo — operabilidad** | Sin logging estructurado, sin métricas, sin códigos de error distinguibles. | Cuando la pasarela reporta un fallo, el backend responde "algo falló" y no hay forma de decidir si reintentar. |

---

## 3. La solución

```
POST /api/payments/confirm-payment
        │
        ├─ 1. Verificar firma HMAC-SHA256 + ventana de timestamp ──── inválida ─→ 401 (no se toca la base)
        │
        ├─ 2. ¿(Provider, ProviderEventId) ya está en PaymentInbox? ── sí ──────→ 200 duplicate (idempotente)
        │       └─ pero el evento apunta a OTRO pedido ────────────────────────→ 409 EVENT_ID_CONFLICT
        │
        ├─ 3. Cargar Order
        │       ├─ no existe ─────────────────────────────────────────→ 404 ORDER_NOT_FOUND
        │       ├─ Status != PendingPayment ──────────────────────────→ 409 ORDER_ALREADY_PAID / ORDER_NOT_PAYABLE
        │       ├─ Currency != Order.Currency ───────────────────────→ 409 CURRENCY_MISMATCH
        │       ├─ Amount  != Order.TotalAmount ─────────────────────→ 409 AMOUNT_MISMATCH
        │       ├─ Amount <= 0 ──────────────────────────────────────→ 400 INVALID_AMOUNT
        │       └─ DriverId sin asignar ─────────────────────────────→ 409 DRIVER_NOT_ASSIGNED
        │
        └─ 4. UNA transacción EF:
               Order.Status = Paid
               + DriverEarning  (70% redondeado)
               + PaymentInbox   (idempotencia del evento)
               + RestaurantNotificationOutbox (notificación al restaurante)
                    │
                    ├─ violación de índice único → el otro request ganó la carrera → 200 duplicate / 409 ORDER_ALREADY_PAID
                    ├─ rowversion en conflicto → 409 ORDER_ALREADY_PAID / CONCURRENCY_CONFLICT
                    └─ deadlock / lock timeout → 409 CONCURRENCY_CONFLICT (reintentable por la pasarela)

Background: OutboxDispatcher (cada 5s) → entrega la notificación al restaurante con reintentos exponenciales.
```

### 3.1 Cambios de persistencia

| Tabla | Cambio | Por qué |
|-------|--------|---------|
| `DriverEarnings` | **UNIQUE INDEX (OrderId)** | Garantía de una sola ganancia por pedido. Resuelta por el motor, no por código de aplicación. |
| `PaymentInbox` (nueva) | **UNIQUE INDEX (Provider, ProviderEventId)** | Idempotencia a nivel de evento de la pasarela. |
| `RestaurantNotificationOutbox` (nueva) | **UNIQUE INDEX (OrderId, Type)**, índice por `(Status, NextAttemptAt)` | Una notificación por pedido; el dispatcher encuentra rápido lo pendiente. |
| `Orders` | columna `RowVersion` (`rowversion`) | Concurrencia optimista: segunda barrera frente al índice único. |
| `Orders` | `Status` como enum almacenado como texto; `DeliveryFee`/`TotalAmount` como `decimal(18,2)`; `Currency` | Estados legibles, monto exacto, moneda explícita. |
| `DriverEarnings` / `PaymentInbox` | `Currency`, `CreatedAt` / `ReceivedAt` / `ProcessedAt` | Trazabilidad del dinero y del evento. |

Migración: `src/Fudis.Payments/Migrations/` · Script SQL idempotente: `database.sql`

---

## 4. Contrato de entrada propuesto

`POST /api/payments/confirm-payment`

**Headers**

| Header | Descripción |
|--------|-------------|
| `X-Fudis-Signature` | HMAC-SHA256 (hex minúscula) del payload canónico. |
| `X-Fudis-Timestamp` | ISO-8601. Ventana aceptada: ±15 min (`Gateway:TimestampToleranceMinutes`). |

**Payload canónico firmado** (los dos extremos deben coincidir byte a byte):

```
{Provider}|{ProviderEventId}|{OrderId}|{Amount:0.00}|{Currency:upper}|{PaidAt:O}|{Timestamp:O}
```

**Body**

```json
{
  "provider": "stripe",
  "providerEventId": "evt_1a2b3c",
  "orderId": 42,
  "amount": 55.00,
  "currency": "BRL",
  "paidAt": "2026-11-05T12:00:00+00:00"
}
```

**Respuestas**

| Código | Significado | Body |
|--------|-------------|------|
| 200 | Pago aplicado | `{ "status": "processed", "orderId": 42 }` |
| 200 | Evento duplicado, ya aplicado | `{ "status": "duplicate", "orderId": 42 }` |
| 400 | Payload inválido | ProblemDetails de ASP.NET |
| 401 | Firma inválida o timestamp vencido | `{ "code": "INVALID_SIGNATURE" \| "STALE_TIMESTAMP" \| ... }` |
| 404 | Pedido inexistente | `{ "code": "ORDER_NOT_FOUND" }` |
| 409 | Regla de negocio violada | `{ "code": "ORDER_ALREADY_PAID" \| "ORDER_NOT_PAYABLE" \| "AMOUNT_MISMATCH" \| "CURRENCY_MISMATCH" \| "DRIVER_NOT_ASSIGNED" \| "EVENT_ID_CONFLICT" }` |
| 409 | Conflicto transitorio, reintentable | `{ "code": "CONCURRENCY_CONFLICT" }` |

El código de error es estable y legible por máquina: la pasarela puede decidir reintentar (409 `CONCURRENCY_CONFLICT`)
o no (409 de negocio, 401).

---

## 5. Garantías: qué SÍ y qué NO

**SÍ garantiza:**

- **A lo sumo una ganancia de repartidor por pedido** — índice único en `DriverEarnings(OrderId)`.
- **A lo sumo una aplicación por evento de la pasarela** — índice único en `PaymentInbox(Provider, ProviderEventId)`.
- **Atomicidad total** — estado del pedido, ganancia, registro del evento y notificación se escriben en **una sola
  transacción**. O se aplica todo, o no se aplica nada.
- **El pago nunca depende del servicio del restaurante** — la notificación se escribe en la misma transacción y se
  entrega después, con reintentos. Un restaurante caído no revierte ni bloquea un pago.
- **Nada se autentica sin firma válida** — HMAC-SHA256 con comparación en tiempo constante y ventana de timestamp.
- **El redondeo del dinero es determinista** — `decimal`, 2 decimales, `MidpointRounding.AwayFromZero`.
- **No se pierde dinero por un repartidor sin asignar** — se rechaza antes de mover plata (`DRIVER_NOT_ASSIGNED`).
- **No se pierde un pago por un id de evento reutilizado** — si el mismo `providerEventId` llega apuntando a otro
  pedido, se responde 409 `EVENT_ID_CONFLICT` en vez de `duplicate`. (Este caso lo destapó la prueba de humo: la
  versión anterior respondía `duplicate` y dejaba el segundo pedido sin confirmar mientras la pasarela creía
  que todo estaba bien.)

**NO garantiza (y es importante decirlo):**

- **No Exactly-Once en la notificación al restaurante.** La entrega es **at-least-once**: si el dispatcher entrega y
  el proceso muere antes de marcar la fila, se reenvía. El consumidor **debe ser idempotente por `orderId`**
  (requisito documentado para el equipo del restaurante). Exactly-once entre dos servicios sin transacción
  distribuida no se puede garantizar de forma honesta.
- **NoExactly-Once en el webhook.** Si la pasarela reintenta después de la ventana de timestamp, la respuesta es
  401 — pero el evento ya quedó registrado en el inbox, así que un reintento tardío de un evento **ya aplicado**
  devuelve 200 `duplicate` en vez de 401 (comportamiento deliberado, para que la pasarela deje de reintentar).
- **No evita el cobro doble si la pasarela manda dos eventos distintos por el mismo pedido.** Se rechaza con 409
  `ORDER_ALREADY_PAID` para que la conciliación lo detecte, pero el segundo cobro **ocurre en la pasarela** y hay
  que reembolsarlo. Esto requiere un flujo de refunds, fuera del alcance de este endpoint.
- **No protege contra una pasarela comprometida.** Si el secreto HMAC se filtra, el atacante puede confirmar pagos.
  La rotación del secreto y la Allowlist de IPs del gateway quedan pendientes.
- **No valida pagos parciales ni múltiples.** Se asume un pago por pedido (supuesto A1).
- **No implementa idempotencia por `orderId` para la API de negocio.** Solo para el webhook de la pasarela.

---

## 6. Supuestos declarados y sus consecuencias

| # | Supuesto | Consecuencia si es falso |
|---|----------|--------------------------|
| A1 | La pasarela manda **un evento por pago** y hay **un pago por pedido**. | Un pedido con dos pagos (split) se rechaza con 409. Requiere modelo de pagos múltiples. |
| A2 | El repartidor está **asignado antes** de confirmar el pago. | Si en Fudis se asigna después, el pago se rechaza con 409 `DRIVER_NOT_ASSIGNED` → el cliente pagó y el pedido queda sin confirmar. **Es el supuesto más frágil: requiere confirmación del negocio.** Alternativa pendiente: ganancia "pendiente de asignar" (ver §9). |
| A3 | `Order.TotalAmount` es el total a pagar (no se sabía su semántica; el fragmento solo daba `DeliveryFee`). | Si `TotalAmount` significa otra cosa, la validación de monto rechaza pagos legítimos. Por eso el monto se agregó al modelo en vez de inferirlo. |
| A4 | El servicio de notificaciones del restaurante es idempotente por `orderId`. | Se podrían generar notificaciones duplicadas al restaurante. |
| A5 | La DB es SQL Server (índices únicos, `rowversion`, `sp_getapplock`). | En otro motor hay que mapear el equivalente (partial unique index, versionado manual). |
| A6 | El secreto HMAC vive en configuración/secret store, no en el repo. | En `appsettings.json` hay un valor de desarrollo explícito que **debe** reemplazarse en producción. |

---

## 7. Pruebas

### 7.1 Ejecutadas — 24/24 en verde (SQL Server real, no mocks)

Archivo: `tests/Fudis.Payments.Tests/`. Evidencia: `evidence/test-results.txt`.

| Grupo | Test | Qué prueba |
|-------|------|-----------|
| Confirmación | `ConfirmedPayment_AppliesStatusEarningInboxAndOutbox_Together` | Camino feliz: las 4 escrituras quedan juntas, ganancia = 21.00 (70% de 30.00) |
| Confirmación | `DriverEarning_IsSeventyPercent_RoundedAwayFromZero` | 10.05 × 0.70 = 7.035 → 7.04 |
| Confirmación | `SameGatewayEventDeliveredTwice_DoesNotPayTheDriverTwice` | Reentrega secuencial → 200 `duplicate`, 1 ganancia |
| Confirmación | `ForgedSignature_IsRejected_AndChangesNothing` | Firma inválida → 401, base intacta |
| Confirmación | `SignatureSignedWithAnotherSecret_IsRejected_AndChangesNothing` | Secreto ajeno → 401 |
| Confirmación | `StaleTimestamp_IsRejected_AndChangesNothing` | Timestamp fuera de ventana → 401 |
| Confirmación | `AmountMismatch_IsRejected_AndNothingIsApplied` | Monto distinto al esperado → 409, nada aplicado |
| Confirmación | `CurrencyMismatch_IsRejected` | Moneda distinta → 409 |
| Confirmación | `OrderThatIsNotPayable_IsRejected` | Pedido cancelado → 409 `ORDER_NOT_PAYABLE` |
| Confirmación | `OrderWithoutAssignedDriver_IsRejected` | Sin repartidor → 409, **0 ganancias** |
| Confirmación | `SecondGatewayEventForAnAlreadyPaidOrder_IsRejectedAsDoubleCharge` | Otro evento sobre pedido pagado → 409 `ORDER_ALREADY_PAID` |
| Confirmación | `UnknownOrder_ReturnsNotFound` | Pedido inexistente → 404 |
| Confirmación | `SameGatewayEventIdForADifferentOrder_IsRejected_AndTheSecondOrderIsNotLeftUnpaid` | Evento reutilizado sobre otro pedido → 409 `EVENT_ID_CONFLICT`, el segundo pedido **no** queda impagado en silencio |
| Concurrencia | `ConcurrentDeliveriesOfTheSameEvent_CreateExactlyOneDriverEarning` | 8 entregas concurrentes del mismo evento → 1 ganancia, 1 inbox, 1 outbox, todas 200 |
| Concurrencia | `ConcurrentDeliveriesOfDifferentEventsForTheSameOrder_CreateExactlyOneDriverEarning` | 6 eventos distintos sobre el mismo pedido → 1 ganancia, 1 respuesta 200 y el resto 409 |
| Concurrencia | `ConcurrentDeliveries_NeverReturnServerError` | Bajo concurrencia nunca hay 500 |
| Concurrencia | `WithoutTheUniqueIndex_TheOriginalSchemaPaysTheDriverTwice` | **Reproduce el defecto original**: sin el índice único, 2 ganancias para un pedido |
| Outbox | `Dispatcher_DeliversPendingNotification_AndMarksItDelivered` | Entrega y marca `Delivered` |
| Outbox | `Dispatcher_WhenTheRestaurantServiceFails_KeepsThePaymentApplied_AndSchedulesARetry` | Caída del restaurante → pago intacto, fila `Pending`, `Attempts=1`, reintento agendado |
| Outbox | `Dispatcher_DeliversOnRetry_AfterATransientFailure` | Reintento posterior entrega y marca `Delivered` |
| Outbox | `NewEndpoint_QueuesTheNotification_InsteadOfCallingTheRestaurantInline` | El request path no toca el servicio del restaurante |
| Legacy | `LegacyEndpoint_AppliesThePaymentAndCreatesTheEarning` | Línea base del fragmento original (documentada, no recomendada) |
| Legacy | `LegacyEndpoint_WhenTheRestaurantServiceFails_LosesTheNotification_AndFailsTheRequest` | **Reproduce el defecto de consistencia**: 500, pago aplicado, notificación perdida, 0 en outbox |
| Legacy | `NewEndpoint_WhenTheRestaurantServiceFails_KeepsThePaymentAndQueuesTheNotification` | Contraste directo: 200, pago aplicado, 1 en outbox |

Además: prueba de humo end-to-end contra la app real (`scripts/smoke-test.ps1`) que firma la notificación como la
pasarela y muestra la reentrega idempotente. Evidencia: `evidence/smoke-test.txt` (incluye el estado del outbox con
el servicio del restaurante caído: pago aplicado, notificación encolada, reintento agendado).

### 7.2 Propuestas / pendientes (NO ejecutadas)

| Propuesta | Por qué no está ejecutada | Prioridad |
|-----------|---------------------------|-----------|
| Test de property-based sobre el redondeo (1000 casos aleatorios) | requiere una librería de property-based testing; el rango actual ya cubre el caso de punto medio | Baja |
| Test de carga (N requests concurrentes sostenidos) midiendo deadlocks | necesita entorno de performance, no un test funcional | Media |
| Test de failover del dispatcher (matar el proceso con una fila en `Processing`) | el reclaim existe (`ProcessingStartedAt`), pero falta el test que lo demuestre | Media |
| Test de contrato del `HttpNotificationService` contra un servidor real | la implementación HTTP real no está en alcance; el test usa un doble | Media |
| Tests E2E del ecosistema completo (restaurante, repartidor, cliente) | fuera del alcance de este endpoint | Baja |

---

## 8. Cómo ejecutarlo

**Requisitos**

| Componente | Versión |
|------------|---------|
| .NET SDK | 8.0.425 (probado con 8.0.x) |
| SQL Server | LocalDB (`MSSQLLocalDB`) o cualquier SQL Server 2019+ |
| EF Core tools | 8.0.11 (tool local del repo: `dotnet tool restore`) |

```bash
# received as a git repository
git clone <repo> && cd fudis-payments

# or received as a compressed archive
# unzip fudis-payments.zip && cd fudis-payments

dotnet tool restore

# 1. Base de datos
dotnet tool run dotnet-ef database update --project src/Fudis.Payments --startup-project src/Fudis.Payments
#    alternativa si tenés SQL Server Command Line Tools:
#    sqlcmd -S "(localdb)\MSSQLLocalDB" -i database.sql

# 2. Pruebas (crea y migra su propia base FudisPaymentsTests)
dotnet test

# 3. Levantar la API
dotnet run --project src/Fudis.Payments --urls http://localhost:5085

# 4. Humo end-to-end (firma la notificación como la pasarela)
pwsh -File scripts/smoke-test.ps1 -BaseUrl http://localhost:5085
```

**Configuración** (`src/Fudis.Payments/appsettings.json`)

| Clave | Default | Nota |
|-------|---------|------|
| `ConnectionStrings:Fudis` | LocalDB `FudisPayments` | En producción: SQL Server real |
| `Gateway:SharedSecret` | `dev-only-secret-replace-me` | **Cambiar**; idealmente por variable de entorno/secret store |
| `Gateway:TimestampToleranceMinutes` | `15` | Ventana anti-replay |
| `Earnings:DriverShareRate` | `0.70` | El 70% deja de estar hardcodeado |
| `Restaurant:BaseUrl` | `http://localhost:5080/` | Servicio de notificaciones del restaurante |

---

## 9. Limitaciones y trabajo pendiente

1. **Supuesto A2 (repartidor asignado antes del pago)** — es el punto más frágil de la propuesta. Alternativa
   recomendada: crear la ganancia en estado `PendingAssignment` y asignarle repartidor cuando se asigne, en vez de
   rechazar el pago. Decisión de negocio, no técnica: **hay que confirmarla**.
2. **Reconciliación y reembolsos** — el 409 `ORDER_ALREADY_PAID` detecta el doble cobro pero no lo revierte. Falta
   el flujo de refunds y la tabla de conciliación con la pasarela.
3. **Exactly-once de la notificación** — requiere que el consumidor sea idempotente (A4). La alternativa es una
   tabla de "notificaciones entregadas" en el servicio del restaurante.
4. **Rotación de claves** — hoy hay una sola clave HMAC. Con dos claves (actual + anterior) se puede rotar sin
   cortar notificaciones en vuelo.
5. **Rate limiting y circuit breaker** — no implementados; el control de volumen del webhook debería estar en el
   API gateway.
6. **Observabilidad** — hay logging estructurado, pero no métricas (contadores de duplicados, reintentos, lag del
   outbox) ni trazas distribuidas.
7. **Notificación fuera de banda** — el dispatcher corre dentro de la API. Con volumen alto conviene un worker
   dedicado (mismo código, otro host).
8. **Prisma de pagos múltiples** — no soportado (A1).
9. **Dead-letter queue del outbox** — tras N intentos la fila sigue en reintento infinito. Falta el estado `Failed`
   terminal y una alerta operativa.

---

## 10. Nota sobre herramientas de IA

**Qué se usó.** Un asistente de IA (OpenCode, modelo MiniMax M3.1 Flash) se usó para: redactar
el análisis inicial de hallazgos, generar el andamiaje del proyecto (csproj, `Program.cs`, DI), el `DbContext` y la
migración, los tests y este README. También se consultó documentación de EF Core 8 y xUnit.

**Qué se verificó y corrigió (nada quedó sin revisar):**

| Corrección | Por qué fue necesaria |
|------------|-----------------------|
| `Convert.TryFromHexString` no existe en .NET 8 (es .NET 9) | Error de compilación; se reescribió con `Convert.FromHexString` + `catch (FormatException)`. |
| `[Range(typeof(decimal), "0.01", "100000000")]` rompía con la cultura de la máquina | `RangeAttribute` parsea los límites con la cultura actual (coma decimal) y tiraba `FormatException`. **Hallazgo real**: el parseo de montos no puede depender de la cultura. Se quitó el atributo y la validación quedó en el servicio, con `InvariantCulture` en toda la canonicalización de la firma. |
| El test de reproducción del defecto original estaba invertido | Insertaba una sola vez y esperaba una violación de índice único; el índice bloquea el **segundo** insert. Corregido: 1 insert ok → 2.º viola → se tira el índice → 2.º insert ok → 2 ganancias. |
| El `finally` del test no podía recrear el índice único | Las 2 filas duplicadas violan el índice al recrearlo. Ahora se borran antes de recrearlo. |
| `DisposeAsync` ocultaba el de `WebApplicationFactory` | Warning CS0114; se implementó explícitamente `IAsyncLifetime.DisposeAsync` para no perder la liberación del servidor de test. |
| La excepción transitoria de base (deadlock 1205) no estaba contemplada | Se agregó `IsTransientConflict` → 409 `CONCURRENCY_CONFLICT` (reintentable) en lugar de 500. |
| Orden de inserts y riesgo de deadlock entre requests concurrentes | Se verificó que EF ordena los inserts de forma determinista y que la colisión de claves se resuelve bloqueando (no en ciclo), por lo que no hay deadlock entre transacciones concurrentes del mismo pedido. |
| La prueba de humo destapó un agujero de **plata perdida** en mi propia solución | El `providerEventId` ya visto respondía `duplicate` sin importar a qué pedido se refería. Si la pasarela reutilizaba un id de evento, el segundo pedido quedaba `PendingPayment` con la respuesta "todo bien". Corregido: 409 `EVENT_ID_CONFLICT` + test `SameGatewayEventIdForADifferentOrder_IsRejected_...`. |
| Revisión adversarial automatizada mult lente | Se intentó lanzar sobre el candidato y **quedó no disponible**: el contrato nativo exige un esquema exacto para la selección de archivos untracked que la CLI no expone, y el commit raíz no se puede expresar como diff con base (árbol vacío). **No se afirma ninguna revisión automática aprobada**; la evidencia es la suite de 24 tests + el smoke test + la inspección del esquema por SQL. |

**Verificación manual adicional:** los tests corren contra **SQL Server real** (LocalDB), no contra un proveedor en
memoria, precisamente porque los comportamientos relevantes (índice único, `rowversion`, violación de clave bajo concurrencia)
no se reproducen en SQLite ni en `InMemory`. El esquema se inspeccionó por SQL para confirmar que los tres índices
únicos y la columna `rowversion` existen.

**Responsabilidad.** Toda la lógica entregada fue revisada y comprendida antes de considerarla final. En la defensa
puedo explicar el porqué de cada decisión sin el asistente.

---

## 11. Estructura del repo

```
fudis-payments/
├─ src/Fudis.Payments/
│  ├─ Domain/            Order, DriverEarning, PaymentInbox, RestaurantNotificationOutbox, enums
│  ├─ Contracts/         PaymentNotification (contrato propuesto) + LegacyPaymentNotification
│  ├─ Services/          PaymentConfirmationService, GatewaySignatureValidator, OutboxDispatcher, MoneyPolicy
│  ├─ Controllers/       PaymentsController (nuevo) + LegacyPaymentsController (original, como referencia)
│  ├─ Infra/             FudisDbContext (+ factory de design-time)
│  └─ Migrations/        InitialCreate
├─ tests/Fudis.Payments.Tests/    24 tests de integración sobre SQL Server
├─ scripts/smoke-test.ps1         demo end-to-end firmado
├─ database.sql                   script SQL idempotente
├─ evidence/test-results.txt      salida de la corrida de tests
└─ odd/tasks/                     documento de feature: specs, tasks y log
```

El endpoint original **se conserva** en `api/legacy/payments/confirm-payment`, ejecutable, para que las diferencias
se puedan demostrar y no solo describir.
