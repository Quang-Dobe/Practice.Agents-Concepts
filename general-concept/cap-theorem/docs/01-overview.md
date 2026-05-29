# CAP Theorem — Overview

> In a distributed data system, when the network breaks between your nodes, you have to choose: refuse some requests to keep data correct, or answer every request and accept that some answers will be stale.

## The 30-second version

CAP is a result about distributed databases. It says that during a network partition — when two parts of your cluster can no longer talk to each other — you cannot simultaneously give every client the latest correct data *and* keep every node responsive. You pick one. The theorem matters because partitions are not a rare edge case in real networks; they are a regular Tuesday. So every distributed system designer is implicitly making this trade-off, whether they realize it or not.

## The mental model

Imagine a bank with two branches, Hanoi and Saigon, that share a single account ledger by phoning each other after every transaction. Now the phone line cuts out.

A customer walks into the Saigon branch and asks to withdraw $500. The teller has two options:

- **Refuse the withdrawal** until the phone line is back. The ledger across both branches stays perfectly correct — no chance of accidentally letting the customer also withdraw $500 in Hanoi — but the customer leaves angry. This is **CP**: you chose consistency over availability.
- **Allow the withdrawal** and write it down locally, planning to sync up later. The customer is happy, but for a while the two branches disagree about the balance, and you might discover an overdraft when the line comes back. This is **AP**: you chose availability over consistency.

There is no third door where the teller magically knows what is happening in Hanoi without a working phone. That third door is the "CA" option, and it does not exist the moment you accept that phone lines can drop.

## What it is NOT

- Not a claim that you must permanently pick two of three. The trade-off only bites *during* a partition. In normal operation you usually get all three.
- Not the same as ACID. ACID is about transactions on one database; CAP is about behavior across many nodes when the network misbehaves.
- Not a complete picture of distributed trade-offs. PACELC extends it to cover latency-vs-consistency choices even when the network is healthy — that comes later.

## When you would reach for it

- You are choosing a database for a multi-region service and need to reason about what happens when regions get isolated.
- You are designing a system where stale reads are dangerous (payments, inventory, leader election) and need to argue for CP behavior.
- You are designing a system where downtime is unacceptable (shopping carts, social feeds, DNS) and need to argue for AP behavior.

## When you would NOT reach for it

- You are running a single-node Postgres on one machine. No partitions, no CAP.
- You are picking between SQL and NoSQL — CAP does not map cleanly onto that axis; plenty of NoSQL stores are CP and plenty of SQL stores can be AP-tuned.
- You want a precise consistency model for a specific operation. CAP is too coarse; you want linearizability, causal consistency, eventual consistency, etc.

## Key vocabulary (just enough to keep reading)

- **Consistency (C)**: every read returns the most recent write — formally, *linearizability*.
- **Availability (A)**: every non-failing node returns a non-error response to every request.
- **Partition tolerance (P)**: the system keeps operating even when messages between nodes are dropped or delayed.
- **Partition**: a network split that prevents some nodes from reaching others.
- **CP system**: sacrifices availability during a partition to stay consistent (e.g. ZooKeeper, etcd).
- **AP system**: sacrifices consistency during a partition to stay available (e.g. Cassandra, DynamoDB in default mode).
- **Tunable consistency**: real systems often let you pick C-vs-A per request, not once at install time.

## What's next

The next document answers What / Where / When / How / Why in detail — including why "CA" is a myth once partitions are on the table, how quorum reads and writes actually implement the trade-off, and where PACELC picks up where CAP leaves off.
