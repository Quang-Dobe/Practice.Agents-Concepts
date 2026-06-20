namespace Domain;

// Plain entity. The Id is our shard key — it is immutable, high-cardinality,
// and (assuming Guid.NewGuid) well-distributed. Those are the three properties
// 03-practice.md insists on for a sane shard key choice.
public sealed class User(Guid id, string name)
{
    public Guid Id { get; } = id;
    public string Name { get; } = name;
}
