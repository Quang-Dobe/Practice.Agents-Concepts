"""
Minimal Byte Pair Encoding (BPE) tokenizer, trained from scratch.

This is the actual algorithm behind GPT-4's tiktoken and Llama 3 — stripped of
pre-tokenization regexes, byte-level shimming, and special tokens, so you can
see the merge loop with no decoration. Train on a tiny corpus, learn N merges,
then encode and decode a sentence.

Reads end-to-end in ~80 lines; everything load-bearing for "what BPE does" is
here. Everything else (the regex pre-tokenizer, special tokens, byte-level
fallback) is library polish on top of this core.
"""

from collections import Counter

# A tiny corpus. BPE is a statistical compressor, so the merges it learns
# reflect the corpus — that is the whole point. With this corpus, "low" and
# "newest" both contain frequent letter pairs that will get merged early.
CORPUS = [
    "low", "low", "low", "low", "low",
    "lower", "lower",
    "newest", "newest", "newest", "newest", "newest", "newest",
    "widest", "widest", "widest",
]

NUM_MERGES = 10  # target vocabulary growth on top of the base alphabet


def get_pair_counts(splits: dict[str, list[str]], freqs: dict[str, int]) -> Counter:
    """Count every adjacent symbol pair across the corpus, weighted by word frequency.

    `splits[word]` is the current segmentation of `word` into tokens. Early on
    that is one-character-per-token; after merges it gets coarser. We weight by
    word frequency so common words pull merges in their direction.
    """
    pairs: Counter = Counter()
    for word, symbols in splits.items():
        for a, b in zip(symbols, symbols[1:]):
            pairs[(a, b)] += freqs[word]
    return pairs


def apply_merge(pair: tuple[str, str], splits: dict[str, list[str]]) -> dict[str, list[str]]:
    """Replace every occurrence of `pair` in every word's segmentation with the merged token.

    This is the "rewrite" step. After it runs, the next pair-count pass will
    see the new merged symbol as a single unit and may merge IT with a neighbor.
    """
    a, b = pair
    merged = a + b
    new_splits = {}
    for word, symbols in splits.items():
        out, i = [], 0
        while i < len(symbols):
            if i < len(symbols) - 1 and symbols[i] == a and symbols[i + 1] == b:
                out.append(merged)
                i += 2  # skip both — they have been consumed
            else:
                out.append(symbols[i])
                i += 1
        new_splits[word] = out
    return new_splits


# --- TRAIN ----------------------------------------------------------------

# Word frequencies. In a real BPE trainer you would first apply a pre-tokenizer
# regex to split on whitespace and punctuation; here the corpus is already words.
freqs = Counter(CORPUS)

# Initial segmentation: one character per token. The character set is our base
# alphabet — the units that can never disappear, so encoding is always possible.
splits = {word: list(word) for word in freqs}
vocab = {ch for word in freqs for ch in word}
merges: list[tuple[str, str]] = []  # ORDERED — replay at encode time uses this order

for step in range(NUM_MERGES):
    pairs = get_pair_counts(splits, freqs)
    if not pairs:
        break
    # Greedy: pick the single most frequent adjacent pair. This is what makes
    # BPE "byte pair encoding" — it is literally Gage's 1994 compression trick.
    best = max(pairs, key=pairs.get)
    merges.append(best)
    vocab.add(best[0] + best[1])
    splits = apply_merge(best, splits)
    print(f"merge {step + 1:2d}: {best!r:>20}  freq={pairs[best]}  -> {best[0] + best[1]!r}")

print(f"\nfinal vocab ({len(vocab)} tokens): {sorted(vocab)}")


# --- ENCODE / DECODE ------------------------------------------------------

def encode(word: str) -> list[str]:
    """Tokenize a new word by REPLAYING the learned merges, in the order they were learned.

    Replaying in learned order (not greedy-longest-match) is what makes a BPE
    encode deterministic and bit-identical to training-time segmentation.
    """
    symbols = list(word)
    for a, b in merges:  # walk the merge table in order
        out, i = [], 0
        while i < len(symbols):
            if i < len(symbols) - 1 and symbols[i] == a and symbols[i + 1] == b:
                out.append(a + b)
                i += 2
            else:
                out.append(symbols[i])
                i += 1
        symbols = out
    return symbols


def decode(tokens: list[str]) -> str:
    """Inverse map: BPE tokens are byte strings, so decoding is just concatenation."""
    return "".join(tokens)


for word in ["lowest", "newer", "widest", "lowing"]:
    toks = encode(word)
    # "lowest" was never in the corpus, but BPE composes it from learned pieces
    # plus single-character fallbacks. That is the no-OOV property in miniature.
    print(f"encode({word!r:>9}) -> {toks}   decode -> {decode(toks)!r}")
