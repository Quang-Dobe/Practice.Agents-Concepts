# Tokenization — Deep Dive

> Builds on `01-overview.md`. Read that first.

## What

### Precise definition

Tokenization is the deterministic, lossless mapping from a Unicode string to a sequence of integer IDs drawn from a fixed vocabulary `V`, and back. Modern LLM tokenizers implement this in two stages: a **pre-tokenizer** that splits the raw string into "word-like" chunks using a regex over Unicode categories, and a **subword model** that segments each chunk into vocabulary entries. The vocabulary is learned once on a training corpus and frozen; at inference time the encode pass is a pure function of the input bytes.

Formally, a subword tokenizer is a tuple `(V, M, P)` where `V` is the vocabulary (a set of byte strings), `M` is either an ordered merge table (BPE/WordPiece) or a probability distribution `p(t)` over `V` (Unigram), and `P` is the pre-tokenization regex.

### The core building blocks

- **Vocabulary `V`**: typically 32k (Llama 2), 100k (GPT-4 / `cl100k_base`), 128k (Llama 3, `cl100k`+), or 200k (GPT-4o / `o200k_base`). Each entry has a unique integer ID.
- **Base alphabet**: the smallest units that can never run out. In byte-level BPE this is the 256 UTF-8 byte values, so every possible input is representable.
- **Merge table** (BPE/WordPiece): an ordered list of pairs `(a, b) → ab`. Order is part of the model — replaying the merges in the wrong order changes the output.
- **Unigram probability table**: `Unigram` instead stores `log p(t)` for each `t ∈ V` and runs Viterbi to find the maximum-likelihood segmentation.
- **Pre-tokenization regex**: in GPT-2/GPT-4, a hand-written regex like `'(?:[sdmt]|ll|ve|re)| ?\p{L}+| ?\p{N}+| ?[^\s\p{L}\p{N}]+|\s+(?!\S)|\s+`. It guarantees merges never span a space-to-letter or letter-to-digit boundary.
- **Special tokens**: reserved IDs (`<|endoftext|>`, `<|im_start|>`, `<|eot_id|>`, `<|begin_of_text|>`) that are added after training and are never produced by merging — they only appear when explicitly inserted by the chat template.
- **Decoder**: the inverse map. For byte-level tokenizers it concatenates token byte strings and re-decodes UTF-8.

### How it relates to the broader landscape

Subword tokenizers sit between two extremes: **word-level** tokenizers (huge vocab, terrible OOV behavior, dead) and **character/byte-level** models (tiny vocab, very long sequences, expensive attention). Within the subword family there are three sibling algorithms — **BPE** (Sennrich et al., 2016), **WordPiece** (Schuster & Nakajima, 2012; revived by BERT), and **Unigram LM** (Kudo, 2018) — plus the **SentencePiece** library which is a packaging of BPE+Unigram that treats whitespace as a regular character (using the meta-symbol `▁`, U+2581). Recent research (Boundless BPE, BlockBPE) is pushing back on the pre-tokenizer step itself, but every production LLM in 2026 still uses one of those three algorithms.

## Where

### Where it runs / lives in the stack

Tokenization is the first computation in the inference pipeline and the last computation before output. It runs **on the host CPU**, not on the GPU — the encoded IDs are what gets copied to device memory. In a typical request the order is: `string → tokenizer.encode → int64[] → embedding lookup on GPU → transformer → unembedding → argmax/sampling → int64 → tokenizer.decode → string`. The same tokenizer instance is used at training time, fine-tuning time, and inference time, and a vocabulary mismatch between any two of those silently destroys quality.

### Where you typically encounter it

- **OpenAI `tiktoken`**: `cl100k_base` (GPT-4, GPT-3.5-turbo), `o200k_base` (GPT-4o, o-series). C++ core, Rust bindings, written for speed.
- **Hugging Face `tokenizers`**: Rust-backed, hosts BPE / WordPiece / Unigram variants for thousands of models.
- **SentencePiece**: Google's reference C++ implementation, used by T5, mT5, ALBERT, XLNet, Gemma, mBART.
- **Llama 3 / 3.1 / 3.2**: 128,256-entry vocabulary built with `tiktoken`-format BPE plus Meta's own special tokens.
- **BERT / DistilBERT**: WordPiece with `##` continuation prefix and `[UNK]`/`[CLS]`/`[SEP]`.
- **Anthropic Claude, Google Gemini**: proprietary BPE-family tokenizers; size and rules are not public.

### Ecosystem and tooling

- **For inspecting tokens**: `tiktokenizer.vercel.app`, OpenAI's tokenizer playground, Hugging Face `AutoTokenizer.encode(..., return_tensors=None)`.
- **For training a vocabulary**: `sentencepiece`, `tokenizers.trainers.BpeTrainer`, `tokenizers.trainers.UnigramTrainer`.
- **For counting tokens at runtime**: `tiktoken.encoding_for_model("gpt-4o")`, `tokenizer(text)["input_ids"]`.
- **For chat formatting**: `tokenizer.apply_chat_template(messages)` (Hugging Face) which injects the model's required special tokens.

## When

### When the topic emerged and why

Subword tokenization for neural models was kicked off by Sennrich, Haddow & Birch (2016, "Neural Machine Translation of Rare Words with Subword Units"), which adapted Gage's 1994 data-compression BPE to MT. The motivation was unsolvable OOV — fixed word vocabularies meant the system literally had no representation for a name it had not seen during training. WordPiece (originally Google Voice Search, 2012) and Unigram LM (Kudo, 2018) followed. GPT-2 (2019) introduced **byte-level BPE**, the trick that makes OOV impossible by reducing the base alphabet to 256 bytes. From then on every frontier LLM has used a byte-level BPE or SentencePiece-Unigram variant.

### When to use it in a project

Reach for an existing tokenizer when:

- You are calling any hosted LLM API — you must match what the model was trained on; you do not get a choice.
- You are fine-tuning a foundation model — extending the vocab is possible but rare; reuse the base tokenizer.
- You are building a RAG pipeline — measure chunk size in tokens (`len(enc.encode(chunk))`), not characters, because the context budget is in tokens.

Train your own tokenizer when:

- You are pre-training from scratch on a domain (code, biomedical, a low-resource language) where fertility on the public tokenizers is bad — typically >2 tokens/word on your evaluation set.
- You need a custom alphabet (protein sequences, MIDI events) where Unicode-based pre-tokenization is wrong.

### When NOT to use it

- You only need word counts for UI — `str.split()` is correct and cheaper.
- You want semantic similarity — that is sentence embeddings, an orthogonal model.
- You are reusing another model's tokenizer with a different model's weights — known as "tokenizer recycling", and reliably produces garbage.

## How

### How it works under the hood

#### Encoding a string (byte-level BPE, e.g. `cl100k_base`)

1. **Normalize**: pass the input through Unicode NFC (most modern tokenizers do not normalize aggressively — they want to round-trip).
2. **Pre-tokenize**: apply the regex. The input `" the cat sat"` becomes the chunks `[" the", " cat", " sat"]`. Note the leading spaces are attached to the word — `" cat"` and `"cat"` are different tokens.
3. **Encode to bytes**: each chunk becomes a UTF-8 byte sequence. Bytes 0–255 are mapped through GPT-2's reversible byte-to-printable-character table so the merge table can be written in printable Unicode.
4. **Greedy merge**: for each chunk, look at every adjacent pair of current tokens. If any pair appears in the merge table, replace the pair with its merged token. Use the merge with the **lowest rank** (earliest learned) first. Repeat until no applicable merge remains.
5. **Look up IDs**: each final token is mapped to its integer ID.
6. **Concatenate**: emit the flat list of IDs across all chunks.

The complexity is `O(n log n)` per chunk with a priority queue keyed on merge rank, and `O(n)` if you use the linked-list trick from `tiktoken`. For a 4k-token prompt this runs in single-digit milliseconds on a laptop CPU.

#### Training (BPE)

1. Initialise vocabulary as the 256 base bytes.
2. Run pre-tokenization on the corpus; count chunk frequencies.
3. For each chunk, hold a current segmentation (initially: one token per byte).
4. Count all adjacent token-pair frequencies across the corpus, weighted by chunk frequency.
5. Pick the most frequent pair, add it to the vocabulary, record it as the next merge.
6. Apply the merge to all segmentations.
7. Go to step 4 until `|V|` hits the target.

#### WordPiece differences

Same skeleton, but step 5 maximizes `score(a, b) = count(a, b) / (count(a) · count(b))` — a likelihood-ratio that prefers pairs which co-occur more than chance, not just frequent pairs. WordPiece also marks continuation pieces with `##` (`tokenizing → tok, ##eniz, ##ing`) and uses **greedy longest-match** at encode time rather than replaying merges.

#### Unigram differences

Top-down rather than bottom-up:

1. Seed `V` with a large set of candidate subwords (e.g. all substrings up to length L, frequency-filtered).
2. Treat `V` as a unigram language model with parameters `p(t)`. Estimate `p(t)` via EM, where the E-step uses **Viterbi or forward-backward** over each training word's segmentation lattice.
3. Score each token by its loss contribution if removed.
4. Drop the bottom ~10% of tokens.
5. Repeat from step 2 until `|V|` hits the target.

At encode time, Viterbi finds the segmentation maximizing `Σ log p(t_i)`. A side benefit: you can sample lower-probability segmentations to do **subword regularization** (Kudo, 2018) as a form of data augmentation.

### Key trade-offs

| Choice | Gained | Given up |
| --- | --- | --- |
| Larger vocab (200k vs 32k) | Fewer tokens per request, cheaper inference, better non-English fertility | Larger embedding matrix (`d_model × |V|`), more memory, slower softmax over the unembedding |
| Byte-level fallback | No OOV ever; emoji, code, rare scripts always representable | Single rare CJK character may cost 3 tokens (one per UTF-8 byte) |
| Pre-tokenization regex | Stable behavior across whitespace, no `the→cat` merges | Locks in English-centric word boundaries; bad for Chinese/Japanese |
| BPE greedy encode | Deterministic, fast, easy to cache | No notion of probability, no regularization at training |
| Unigram + Viterbi | Probabilistic, supports sampling for regularization | More expensive to encode, harder to implement correctly |
| Adding special tokens post-hoc | Clean separation of structure vs. content | Can collide with user input; vulnerable to prompt-injection of `<|im_end|>` |

### Common failure modes

- **Digit-grouping arithmetic errors**: `cl100k_base` and Llama 3 chunk runs of digits into up-to-3-digit tokens. The number `1,000,001` may tokenize as `1`, `000`, `001` while `999` is one token, so the model learns inconsistent representations of "the same digit position".
- **Leading-space artifacts**: `"Hello"` and `" Hello"` are different IDs. A model fine-tuned with `"Hello"` after a newline can quietly underperform when the prompt template inserts a space.
- **Glitch tokens** (e.g. `SolidGoldMagikarp`): tokens that appear in the BPE vocabulary because of corpus quirks but were stripped from training data. Their embeddings remain at initialization and produce nonsense outputs.
- **Multilingual fertility tax**: English averages ~1.2 tokens/word; Ukrainian ~2.7; Burmese, Telugu, Khmer often >3. Same API call, different cost; same 8k context window, half the usable text.
- **Chat-template injection**: a user message containing literal `<|im_end|>` text may be tokenized into the special ID if the tokenizer is configured to recognize specials in input — opening a prompt-injection surface.
- **Tokenizer recycling**: porting another model's tokenizer onto new weights (e.g. using Llama's tokenizer for a French model) produces fertility blow-ups and degraded perplexity.
- **Trailing-whitespace traps**: chat templates that end the prompt with a trailing space cause the model to score the wrong continuation tokens, because the leading-space variant is already "consumed".

## Why

### Why it exists

Three first-principles reasons:

1. **Lossless integer encoding** is required: transformers compute on integer-indexed embeddings, so the string-to-int step must be reversible and deterministic across training and inference.
2. **Sequence length costs `O(n²)` attention**. Anything that compresses 4 characters into 1 token quarters the cost of attention. Tokenization is a learned compressor whose Pareto target is "high compression on training-corpus text without losing the ability to express anything".
3. **No-OOV guarantee**. Byte-level alphabets make the encoder a total function — every Unicode string round-trips.

### Why it looks the way it does

The obvious alternative is **pure character-level**: vocabulary of size ~150k Unicode code points, no merging, no pre-tokenization. It is simpler, fairer across languages, and avoids glitch tokens entirely. It is not used because:

- Sequences become 3–5× longer, blowing the attention budget.
- The model spends most of its capacity learning spelling instead of semantics.
- Empirically (Kudo & Richardson 2018; many follow-ups), subword models match or beat character models on downstream tasks at a fraction of the FLOPs.

Byte-level (not character-level) BPE is the synthesis: keep the no-OOV property of character models, keep the compression of subword models, accept that one Han character costs 3 bytes. The Unigram-vs-BPE choice is more about training: Unigram supports sampled segmentations during training, BPE is deterministic and faster to encode. Most production tokenizers picked BPE because encode-time determinism made the engineering simpler — not because BPE is mathematically superior.

### Why it matters now

Three trends keep tokenization on the critical path in 2026:

1. **Context windows are exploding** (1M tokens for Gemini, 200k+ for Claude and GPT). Tokenizer fertility is now a first-class scaling-cost question — a 10% better tokenizer is a 10% larger effective context.
2. **Multilingual deployment is the norm**. The "tokenizer tax" disproportionately hits the regions with the fastest LLM-adoption growth.
3. **Code-acting agents** parse, edit, and re-emit source code where tokenization interacts with diff fidelity. Numeric and whitespace handling now have downstream correctness implications, not just cost.

## Open questions / things to verify in practice

- Run `tiktoken.encoding_for_model("gpt-4o").encode(text)` on a representative sample of your prompts — what is your fertility (tokens / word)? Is it under 1.5?
- For your target non-English languages, compare fertility across `cl100k_base`, `o200k_base`, and Llama 3. Does the cost ratio justify a different model choice?
- Try `len(enc.encode("999")) vs len(enc.encode("1000")) vs len(enc.encode("1234567"))` — does the digit-grouping match your assumption?
- Tokenize one of your chat templates including a few special tokens. Are `<|im_start|>`-style tokens being treated as specials or as literal text?
- If you fine-tune, run the base tokenizer over your fine-tuning corpus and check `unk` rate and per-token frequency tail — are large parts of the vocab dead in your domain?
- Try encoding the string `" cat"` and `"cat"`. Are the IDs different? They should be.

Sources:
- [Hugging Face — Byte-Pair Encoding tokenization](https://huggingface.co/learn/llm-course/chapter6/5)
- [Hugging Face — Tokenization algorithms summary](https://huggingface.co/docs/transformers/tokenizer_summary)
- [Sennrich et al. — Neural Machine Translation of Rare Words with Subword Units (BPE paper)](https://arxiv.org/pdf/1508.07909)
- [Kudo — Subword Regularization & Unigram LM](https://arxiv.org/abs/1804.10959)
- [Modal — What is o200k Harmony](https://modal.com/blog/what-is-o200k-harmony)
- [Sander Land — Unreachable tokens in GPT-4o](https://tokencontributions.substack.com/p/unreachable-tokens-in-gpt-4o)
- [LessWrong — SolidGoldMagikarp III: Glitch token archaeology](https://www.lesswrong.com/posts/8viQEp8KBg2QSW4Yc/solidgoldmagikarp-iii-glitch-token-archaeology)
- [arXiv — The Tokenizer Tax Across 25 European Languages](https://arxiv.org/abs/2605.24718)
- [Hugging Face Blog — Tokenization is Killing our Multilingual LLM Dream](https://huggingface.co/blog/omarkamali/tokenization)
- [Hugging Face Blog — wHy DoNt YoU jUsT uSe ThE lLaMa ToKeNiZeR?? (tokenizer recycling)](https://huggingface.co/blog/catherinearnett/dangers-of-tokenizer-recycling)
- [Hugging Face — Chat templates](https://huggingface.co/docs/transformers/main/en/chat_templating)
- [MachineLearningMastery — Training a Tokenizer for the Llama Model](https://machinelearningmastery.com/training-a-tokenizer-for-llama-model/)
- [Brenndoerfer — Unigram Language Model Tokenization](https://mbrenndoerfer.com/writing/unigram-language-model-tokenization)
