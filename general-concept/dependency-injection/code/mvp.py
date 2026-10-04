"""
Dependency Injection: smallest provable demo (Python 3.11+, stdlib only).

Proves three ideas from ../docs/02-deep-dive.md:
  1. Pure DI: a class lists its needs in __init__, and a composition root passes them in.
  2. A container automates that same wiring. It reads constructor type hints,
     resolves them recursively, and caches each instance by lifetime (singleton / scoped / transient).
  3. The lifetime invariant: a singleton that captures a scoped object leaks one
     request's state into the next (a "captive dependency") unless the container validates.
"""
import itertools
from datetime import datetime, timezone


# ------------------------------------------------------------------ services
class Clock:
    """Real system clock. Consumers depend on this type and never call datetime.now() themselves."""
    def now(self) -> datetime:
        return datetime.now(timezone.utc)


class FixedClock(Clock):
    """Deterministic substitute. Being able to swap this in is the whole reason to inject a clock."""
    def __init__(self, at: datetime):
        self.at = at

    def now(self) -> datetime:
        return self.at


_uow_ids = itertools.count(1)


class UnitOfWork:
    """Per-request state (think: a DB transaction). It must never outlive its request."""
    def __init__(self):
        self.id = next(_uow_ids)
        self.pending: list[str] = []


class OrderRepo:
    def __init__(self, uow: UnitOfWork):
        self.uow = uow

    def add(self, order: str) -> None:
        self.uow.pending.append(order)


class OrderService:
    # Constructor injection: this signature IS the dependency list. No `OrderRepo()` call, no lookup.
    def __init__(self, repo: OrderRepo, clock: Clock):
        self.repo, self.clock = repo, clock

    def place(self, item: str) -> str:
        order = f"{item}@{self.clock.now():%H:%M}"
        self.repo.add(order)
        return order


# ------------------------------------------------------------------ a 30-line container
# A component may depend only on components that live at least as long as it does.
LONGEVITY = {"transient": 0, "scoped": 1, "singleton": 2}


class Container:
    def __init__(self, validate: bool = True):
        self.registry: dict[type, tuple[object, str]] = {}  # service key -> (recipe, lifetime)
        self.singletons: dict[type, object] = {}            # root cache: lives until shutdown
        self.validate = validate                             # plays the role of .NET's ValidateScopes

    def register(self, key: type, recipe: object = None, lifetime: str = "transient") -> None:
        self.registry[key] = (recipe or key, lifetime)       # re-registering a key: last one wins

    def scope(self) -> "Scope":
        return Scope(self)


class Scope:
    """One per request. Scoped instances are cached here; singletons come from the root."""
    def __init__(self, root: Container):
        self.root, self.cache, self.resolving = root, {}, []

    def resolve(self, key: type, consumer: str = "transient") -> object:
        if key in self.resolving:  # depth-first walk + "currently resolving" stack = cycle detection
            raise RuntimeError(f"circular dependency: {[k.__name__ for k in self.resolving + [key]]}")
        recipe, life = self.root.registry[key]  # KeyError here = missing registration
        if self.root.validate and LONGEVITY[life] < LONGEVITY[consumer]:
            raise RuntimeError(f"captive dependency: {consumer} {self.resolving[-1].__name__} "
                               f"-> {life} {key.__name__}")
        cache = {"singleton": self.root.singletons, "scoped": self.cache}.get(life)  # transient: None
        if cache is not None and key in cache:
            return cache[key]
        if isinstance(recipe, type):  # a class: read its constructor's type hints and recurse
            self.resolving.append(key)
            hints = getattr(recipe.__init__, "__annotations__", {})
            deps = {name: self.resolve(dep, life) for name, dep in hints.items() if name != "return"}
            obj = recipe(**deps)
            self.resolving.pop()
        else:                         # a pre-built instance
            obj = recipe
        if cache is not None:
            cache[key] = obj
        return obj


# ------------------------------------------------------------------ composition root
FROZEN = FixedClock(datetime(2026, 1, 1, 9, 30, tzinfo=timezone.utc))


def compose(service_life: str = "transient", validate: bool = True) -> Container:
    """The ONE place that picks implementations and lifetimes. Consumers never see this."""
    c = Container(validate)
    c.register(Clock, FROZEN, "singleton")  # production: c.register(Clock, Clock(), "singleton")
    c.register(UnitOfWork, lifetime="scoped")
    c.register(OrderRepo, lifetime="scoped")
    c.register(OrderService, lifetime=service_life)
    return c


if __name__ == "__main__":
    print("1) Pure DI: wired by hand, no container")
    svc = OrderService(OrderRepo(UnitOfWork()), FROZEN)  # this line is the whole composition root
    print(f"   placed {svc.place('book')} with a FixedClock")

    print("2) Container: same graph, auto-wired from type hints")
    c = compose()
    req_a = c.scope()
    s1, s2 = req_a.resolve(OrderService), req_a.resolve(OrderService)
    s3 = c.scope().resolve(OrderService)
    print(f"   request A: new OrderService each resolve (transient): {s1 is not s2}")
    print(f"   request A: both share UnitOfWork #{s1.repo.uow.id} and #{s2.repo.uow.id} (scoped)")
    print(f"   request B: gets its own UnitOfWork #{s3.repo.uow.id}")
    print(f"   Clock shared across requests (singleton): {s1.clock is s3.clock}")

    print("3) Captive dependency: OrderService registered as singleton")
    c = compose("singleton", validate=False)
    c.scope().resolve(OrderService).place("pen")  # request A
    svc_b = c.scope().resolve(OrderService)        # request B receives A's cached singleton...
    svc_b.place("ink")                             # ...so it writes into A's unit of work
    print(f"   validate=False -> request B wrote into UnitOfWork #{svc_b.repo.uow.id}: {svc_b.repo.uow.pending}")
    try:
        compose("singleton", validate=True).scope().resolve(OrderService)
    except RuntimeError as e:  # the failure mode IS the lesson here, so catch and show it
        print(f"   validate=True  -> RuntimeError: {e}")
