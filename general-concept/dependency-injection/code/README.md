# Dependency Injection — MVP Code

The smallest runnable demo of Dependency Injection. About 90 lines of actual code, comments excluded.

## What it demonstrates
- **Constructor injection + Pure DI:** `OrderService(repo, clock)` lists its needs, and a hand-written composition root passes a `FixedClock` in (see `../docs/01-overview.md`).
- **A container automates the same wiring:** a small `Container` reads `__init__` type hints, resolves depth-first, and detects cycles (`../docs/02-deep-dive.md` § How, steps 1–4).
- **Lifetimes:** singleton `Clock` shared by every request, scoped `UnitOfWork` shared within one request, transient `OrderService` built on every resolve.
- **Captive dependency:** a singleton `OrderService` keeps request A's `UnitOfWork`, so request B writes into it. With `validate=True` the container rejects that graph.

## Prerequisites
- Python 3.11+, standard library only (nothing to `pip install`).

## Run it

```bash
python mvp.py
```

## Expected output

```
1) Pure DI: wired by hand, no container
   placed book@09:30 with a FixedClock
2) Container: same graph, auto-wired from type hints
   request A: new OrderService each resolve (transient): True
   request A: both share UnitOfWork #2 and #2 (scoped)
   request B: gets its own UnitOfWork #3
   Clock shared across requests (singleton): True
3) Captive dependency: OrderService registered as singleton
   validate=False -> request B wrote into UnitOfWork #4: ['pen@09:30', 'ink@09:30']
   validate=True  -> RuntimeError: captive dependency: singleton OrderService -> scoped OrderRepo
```

## What to try next
- In `compose()`, register `Clock()` instead of `FROZEN`. The times become real and no consumer class changes.
- Comment out the `UnitOfWork` registration. The `KeyError` appears on the first resolve, not in `compose()`, which is why graphs need validating before traffic arrives.
- Make `UnitOfWork` `"transient"` and watch validation reject the scoped `OrderRepo` that would hold it.
- Make `UnitOfWork` `"singleton"`. Validation passes because the direction is legal, yet request B now shares UnitOfWork #2. The check enforces lifetimes; it cannot tell you the lifetime you picked is wrong.
