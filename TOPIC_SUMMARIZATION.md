# Dependency Injection

Dependency Injection means a piece of code receives the things it needs, such as a database, an HTTP client, a clock, or a logger, from outside instead of creating or looking them up itself. The class declares what it needs, usually as constructor parameters, and whoever creates the class passes those in. It is a technique, not a framework: passing arguments by hand already counts.

It matters because it lets you test code without real infrastructure and swap implementations in one place. Engineers reach for it when unit tests should run without a real database or network, when the right implementation depends on the environment, when several objects should share one expensive instance like a connection pool, or when they want to see everything a class needs just by reading its constructor. Once hand-wiring in the entry point becomes a chore, a DI container can automate it. It is not worth it for throwaway scripts or pure helpers with nothing to swap.

Picture a restaurant kitchen. One chef drives to a particular farm every morning for tomatoes, so the recipe is welded to that farm. Another chef posts a list at their station saying "tomatoes, flour, a stove", and the kitchen manager stocks it before service. That chef cooks with whatever arrives and never notices when the manager switches suppliers or brings cheap practice tomatoes on training day. The second chef is Dependency Injection, the list is the constructor signature, and the manager is the composition root, the one place at startup where real objects are built and wired together.

---

Full notes: https://quang-dobe.github.io/Practice.Agents-Concepts/general-concept/dependency-injection/present/index.html
