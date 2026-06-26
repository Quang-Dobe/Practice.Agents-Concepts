# Tokenization — MVP Code

The smallest runnable demo of Byte Pair Encoding (BPE). About 70 lines of actual code, comments excluded.

## What it demonstrates

- The **BPE training loop**: count adjacent pairs, merge the most frequent, repeat — Sennrich et al. 2016 in raw form (`02-deep-dive.md` §How → Training).
- The **merge table as an ordered artifact**: replaying merges at encode time in learned order is what makes BPE deterministic (`02-deep-dive.md` §How → Encoding).
- **Subword composition for unseen words**: `"lowest"` was never in the corpus but tokenizes cleanly into learned pieces — the no-OOV intuition (`01-overview.md` §The mental model).
- The **vocabulary as base alphabet + merges**: every final token is either a base character or a recorded merge of two earlier tokens.

## Prerequisites

Python 3.11+. No external dependencies — only the standard library (`collections.Counter`).

## Run it

```bash
python /home/user/Practice.Concept/ai/tokenization/code/mvp.py
```

## Expected output

```
merge  1:           ('e', 's')  freq=9  -> 'es'
merge  2:          ('es', 't')  freq=9  -> 'est'
merge  3:           ('l', 'o')  freq=7  -> 'lo'
merge  4:          ('lo', 'w')  freq=7  -> 'low'
merge  5:           ('n', 'e')  freq=6  -> 'ne'
merge  6:          ('ne', 'w')  freq=6  -> 'new'
merge  7:       ('new', 'est')  freq=6  -> 'newest'
...
encode( 'lowest') -> ['low', 'est']   decode -> 'lowest'
encode(  'newer') -> ['new', 'e', 'r']   decode -> 'newer'
encode( 'widest') -> ['widest']   decode -> 'widest'
encode( 'lowing') -> ['low', 'i', 'n', 'g']   decode -> 'lowing'
```

Note: `lowest` was never in the training corpus but tokenizes cleanly into `["low", "est"]` — that is the BPE-composes-unseen-words property in miniature.

## What to try next

- Bump `NUM_MERGES` to 20 and watch `newest` collapse to one token.
- Add `"slow"` to `CORPUS` ten times and see `slow` win a merge over `widest`.
- Encode a word with an unseen character (e.g. `"low!"`) — the `!` survives as its own token because it is in the base alphabet via encoding.
- Replace the greedy `max(pairs, key=pairs.get)` with the *least* frequent pair and observe how the learned merges become useless.
