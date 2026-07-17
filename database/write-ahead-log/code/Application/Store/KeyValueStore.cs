namespace Application.Store;

// The "in-memory state" the WAL is protecting. Real engines call this the
// buffer pool; here it is just a dictionary. Nothing writes to it except
// through the mediator, so the WAL-before-state invariant is enforceable
// in one place (the command handlers below).
public sealed class KeyValueStore
{
    private readonly Dictionary<string, string> _state = new();

    public void Set(string key, string value) => _state[key] = value;
    public void Remove(string key) => _state.Remove(key);
    public string? Get(string key) => _state.TryGetValue(key, out var v) ? v : null;

    // Snapshot for the demo's "print final state" step. Not part of the
    // durability story - a real engine would never expose the whole map.
    public IReadOnlyDictionary<string, string> Snapshot() => _state;
}
