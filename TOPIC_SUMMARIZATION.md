# Backpressure

Backpressure is the mechanism a slow consumer uses to tell a fast producer to ease up, so a pipeline of producers and consumers bends instead of breaking. Whenever one side of a system produces data and another side processes it, the two almost never run at the same speed — backpressure is the family of techniques that handles that mismatch on purpose, instead of letting memory balloon, latency spike, or the whole process crash.

Engineers reach for it whenever data flows between components that work at different rates: streaming services to downstream APIs, Kafka topics to consumers, file ingestion into a database, message queues into batch ML inference. Without it, fast producers silently overwhelm slow consumers and the failure mode is ugly — out-of-memory kills, hours of Kafka consumer lag, or mysterious latency spikes from a buffer that grew without a ceiling. With it, the system can choose one of four honest responses when capacity tightens: buffer up to a bounded limit, drop the overflow, block the producer, or signal upstream to slow down. Picking the right response is the design decision; admitting that the mismatch exists is the engineering one.

A useful way to picture it: imagine a busy restaurant kitchen on a Friday night. Waiters keep firing tickets at the line cook, but the cook can only plate so many dishes a minute. The kitchen can clip new tickets to a rail (buffer), toss them in the bin (drop), tell the waiters to stop taking orders (block), or wave them off with "bring me three at a time, not thirty" (signal slowdown). Every backpressure-aware system is doing one of those four things — the question is whether the engineer chose, or whether the system chose for them.

---

Full notes: https://github.com/Quang-Dobe/Practice.Concept/tree/main/general-concept/backpressure/
