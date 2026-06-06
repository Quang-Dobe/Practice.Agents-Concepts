# Attention Mechanism

Attention is a learned weighted average over a sequence of vectors, where the weights come from comparing a "query" vector against a set of "key" vectors to decide how much each position should contribute. It is the operation that lets every token in a sequence look directly at every other token and pull in whatever it finds relevant, instead of squeezing the whole history into a single fixed-size hidden state the way older recurrent models did.

It matters because attention is the building block that made transformers — and therefore GPT, Claude, BERT, and vision transformers — possible. An engineer reaches for it whenever relationships between distant elements of a sequence matter, when the model should learn what to focus on rather than have it hard-coded, when training needs to run in parallel over long inputs, or when aligning across modalities like matching words to image patches.

A good way to picture it is a soft dictionary lookup. A normal Python dict takes a key, finds the one exact match, and returns its value. Attention does the same thing but fuzzy: each token produces a query, a key, and a value, the query is compared against every key, the scores are turned into weights that sum to one, and the output is a blend of all the values mixed in proportion to how well each one matched. So instead of retrieving one entry, you retrieve a smoothie of all entries, weighted by relevance.

---

Full notes: https://github.com/Quang-Dobe/Practice.Concept/tree/main/ai/attention-mechanism/
