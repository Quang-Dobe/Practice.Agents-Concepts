# Event Sourcing — Deep Analysis
> Source: [Martin Fowler — EventSourcing (2005)](https://martinfowler.com/eaaDev/EventSourcing.html)  
> Analyzed: April 26, 2026

---

## Table of Contents

1. [Core Thesis](#1-core-thesis)
2. [How It Works](#2-how-it-works)
   - [2.1 Fundamental Mechanics](#21-fundamental-mechanics)
   - [2.2 Application State Storage](#22-application-state-storage)
   - [2.3 Structuring Event Handler Logic](#23-structuring-event-handler-logic)
   - [2.4 Reversing Events](#24-reversing-events)
   - [2.5 External Updates](#25-external-updates)
   - [2.6 External Queries](#26-external-queries)
   - [2.7 External Interaction (Both Together)](#27-external-interaction-both-together)
   - [2.8 Code Changes](#28-code-changes)
   - [2.9 Events and Accounts](#29-events-and-accounts)
3. [When to Use It](#3-when-to-use-it)
4. [Code Examples — C# Walkthroughs](#4-code-examples--c-walkthroughs)
   - [4.1 Tracking Ships (Basic)](#41-tracking-ships-basic)
   - [4.2 Updating an External System](#42-updating-an-external-system)
   - [4.3 Reversing an Event](#43-reversing-an-event)
   - [4.4 External Query with Logged Gateway](#44-external-query-with-logged-gateway)
5. [Key Patterns Referenced](#5-key-patterns-referenced)
6. [Critical Trade-offs Summary](#6-critical-trade-offs-summary)

---

## 1. Core Thesis

**One-line definition:**  
> *Capture all changes to application state as a sequence of events.*

The blog opens with a profound but simple motivation: querying current state answers *where we are*, but sometimes we need to know *how we got here*. Event Sourcing solves this by making the event log — not the final state — the **source of truth**.

### What changes structurally

| Traditional Approach | Event Sourcing |
|---|---|
| State is mutated in place | State is **derived** from an ordered event log |
| History is lost (or must be explicitly logged) | History is the primary artifact |
| A "record" = current snapshot | A "record" = append-only sequence of events |

### The guarantee

The key insight Fowler emphasizes is not simply "log your changes." The differentiator is:

> *All changes to domain objects are **initiated** by event objects.*

This is a stronger contract than logging. It means no mutation happens outside the event system — the event log is a complete, authoritative replay surface.

---

## 2. How It Works

### 2.1 Fundamental Mechanics

Fowler uses a **ship-tracking system** as the running example. In the traditional model:
- A service method receives a movement notification
- It finds the ship and mutates its location field
- Only the final state persists

With Event Sourcing:
1. The service creates an **event object** (e.g., `ArrivalEvent`) capturing what happened
2. The event is **processed** — applied to the domain model
3. The event is **persisted** alongside (or instead of) the current state

Two artifacts now exist:
- **Application State** — the current materialized view of the world
- **Event Log** — the immutable, ordered sequence of everything that ever happened

This duality enables three powerful capabilities:

| Capability | Description |
|---|---|
| **Complete Rebuild** | Discard current state; replay all events from scratch to reconstruct it |
| **Temporal Query** | Replay only events up to a specific point in time to see past state |
| **Event Replay** | Correct a past event and recompute all downstream consequences |

**Deep analysis:** These three capabilities are not just conveniences — they fundamentally change the epistemology of the system. In a traditional system, a bug that corrupts state is catastrophic. In Event Sourcing, corrupted state is irrelevant if the event log is intact — you simply fix the processing logic and rebuild. This is comparable to how a blockchain is immutable and reconstructible, or how a database WAL (Write-Ahead Log) can rebuild state.

Fowler also notes version control systems (e.g., Subversion) as a canonical real-world example of Event Sourcing: history is the primary artifact and current state is computed from it.

---

### 2.2 Application State Storage

Two storage strategies exist:

#### Strategy A: Blank Slate + Full Replay
- Start from an empty application state
- Replay every event from the beginning
- Simple but **O(n)** in cost — unacceptable for large event logs

#### Strategy B: Snapshots (Practical Default)
- Store a **snapshot** of application state periodically (e.g., nightly)
- On startup, load the latest snapshot and replay only events **since that snapshot**
- Dramatically reduces replay cost
- Snapshots can be made **in parallel** without downtime

**Deep analysis:** The snapshot strategy introduces a tension: snapshots are derivable artifacts (not the source of truth), yet they must be kept consistent. If a snapshot is corrupted or lost, the system can always rebuild it. This is a powerful safety property. The event log is the **irreplaceable** artifact; snapshots are merely a performance optimization.

#### System of Record: Two valid choices

1. **Event log is the system of record** — databases holding application state are secondary views, rebuildable at will. This is the purer form of Event Sourcing.
2. **Application state (database) is the system of record** — event logs exist for audit and replay purposes only. This is a pragmatic hybrid common in legacy systems.

The choice affects operational complexity significantly. Option 1 is more powerful but requires discipline; Option 2 is easier to retrofit onto existing systems.

---

### 2.3 Structuring Event Handler Logic

This is a nuanced design decision that Fowler breaks into two independent axes:

#### Axis 1: Where does domain logic live?

| Approach | Description | Best For |
|---|---|---|
| **Transaction Script** | Event handler directly contains all business logic | Simple, low-complexity domains |
| **Domain Model** | Event hands off to rich domain objects that contain business logic | Complex, evolving domains |

Fowler observes a common anti-pattern: developers believe event-driven systems *require* Transaction Scripts. This is false — a Domain Model is entirely compatible and usually superior for complex domains.

#### Axis 2: Where does selection logic live?

- **Processing Selection Logic**: decides *which* domain logic runs for a given event type
- **Processing Domain Logic**: the actual business rules

These can be combined (Transaction Script style) or separated (Domain Model style).

Further, selection logic can live in:
- **The event object itself** — preferred, because the event type is the varying thing; this avoids type-switch anti-patterns
- **A separate event processor object** — necessary when event objects are DTOs that cannot contain code (e.g., auto-serialized wire formats)

**Deep analysis:** The recommendation to embed selection logic in the event object is an application of the **Open/Closed Principle** — adding a new event type does not require modifying a central processor. This is also why polymorphic dispatch on event objects is architecturally superior to a large `switch(eventType)` statement. However, when working with serialization frameworks that don't support code in DTOs, Fowler suggests using naming conventions or configuration to map DTOs to their corresponding event handler types — effectively a lightweight registry pattern.

---

### 2.4 Reversing Events

Reversal is the ability to **undo** an event and restore prior state.

#### Design requirement: difference-form events
Reversal is straightforward when events capture **differences** rather than absolute values:
- `"Add $10 to Martin's account"` → reversible by subtracting $10
- `"Set Martin's account to $110"` → NOT reversible without knowing the prior value

When events are not in difference form, the event must **store prior values** during its `Process()` execution so that its `Reverse()` method has everything it needs.

#### Where to store prior values
Prior state data is stored **on the event object itself**. Since events are passed through the domain model during processing, domain objects can write their prior state onto the event. Examples from the C# code:
- `ev.priorPort = _port;` — a scalar prior value for a single object
- `ev.priorCargoInCanada[this] = _hasBeenInCanada;` — a dictionary keyed by domain object, for events that affect multiple objects

**Deep analysis:** This design has a subtle consequence: events are no longer purely immutable once they carry prior-value data accumulated during processing. They start as data records and become enriched with context during their own execution. This is acceptable because prior data is deterministic — replaying the same event against the same state produces the same prior data. The event remains **referentially transparent** in the mathematical sense, even if mutated during a single pass.

#### When reversal is not needed
Any reversal can be replaced by:
1. Revert to a past snapshot
2. Replay forward from that snapshot, excluding or replacing the erroneous event

Reversal is therefore a **performance optimization**, not a functional requirement. It pays off when reversing a few recent events is far cheaper than replaying thousands of old ones.

---

### 2.5 External Updates

This is one of the most operationally dangerous aspects of Event Sourcing.

**The problem:** Event replay is designed to be idempotent and repeatable. But external systems (payment gateways, email services, customs APIs) have their own state. Replaying an event that sends a payment notification will trigger duplicate real-world side effects.

**The solution: Gateway pattern + replay mode flag**

All external interactions are wrapped in a **Gateway** object. The gateway knows whether the system is in "live" or "replay" mode:

```csharp
// Domain logic calls gateway uniformly — it doesn't know about replay
Registry.CustomsNotificationGateway.Notify(ev.Occurred, ev.Ship, ev.Port);

// Gateway decides internally whether to forward the call
class CustomsEventGateway {
    public void Notify(DateTime arrivalDate, Ship ship, Port port) {
        if (processor.isActive) 
            SendToCustoms(BuildArrivalMessage(arrivalDate, ship, port));
    }
}
```

During replay, `processor.isActive` is `false`, so no external calls are made.

**Deeper strategies for external updates:**
1. **Disable gateways during replay** — sufficient for full rebuilds and temporal queries
2. **Buffered notifications** — delay external messages until a commitment deadline (e.g., end-of-month). This gives freedom to reprocess up until that deadline without worrying about external side effects
3. **Notify via domain events** — instead of calling external APIs directly, emit a notification domain event that the gateway subscribes to; the gateway only acts on events marked as "live"

**Deep analysis:** The Gateway pattern here is not optional — it is architecturally mandatory for correct Event Sourcing with external dependencies. The domain model must remain agnostic about whether it is processing "for real" or for replay. This separation of concerns is critical and maps to the **Strangler Fig** and **Anti-Corruption Layer** concepts in DDD.

---

### 2.6 External Queries

**The problem:** If processing an event on December 5th queries an external system for an exchange rate, and that event is replayed on December 20th, the external system returns a *different* exchange rate. The rebuild is now **non-deterministic**.

**Solutions in order of preference:**

1. **External system supports temporal queries** — ask for the rate "as of Dec 5th." Reliable if the external system is trustworthy.
2. **Event Collaboration** — use events to track history of external data changes, maintaining your own view of their history
3. **Logged Gateway (most general)** — wrap the external gateway with a logging layer that:
   - On first call: makes the real request and stores the response linked to the current domain event being processed
   - On replay: finds the stored response by matching the domain event reference, and returns the cached answer

```csharp
class LoggedPricingGateway {
    public Money GetPrice(Cargo cargo) {
        GetPriceRequest oldReq = oldRequest(cargo);
        if (null != oldReq) return (Money) oldReq.Result; 
        else return newRequest(cargo);
    }
}
```

**Deep analysis:** The logged gateway approach is elegant but has a significant operational cost: **every external query response must be persisted alongside the event log**. This log of query responses becomes part of the system of record, as essential as the event log itself. Losing it means rebuilds become non-deterministic. This is often overlooked in Event Sourcing implementations and can lead to subtle bugs in replay scenarios.

A secondary concern is **matching**: the gateway must reliably match a replay call to its historical response. Using the current domain event as a key (as shown in the example) works well when each event generates a bounded number of external queries. If an event generates dynamic or variable numbers of queries, the matching logic becomes more complex.

---

### 2.7 External Interaction (Both Together)

Fowler briefly flags this as the hardest case: an external call that **both returns data and causes a state change** in the external system (e.g., "submit order, get delivery confirmation").

This combines the difficulties of external updates (side effects on replay) and external queries (non-deterministic data on replay) into a single interaction. No clean general solution is presented — Fowler acknowledges it's inherently complex and refers to [Retroactive Event](https://martinfowler.com/eaaDev/RetroactiveEvent.html) for detailed treatment.

**Deep analysis:** In practice, this scenario is the strongest argument for designing external APIs to be **idempotent** (safe to call multiple times with the same parameters) and **separating command and query** (CQS principle). If you own the external system, designing it with idempotency keys (common in payment APIs like Stripe) largely eliminates this problem. If you don't own it, the logged gateway strategy is the best available mitigation, but it requires careful engineering.

---

### 2.8 Code Changes

Events handle data changes — but the system also evolves through code changes. Fowler categorizes three types:

#### Type 1: New Features
- Add new capabilities without invalidating past events
- Old events can be replayed through new code to produce enriched results
- External gateways should generally be disabled during this reprocess
- **Verdict:** Low risk, low complexity

#### Type 2: Bug Fixes
- Identify that past processing was wrong
- Fix the code and **replay all events** — the application state is now corrected to what it should have been
- For internal state only: trivially easy
- For external gateways: complex — gateways must track the difference between "what happened with the bug" and "what happens without it"
- This parallels the Retroactive Event pattern

**Deep analysis:** Bug fixing via replay is one of the most underappreciated superpowers of Event Sourcing. In traditional systems, a bug that corrupted data often requires painful data migration scripts with uncertain coverage. In Event Sourcing, a bug fix is "fix the code, replay the log." The corrected state materializes automatically. This fundamentally changes the economics of production bug remediation.

#### Type 3: Temporal Logic (Logic that Changes Over Time)
- Business rules themselves change over time: "charge $10 before Nov 18, $15 after"
- The domain model must be able to run events with the **correct rules for the event's timestamp**, not today's rules
- Simple approach: conditional logic in the domain model — gets messy fast
- Better approach: **Temporal Property** pattern with Strategy objects

```csharp
// Temporal Property pattern
chargingRules.get(aDate).process(anEvent);
```

**Deep analysis:** This is bi-temporal complexity in disguise. When bugs and temporal logic intersect, you get scenarios like: "reverse this event using the rules that were valid on August 1st as understood on October 1st, then reapply it using August 1st's rules as understood today." This is extremely difficult to manage and Fowler explicitly cautions against going down this path unless absolutely necessary.

An alternative is to **put code in data** — using Adaptive Object Models or embeddable scripting languages (e.g., JRuby in a Java app) where processing scripts are stored as data, versioned, and changed through events themselves. This is speculative but elegant in theory.

---

### 2.9 Events and Accounts

Fowler observes a natural synergy between Event Sourcing and **accounting systems**:

- Accounting already thinks in events: every transaction is a journal entry, never a mutation of a balance
- Audit trails are mandatory in accounting, aligning perfectly with the event log
- Double-entry bookkeeping is itself a form of Event Sourcing — the ledger entries are the primary record, balances are derived

The pattern crystallizes: domain events cause the creation of `AccountingEntry` objects, which are linked back to the originating event. This provides:
- Clean audit trails
- Reversibility via counter-entries
- A natural basis for balance reconstruction

**Deep analysis:** This is not coincidental. The accounting domain literally invented append-only ledgers centuries before computer science. Event Sourcing can be seen as the generalization of ledger-based thinking to arbitrary domain state. The fit is so natural that accounting systems are often cited as the "killer app" for Event Sourcing — the domain requirements (immutable history, auditability, reversal via adjustment) map 1:1 to Event Sourcing capabilities.

---

## 3. When to Use It

Fowler is candid: Event Sourcing is not a default choice. The "event as interface" style is uncomfortable for many developers and introduces real complexity. You should adopt it when the return justifies the investment.

### Use Case 1: Audit Logging
Event Sourcing naturally produces a complete, tamper-evident audit trail. Every state change has a corresponding event with timestamps and causal data. This is directly useful for:
- Compliance and regulatory requirements
- Customer support (reproduce exactly what a user did)
- Forensic analysis

**Caveat:** A regular log file can serve simple audit needs. Event Sourcing earns its keep when you need the audit trail to be **executable** — not just readable, but replayable.

### Use Case 2: Debugging and Testing
- Reproduce production bugs deterministically: capture the event log from production, replay it in a test environment
- "Stop, rewind, replay" debugging — like a time-traveling debugger
- **Parallel testing before upgrades**: replay real production events against the new code and validate results before going live

**Deep analysis:** This is transformative for complex distributed systems where reproducing bugs is notoriously hard. In traditional systems, you need production data snapshots and log files, and even then reproduction is non-deterministic. With Event Sourcing, the event log *is* the exact recipe for reproducing any system state.

### Use Case 3: Foundation for Advanced Patterns
Event Sourcing is a **prerequisite** for:
- **Parallel Model** — running multiple model versions simultaneously against the same event log
- **Retroactive Event** — inserting, modifying, or deleting past events and recomputing consequences

Fowler is emphatic: **retrofitting these patterns onto a system without Event Sourcing is extremely difficult**. This is one of the rare cases where deferring the decision is inadvisable. If there is a reasonable probability these patterns will be needed, build Event Sourcing now.

### Use Case 4: Scalability via Event-Driven Architecture
Event Sourcing pairs naturally with event-driven architecture for horizontal scalability:

- **Reads (many)**: A cluster of in-memory reader nodes, each maintaining their own materialized view, kept in sync by consuming the event stream
- **Writes (few)**: Routed to a single master (or tight cluster) that appends to the event log and broadcasts events to readers

The event log is **append-only**, requiring minimal locking and enabling very high write throughput.

**Trade-off acknowledged:** Reader nodes will be temporarily out of sync with the master due to event propagation latency. This is **eventual consistency** — acceptable for most read-heavy workloads, but requires careful design for scenarios requiring strong consistency.

**Additional scalability benefit:** New applications can tap into the event stream and build their own projection/models without modifying the source system. This is a natural fit for **messaging-based integration** and **CQRS** (Command Query Responsibility Segregation).

---

## 4. Code Examples — C# Walkthroughs

### 4.1 Tracking Ships (Basic)

**Domain:** Ships carry cargo, move between ports. Four event types: `ArrivalEvent`, `DepartureEvent`, `LoadEvent`, `UnloadEvent`.

**Key design choices shown:**

1. **Event Processor as the central coordinator**
   ```csharp
   class EventProcessor {
       IList log = new ArrayList();
       public void Process(DomainEvent e) {
           e.Process();
           log.Add(e);
       }
   }
   ```
   Simple: call `Process()` on the event, then append to log. The log is the event store.

2. **Events delegate to domain objects** (separation of selection and domain logic)
   ```csharp
   // Selection logic in the event
   class DepartureEvent {
       internal override void Process() {
           Ship.HandleDeparture(this); // delegate to domain object
       }
   }
   
   // Domain logic in the domain object
   class Ship {
       public void HandleDeparture(DepartureEvent ev) {
           Port = Port.AT_SEA; // domain rule: departing ship is at sea
       }
   }
   ```

3. **Passing the full event vs. specific data** — The event is passed to `HandleDeparture`, not just the individual fields. This means:
   - Domain objects don't need to change signatures when events gain new fields
   - Domain objects become aware of the event type (a coupling tradeoff)

4. **Domain logic propagates through object graph** — `ArrivalEvent` notifies the `Ship`, which notifies all cargo aboard, allowing cargo to track Canada visits without the event having to know about cargo at all

**Analysis:** This structure keeps events thin (data + dispatch) and domain objects rich (business rules). Adding a new rule (e.g., "mark cargo if it passes through a port with customs delay") requires only modifying the relevant domain object — not the event class.

---

### 4.2 Updating an External System

**Scenario:** Ships arriving at port must notify customs. But notification must NOT fire during event replay.

**Solution:** The gateway checks the EventProcessor's active flag:

```csharp
class EventProcessor {
    public void Process(DomainEvent e) {
        isActive = true;   // enter live mode
        e.Process();
        isActive = false;  // exit live mode
        log.Add(e);
    }
}

class CustomsEventGateway {
    public void Notify(DateTime arrivalDate, Ship ship, Port port) {
        if (processor.isActive)         // only act in live mode
            SendToCustoms(...);
    }
}
```

**Key principle:** Domain logic calls `Gateway.Send()` unconditionally. The **gateway** owns the decision about whether to actually forward the call. This preserves domain logic purity and centralizes cross-cutting concerns (replay mode awareness) in the infrastructure layer.

**Analysis:** This is the same principle as a feature flag — the domain doesn't know or care; the infrastructure makes the routing decision. The boolean `isActive` flag is the simplest possible implementation. More sophisticated implementations might use a dedicated replay context object (similar to a Spring `TransactionStatus`) that gateways can inspect.

---

### 4.3 Reversing an Event

**Scenario:** Reverse a `LoadEvent` that loaded cargo onto a ship.

**Key implementation:** The event accumulates prior state during `Process()` so that `Reverse()` has what it needs:

```csharp
class LoadEvent {
    internal Port priorPort;  // prior state stored ON the event

    internal override void Process() {
        Cargo.HandleLoad(this);
    }
    internal override void Reverse() {
        Cargo.ReverseLoad(this);
    }
}

class Cargo {
    internal void HandleLoad(LoadEvent ev) {
        ev.priorPort = _port;  // cargo stores its prior location on the event
        _port = null;
        _ship = ev.Ship;
    }
    
    public void ReverseLoad(LoadEvent ev) {
        _ship.ReverseLoad(ev);
        _ship = null;
        _port = ev.priorPort;  // restore from event
    }
}
```

**Multi-object prior state:** When an arrival event affects multiple cargo items, a dictionary is used:

```csharp
class ArrivalEvent {
    internal IDictionary priorCargoInCanada = new Hashtable();
}

class Cargo {
    public void HandleArrival(ArrivalEvent ev) {
        ev.priorCargoInCanada[this] = _hasBeenInCanada; // keyed by cargo instance
        if ("CA" == ev.Port.Country) 
            _hasBeenInCanada = true;
    }
    public void ReverseArrival(ArrivalEvent ev) {
        _hasBeenInCanada = (bool) ev.priorCargoInCanada[this];
    }
}
```

**Analysis:** This pattern works beautifully when event source data is rich (e.g., if the event already includes the prior port, no extra storage is needed). The richer you make your event source data, the less prior state you need to accumulate. This is an argument for capturing **more context in events at creation time** — a principle aligned with GDPR-aware event design where you want events to be self-contained and not rely on external lookups for processing.

---

### 4.4 External Query with Logged Gateway

**Scenario:** When cargo arrives at a port, its value is fetched from an external pricing service. Replays must return the same value as the original processing.

**Solution:** A `LoggedPricingGateway` wraps the real gateway, caching responses keyed by the domain event being processed:

```csharp
class LoggedPricingGateway {
    public Money GetPrice(Cargo cargo) {
        GetPriceRequest oldReq = oldRequest(cargo);
        if (null != oldReq) return (Money) oldReq.Result;  // cache hit
        else return newRequest(cargo);                       // live call
    }
    
    private Money newRequest(Cargo cargo) {
        GetPriceRequest request = new GetPriceRequest(cargo);
        request.Result = gateway.GetPrice(cargo);  // real external call
        log.Store(request);                         // persist response
        return (Money) request.Result;
    }
    
    private GetPriceRequest oldRequest(Cargo cargo) {
        // Find cached response for the current domain event + cargo
        IList candidates = log.FindBy(EventProcessor.CurrentEvent, typeof(GetPriceRequest));
        foreach (GetPriceRequest request in candidates) {
            if (request.Cargo.RegistrationCode == cargo.RegistrationCode)
                return request;
        }
        return null;
    }
}
```

**`QueryEvent` base class** — every logged query stores a reference to the domain event that triggered it, enabling accurate historical lookup:

```csharp
class QueryEvent {
    DomainEvent _eventBeingProcessed;
    public QueryEvent() {
        _eventBeingProcessed = Registry.EventProcessor.CurrentEvent;
    }
}
```

**Critical operational implication:** The query log must be **persisted** with the same durability guarantees as the event log. It is part of the system of record.

**Analysis:** The elegance here is that the caching is **transparent to the domain model** — `Cargo.HandleArrival` just calls `Registry.PricingGateway.GetPrice(this)` with no awareness of whether it's live or replay. The infrastructure layer (logged gateway) handles all the complexity. This is a perfect application of the Decorator pattern: the `LoggedPricingGateway` decorates the real gateway with caching behavior.

---

## 5. Key Patterns Referenced

| Pattern | Role in Event Sourcing |
|---|---|
| **Domain Event** | The fundamental building block — immutable record of something that happened |
| **Transaction Script** | Simple event handler approach; all logic in one place; suitable for low complexity |
| **Domain Model** | Rich object model for handling events; better for complex domains |
| **Gateway** | Wraps external systems; enables replay-mode awareness; mandatory for correctness |
| **Temporal Property** | Manages logic that changes over time (`chargingRules.get(aDate)`) |
| **Retroactive Event** | Inserting/modifying past events and propagating consequences |
| **Parallel Model** | Running multiple model versions against the same event log simultaneously |
| **Audit Log** | Natural output of Event Sourcing; complete, executable history |
| **Special Case** | e.g., `Port.AT_SEA` as a null object for ships between ports |
| **Data Transfer Object (DTO)** | When event objects must be auto-serialized; requires separate selection logic |
| **Agreement Dispatcher** | Dispatches processing based on temporal rules — companion to Temporal Property |

---

## 6. Critical Trade-offs Summary

### Advantages

| Advantage | Depth |
|---|---|
| **Complete audit trail** | Every state change is recorded with causal data; fully executable |
| **Temporal queries** | Query any past state without additional infrastructure |
| **Deterministic bug fixing** | Fix code, replay log — corrected state materializes automatically |
| **Parallel testing** | Replay production event log against new code before deploying |
| **Scalability** | Append-only log + read replicas = excellent horizontal scaling |
| **Integration flexibility** | New subscribers can tap the event stream without modifying the source |

### Disadvantages / Complexities

| Complexity | Mitigation |
|---|---|
| **Unfamiliar interface style** | Team training; start with bounded contexts where audit is most valuable |
| **External update side effects on replay** | Gateway pattern with replay-mode flag |
| **External query non-determinism on replay** | Logged gateway; temporal query support in external APIs |
| **Combined external interactions** | Idempotency keys; CQS design; logged gateway |
| **Temporal logic in code** | Temporal Property pattern + Strategy objects; avoid if possible |
| **Event schema evolution** | Upcasters (transform old event formats to new); versioned event types |
| **Query performance (current state)** | Snapshots + CQRS projections (materialized views) |
| **Storage growth** | Event logs grow forever; archival strategy needed for old events |

### When NOT to use Event Sourcing

- Simple CRUD applications with no audit, replay, or temporal query needs
- Systems where external side effects dominate and cannot be made idempotent
- Teams not yet comfortable with eventual consistency for read models
- Systems requiring complex current-state queries — Event Sourcing alone is a poor fit; must be combined with CQRS projections

### The irreversibility warning

Fowler is explicit: **Event Sourcing is hard to retrofit**. If you think you might need Parallel Models or Retroactive Events in the future, build Event Sourcing now. This is an architectural decision that becomes increasingly expensive to add later, unlike most refactoring patterns.

---

> *"Event Sourcing ensures that all changes to application state are stored as a sequence of events. Not just can we query these events, we can also use the event log to reconstruct past states, and as a foundation to automatically adjust the state to cope with retroactive changes."*  
> — Martin Fowler, 2005
