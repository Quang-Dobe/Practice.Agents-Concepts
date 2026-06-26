# Tokenization — Overview

> Tokenization is how a language model turns raw text into the small, numbered chunks ("tokens") it actually does math on — and the choice of chunking algorithm quietly shapes cost, context length, and how well the model handles weird inputs.

## The 30-second version

A neural network cannot read the string `"unhappiness"`. It can only multiply numbers. Tokenization is the deterministic step that sits between your text and the model: it splits the string into a sequence of subword pieces (say `["un", "happiness"]`), looks each piece up in a fixed vocabulary, and hands the model a list of integers. Every modern LLM — GPT-4, Llama 3, Gemma, Claude — runs this step before any "intelligence" happens. Engineers care because tokens are the unit of billing, the unit of context windows, and the source of a surprising number of bugs (bad math on numbers, broken non-English text, weird behavior on emoji).

## The mental model

Think of a tokenizer as a **phrasebook for a tourist who only knows a fixed list of syllables**. You can say anything you want, but you have to assemble it from the syllables in the book. Common things like `" the"`, `" of"`, or `"ing"` get their own entry — one lookup, done. Rare words like `"antidisestablishmentarianism"` don't appear in the book, so the tourist stitches them together from smaller pieces: `"anti"` + `"disestablish"` + `"ment"` + `"arian"` + `"ism"`. A truly unknown sequence — an obscure emoji, a Cyrillic name — falls back to raw bytes, the most granular page in the book.

The phrasebook is built once, before training, by reading a giant corpus and asking: *"Which pairs of characters keep showing up together? Promote them to single entries."* That greedy merging is the heart of **Byte Pair Encoding (BPE)**, the algorithm behind GPT-4's `tiktoken` and Llama 3. Other algorithms — **WordPiece** (BERT), **Unigram** (Gemma, T5) — build the same kind of phrasebook with different statistical recipes, but the user-facing behavior is nearly identical: frequent stuff is one token, rare stuff is many.

## What it is NOT

- Not **word splitting**. `"tokenizing"` is three tokens in GPT-4, not one.
- Not **embeddings**. Tokenization produces integer IDs; embeddings turn those IDs into vectors. Different step.
- Not **parsing**. The tokenizer has no idea what a noun or a function call is. It is pure string statistics.
- Not **language-specific**. Modern tokenizers (SentencePiece, byte-level BPE) operate on raw bytes, so they handle Japanese, code, and emoji with the same machinery.

## When you would reach for it

- Estimating cost or context usage before sending a prompt to an API.
- Debugging why a model miscounts characters, fails at arithmetic, or mangles a non-English phrase.
- Training or fine-tuning a model and choosing (or extending) a vocabulary.
- Designing a RAG pipeline where chunk size is measured in tokens, not characters.

## When you would NOT reach for it

- You just need to count words for a UI — use a word splitter, not a tokenizer.
- You want semantic similarity — that is embeddings, downstream of tokenization.
- You are doing classical string processing (search, regex, diff) — tokens are the wrong abstraction.

## Key vocabulary (just enough to keep reading)

- **Token**: one entry in the vocabulary; the atomic unit the model sees.
- **Vocabulary**: the fixed list of all possible tokens, usually 30k–256k entries.
- **Subword**: a token smaller than a word but larger than a character — the sweet spot.
- **BPE (Byte Pair Encoding)**: greedy merge-based algorithm; the de facto standard.
- **WordPiece**: BERT's variant; merges by likelihood gain instead of raw frequency.
- **Unigram**: top-down algorithm that starts huge and prunes; used by SentencePiece and Gemma.
- **SentencePiece**: a library/framework, not an algorithm — wraps BPE or Unigram, treats input as raw bytes.
- **Byte-level**: tokenizer operates on UTF-8 bytes, so nothing is ever truly "unknown".
- **Special tokens**: reserved IDs like `<|endoftext|>` or `<|im_start|>` that carry structural meaning.

## What's next

The next document (`02-deep-dive.md`) answers the What / Where / When / How / Why of tokenization in detail: how the BPE merge table is actually built, where WordPiece and Unigram diverge, why byte-level fallback matters, and how all of this interacts with context windows, multilingual performance, and the infamous "LLMs can't count letters" problem.
