# Tokenization — In Practice

> Builds on `01-overview.md` and `02-deep-dive.md`. Read those first.

## Where you'll actually meet this topic

In an LLM-backed product, the tokenizer is the silent middleman on every request. It decides what the user pays per call (input/output tokens), how much chat history you can keep before truncation, and whether your "8k context window" actually fits the document you wanted to summarize. Most teams notice it for the first time when an accountant asks why the OpenAI bill doubled after a Spanish-language launch.

In a RAG pipeline, the tokenizer is the unit of measurement for everything: chunk size, overlap, retrieval budget, and the maximum number of retrieved passages you can stuff into the prompt. Get this wrong and either you over-truncate (losing answers) or you over-spend (paying for context the model can't actually use).

In a fine-tuning or pre-training project, the tokenizer is part of the model artifact, not a preprocessing step. Swap it and you have a different model; freeze it incorrectly and you ship subtle bugs that look like "the model just isn't learning." It also shows up in agent/tool-calling code where structured output (JSON, code, XML) tokenizes very differently from prose, and where a one-character change in the system prompt can move you across a token boundary.

## Best practices

### 1. Pin the tokenizer to the model — and to a version
**Do:** Load tokenizer and model from the same checkpoint, in the same code path, with both versions pinned (`tiktoken==X.Y.Z`, `transformers==X.Y.Z`, model revision SHA). Treat them as one artifact.
**Why:** A `tiktoken` upgrade can ship a new encoding name for a new model; a `transformers` upgrade can change default chat-template behavior. Either silently changes token counts, costs, and outputs — the classic "tokenizer drift" incident.
**Avoid:** Loading the tokenizer from one place (`AutoTokenizer.from_pretrained("…base")`) and the weights from another (a fine-tune that extended the vocab).

### 2. Count tokens with the exact tokenizer you will hit
**Do:** Use `tiktoken.encoding_for_model("gpt-4o")` for OpenAI, the Anthropic count-tokens API for Claude, `countTokens` for Gemini, and the model's own `AutoTokenizer` for open-weights models. Count the full wire payload — system prompt, tool schemas, function args, assistant prefill.
**Why:** Cross-tokenizer estimates ("~4 chars per token") are wrong by 30–80% on code, JSON, and non-English. Budgeting on the wrong number is how you hit `context_length_exceeded` in production while your dashboard says you had headroom.
**Avoid:** A single shared `count_tokens()` helper that uses `cl100k_base` for every provider.

### 3. Measure fertility, not character length
**Do:** For each language/format you ship, compute tokens-per-word (or tokens-per-character) over a real sample. Track it as a metric. Compare across candidate models before locking in.
**Why:** Fertility is the difference between "Llama 3 is cheaper than GPT-4o" and "Llama 3 is cheaper than GPT-4o for English but 1.6× more expensive for Vietnamese." This is the actual basis for model selection, not the headline $/Mtok.
**Avoid:** Estimating context budgets from `len(text) / 4`.

### 4. Budget context as input + output + overhead, with a margin
**Do:** Reserve explicit slots: system prompt (counted once, with chat-template special tokens), conversation history, retrieved context, expected output, and a 5–10% slack. Truncate the oldest, lowest-value bucket first.
**Why:** Models are trained on examples that end cleanly; cutting a prompt mid-token or mid-JSON degrades output quality before it errors out. And output tokens cost 3–5× input tokens, so under-budgeting output is the single biggest avoidable cost driver.
**Avoid:** Setting `max_tokens` to "whatever's left" — that lets a runaway generation eat your margin.

### 5. Treat JSON, code, and whitespace as expensive
**Do:** Strip non-semantic whitespace from JSON before sending (`json.dumps(obj, separators=(",", ":"))`). Prefer compact field names in tool schemas. For code-heavy prompts, expect 1.5–2× the token count of equivalent prose.
**Why:** A pretty-printed JSON document of 10kB can be 30% more tokens than its minified version — every `\n    ` indent is its own token. At scale that is real money, and it eats context you needed for the answer.
**Avoid:** Sending `JSON.stringify(obj, null, 2)` to an API. The model does not care about the indentation; your wallet does.

### 6. Always use the model's chat template
**Do:** Call `tokenizer.apply_chat_template(messages, add_generation_prompt=True)` (HF) or the provider's `messages=` parameter. Let the library insert `<|im_start|>`, `<|eot_id|>`, and friends.
**Why:** Hand-rolling chat strings drops or duplicates special tokens, which routinely costs 5–20% in eval quality and produces the "model won't stop generating" bug. Trailing-whitespace and missing-BOS variants of this bug are notoriously hard to spot in logs.
**Avoid:** String-concatenating roles into a single prompt for an instruct model.

### 7. Sanitize user input before feeding it to the tokenizer as "raw"
**Do:** When using HF tokenizers, set `add_special_tokens=False` for arbitrary user text, and never set `split_special_tokens=False` on untrusted input. Strip or escape literal `<|im_end|>`-style strings.
**Why:** If special tokens are recognized in user content, an attacker can inject `<|im_end|><|im_start|>system\n…` and break out of their role. This is a real prompt-injection vector, not a theoretical one.
**Avoid:** Passing user-supplied strings straight into a template that allows special-token interpretation.

### 8. Cache tokenization for hot paths
**Do:** Cache the encoded IDs of stable parts of the prompt (system prompt, few-shot examples, tool schemas). For high-RPS counting, reuse the encoder instance and consider `encode_batch` or `encode_to_numpy`.
**Why:** `tiktoken` is fast (hundreds of MB/s) but not free; recreating the encoder per request is the usual culprit when token counting shows up in flame graphs. Caching also lets you precompute prompt-cache hits with providers that bill less for cached prefixes.
**Avoid:** Calling `tiktoken.get_encoding(...)` inside your request handler.

### 9. Inspect "weird" tokenizations directly
**Do:** Keep a debug utility that prints `(token_id, repr(token_bytes))` side-by-side. When the model misbehaves on a specific input, tokenize it first. Tools: `tiktokenizer.vercel.app`, `tokenizer.tokenize(text)`, `tokenizer.convert_ids_to_tokens(ids)`.
**Why:** Half of "the model can't do arithmetic / can't count Rs in strawberry / mangles this name" bugs become obvious the second you see the token boundaries. The model is doing exactly what its input implies.
**Avoid:** Debugging output quality by tweaking the prompt before you have looked at the tokens.

### 10. Reuse a tokenizer unless you have a measured reason not to
**Do:** Start with the foundation model's tokenizer. Only train your own when you are pre-training from scratch (or doing heavy continued pre-training) on a domain where measured fertility is bad — typically >2 tokens/word on your eval set, or a non-Unicode alphabet (proteins, DNA, MIDI).
**Why:** A custom tokenizer is a permanent commitment: you cannot mix it with off-the-shelf weights. The break-even point against "just pay the fertility tax" is far higher than people expect, because every dependency (LoRA adapters, eval harnesses, distillation teachers) assumes the base vocabulary.
**Avoid:** "We'll train our own tokenizer to save tokens" as a fine-tuning optimization. That is tokenizer recycling's evil twin and it loses every time.

## Anti-patterns to recognize

- **The 4-chars-per-token estimator**: A `len(text) // 4` helper used everywhere for budgeting. Fails silently on code, JSON, and any non-English text; the bug surfaces as 429s and truncated answers in production. Use the real tokenizer.
- **Tokenizer recycling**: Loading another model's tokenizer (often Llama's) onto a new model's weights because "BPE is BPE." Embeddings index by ID, so this corresponds to randomly shuffling the vocabulary — perplexity explodes. Always pair tokenizer and weights from the same checkpoint.
- **Pretty-printed prompts**: Sending JSON, YAML, or code with indentation and trailing newlines because it "reads better." The model does not read it; it pays for every `\n    `. Minify structured payloads before sending.
- **Trailing-space prompts**: Ending the prompt with `"Answer: "` (note the space). The space gets consumed by the tokenizer and now the model has to predict the no-leading-space variant of every continuation, which is rarer in training. Leave the trailing space off; the chat template handles it.
- **Counting only the user message**: Token-budget code that ignores the system prompt, tool schemas, and chat-template specials. The actual wire payload is 200–2000 tokens larger than the "prompt" your code thinks it sent. Count the rendered message list, not the raw string.
- **Trusting user-supplied special tokens**: Treating `<|im_end|>` in user input as a literal string when the tokenizer is configured to recognize specials. This is a prompt-injection foothold. Strip or escape, and disable special-token parsing on untrusted input.
- **Silent tokenizer upgrades**: Letting `pip install -U` bump `tiktoken` or `transformers` in CI without re-running token-budget tests. Costs and context math change underneath you. Pin versions and snapshot token counts on a fixed eval set.
- **Glitch-token roulette**: Including rare strings like `SolidGoldMagikarp` or `_$_$_$_$` (often from web-scraped boilerplate) in prompts because they came from a copy-paste. The model produces nonsense because those embeddings were never trained. Sanitize prompts; flag tokens with frequency below a threshold.

## Real-world usage patterns

- **Multilingual SaaS chatbot**: A B2B support product expands from English to Spanish, Polish, and Japanese. The English context budget (8k) was tuned to fit ~6k characters of chat history; Polish blows that to ~10k characters in the same token budget, so older messages get silently dropped. Lesson: context windows are language-dependent — measure fertility per locale and either truncate per-language or switch to a tokenizer with better fertility for your target languages (`o200k_base` and Gemini are noticeably better than `cl100k_base` for non-English).

- **RAG over a code repo**: An engineering assistant chunks source files by token count using `cl100k_base`, retrieves top-k, and stuffs them into the prompt. The team notices chunk boundaries cutting through function bodies. Lesson: tokenize-then-chunk on raw code produces semantically nonsensical splits; chunk on syntax (AST or symbol boundaries) and *measure* in tokens for budgeting, not for splitting.

- **High-volume classification pipeline**: A content-moderation system runs millions of short messages per day through a small LLM. Token counting becomes a CPU bottleneck — naive `tiktoken.get_encoding()` per call dominates a hot path. Lesson: reuse encoder instances, batch with `encode_batch`, and cache token counts on the message hash. The fix dropped CPU usage by 40% at no quality cost.

- **Fine-tuning a domain model**: A legal-tech team fine-tunes Llama 3 on contracts. They debate training a custom tokenizer for "legal jargon." Measured fertility on their corpus is 1.4 tokens/word — well within normal range. Lesson: fine-tuning rarely justifies a new tokenizer; the engineering cost (custom embeddings, broken LoRA stack, incompatible eval harnesses) dwarfs the inference savings.

- **Agent with tool-calling**: An agent emits JSON tool calls; logs show a spike in `max_tokens` truncations on a specific tool. The tool's argument schema uses verbose field names (`customerEmailAddress`, `shippingDestinationCountryCode`). Lesson: schema field names tokenize on every call — shorter, snake_case keys cut 15–25% of tool-call tokens at zero functional cost.

## Operational checklist

- [ ] Tokenizer and model versions pinned together and tested as one artifact.
- [ ] Token-count metric exported per request, broken down by system / user / output / tool-call.
- [ ] Fertility (tokens-per-character) tracked per language/format in your monitoring.
- [ ] Context-budget logic accounts for system prompt, tool schemas, chat-template specials, and expected output — with a slack margin.
- [ ] User input is sanitized for special-token strings before being placed into chat templates.
- [ ] Cost dashboard separates input vs output tokens and alerts on per-tenant anomalies (prompt-injection or runaway-loop signal).
- [ ] A debug tool exists that shows token-by-token splits for any prompt; on-call has used it at least once.
- [ ] Encoder instances are reused (not recreated per request) on any hot path.
- [ ] CI test asserts token counts on a snapshot of representative prompts — fails on tokenizer upgrades.
- [ ] New engineer onboarding includes "the model sees tokens, not characters" with a 10-minute walkthrough of the debug tool.

## How this topic typically evolves in a codebase

Teams start with the tokenizer invisible: a single API call, no token counting, a vague awareness that "long prompts cost more." The first forcing function is usually a `context_length_exceeded` error in production, which leads to a homemade `count_tokens(text)` helper — often using the wrong encoding for the wrong model. This works until the second model is added (Claude alongside GPT, or Llama for a self-hosted fallback) and the helper silently undercounts by 20%.

The second phase is the introduction of a real token-budget abstraction: explicit accounting for system prompt, history, retrieved context, and output reserve. RAG pipelines drive this — chunk size, overlap, and retrieval-k all become token-denominated. Cost dashboards appear, broken down per-tenant and per-feature. Multilingual rollout exposes fertility differences and forces either per-language budgets or a tokenizer-aware model-routing layer.

The painful migration point arrives when the team wants to fine-tune, switch foundation models, or self-host. Suddenly the tokenizer is a hard dependency: prompt-cache hashes, eval snapshots, prompt-injection filters, and tool schemas were all implicitly written against the old vocabulary. The teams who pinned tokenizer versions and tracked fertility metrics from day one switch in a sprint. The teams who didn't spend a quarter chasing "the new model just feels worse" before realizing the prompts haven't been retuned for the new tokenization.

## Further reading

- [Hugging Face — Chat templates](https://huggingface.co/docs/transformers/main/en/chat_templating) — the definitive guide to special-token handling; read before hand-rolling any chat string.
- [OpenAI Cookbook — How to count tokens with tiktoken](https://github.com/openai/openai-cookbook/blob/main/examples/How_to_count_tokens_with_tiktoken.ipynb) — canonical token-counting recipes including the chat-message overhead formula.
- [Hugging Face Blog — Tokenization is Killing our Multilingual LLM Dream](https://huggingface.co/blog/omarkamali/tokenization) — concrete fertility numbers across languages; the source for the "tokenizer tax" framing.
- [Trend Micro — When Tokenizers Drift](https://www.trendmicro.com/vinfo/us/security/news/cybercrime-and-digital-threats/when-tokenizers-drift-hidden-costs-and-security-risks-in-llm-deployments) — security and cost angles on tokenizer version drift in production deployments.
- [Hugging Face Blog — Dangers of Tokenizer Recycling](https://huggingface.co/blog/catherinearnett/dangers-of-tokenizer-recycling) — empirical evidence for why you cannot swap tokenizers between models.
- [arXiv — Getting the most out of your tokenizer for pre-training and domain adaptation](https://arxiv.org/html/2402.01035v2) — when training a custom tokenizer actually pays off; the numbers behind the "reuse unless measured otherwise" rule.
