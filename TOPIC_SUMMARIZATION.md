# Sharding

Sharding is splitting one logical database into many physical databases by a key, so each machine only owns a slice of the data. Every row keeps the same schema, but a routing rule — usually a hash of `user_id` or a range of dates — decides which shard a given row lives on. You scale by adding more machines instead of buying a bigger one.

Engineers reach for it when a single primary node can no longer absorb the write rate, when the hot working set stops fitting in RAM, when data residency demands EU users on EU shards, or when a multi-tenant SaaS naturally isolates tenants. It is not the right tool for a database that fits on one box and is only missing an index, for workloads dominated by big cross-entity joins, or for under 100 GB of hot data where the operational cost of resharding, cross-shard queries, and distributed transactions outweighs the throughput win. Sharding is also not replication, not vertical partitioning, and not the same as a single-server table partition.

A useful picture: one giant filing cabinet works until the office has ten thousand employees. So you buy ten smaller cabinets, write a rule that says "last name A–C goes to cabinet 1, D–F to cabinet 2," and put a receptionist at the door who knows the rule. Anyone looking up a folder walks straight to the right cabinet. The rule is the shard key, the cabinets are shards, the receptionist is the router — and the whole game is picking a key where folders end up spread evenly and most lookups only touch one cabinet.

---

Full notes: https://github.com/Quang-Dobe/Practice.Concept/tree/main/database/sharding/
