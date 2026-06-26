# Tokenization

Tokenization is the step that turns raw text into the small numbered chunks ("tokens") a language model actually does math on. It splits a string into subword pieces, looks each piece up in a fixed vocabulary, and hands the model a list of integers — every modern LLM, from GPT-4 to Llama 3 to Claude, runs this step before any "intelligence" happens.

Engineers reach for it whenever cost, context length, or weird model behavior is on the line. Tokens are the unit of API billing and the unit of context windows, so estimating prompt size starts here. Tokenization is also the hidden source of a surprising number of bugs: models mangle arithmetic, miscount letters, or stumble on non-English text precisely because of how the tokenizer chunked the input. Anyone building RAG pipelines, fine-tuning a model, or just trying to fit more into a prompt eventually has to think about it.

A useful way to picture it: a tokenizer is a phrasebook for a tourist who only knows a fixed list of syllables. You can say anything you want, but you have to assemble it from the syllables in the book. Common things like " the" or "ing" get their own entry. Rare words like "antidisestablishmentarianism" are stitched together from smaller pieces. The phrasebook is built once, before training, by a greedy merging algorithm — most commonly Byte Pair Encoding — that learns which character pairs keep showing up together and promotes them to single entries.

---

Full notes: https://github.com/Quang-Dobe/Practice.Concept/tree/main/ai/tokenization/
