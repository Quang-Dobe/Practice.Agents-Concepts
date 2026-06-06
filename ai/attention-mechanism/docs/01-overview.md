# Attention Mechanism — Overview

> Attention is a learned weighted average over a sequence of vectors, where the weights come from comparing a "query" vector against a set of "key" vectors to decide how much each position should contribute.

## The 30-second version
Before attention, models processed sequences one step at a time and squeezed everything they had read so far into a single fixed-size hidden state. That worked for short sentences and fell apart on long ones. Attention lets every position in a sequence look directly at every other position and pull in whatever it finds relevant, with the relevance learned from data. It is the building block that made transformers — and therefore GPT, Claude, BERT, and vision transformers — possible.

## The mental model
Imagine a soft dictionary lookup.

A normal Python `dict` takes a key, finds the one entry that matches exactly, and returns its value. Attention does the same thing, but fuzzy. Each token in your sequence produces three vectors:

- A **query** — "here is what I am looking for."
- A **key** — "here is what I advertise about myself."
- A **value** — "here is what I will hand over if you pick me."

To compute the output for a single token, you take its query and compare it against every key in the sequence using a dot product. The bigger the dot product, the more those two tokens "match." Those scores get turned into a set of weights that sum to 1, and the output is the weighted blend of all the values.

So instead of retrieving one entry, you retrieve a smoothie of all entries, mixed in proportion to how well each one matched your question. The model learns, through training, what makes a good query, a good key, and a good value for the task at hand.

Concrete example: in the sentence "The cat that the dog chased was tired," when the model processes "was," its query roughly asks "what is my subject?" The key for "cat" matches strongly, the key for "dog" matches weakly, and the resulting output is mostly the value vector of "cat." The model has effectively reached across the sentence to find the right referent.

## What it is NOT
- Not a recurrent network. RNNs pass information step by step through a hidden state; attention compares all positions in parallel.
- Not a convolution. Convolutions look at a fixed local window; attention's "window" is the whole sequence and the weights are content-dependent.
- Not memory in the database sense. Nothing is stored between forward passes — the queries, keys, and values are recomputed from the current input every time.
- Not the transformer. Attention is one ingredient; transformers also need feed-forward layers, residual connections, and normalization.

## When you would reach for it
- Any task where relationships between distant elements of a sequence matter (language, code, DNA, time series).
- Tasks where you want the model to learn *what to focus on* rather than hard-coding it.
- Settings where you need parallel training on long inputs — attention computes all positions in one matrix multiply.
- Cross-modal alignment, like matching words to image patches in vision-language models.

## When you would NOT reach for it
- Very long sequences with tight compute budgets — vanilla attention costs scale quadratically with sequence length.
- Problems with strict locality and no long-range structure, where a small CNN or a simple MLP is cheaper and sufficient.
- Tiny datasets — attention has a lot of parameters and tends to overfit without enough data or strong regularization.

## Key vocabulary (just enough to keep reading)
- **Token** — one element of the input sequence (word piece, image patch, etc.).
- **Embedding** — the vector representation of a token.
- **Query (Q)** — what a token is looking for.
- **Key (K)** — what each token advertises about itself.
- **Value (V)** — what each token contributes if chosen.
- **Attention weights** — the normalized similarity scores between one query and all keys.
- **Self-attention** — Q, K, V all come from the same sequence; each token attends to its peers.
- **Cross-attention** — Q comes from one sequence, K and V from another (e.g. decoder attending to encoder).
- **Transformer** — the architecture built around stacked self-attention layers.

## What's next
The next document answers What / Where / When / How / Why in detail: the actual softmax formula, why we scale by the square root of the key dimension, what multi-head attention buys you, how masking enforces causality, and the cost picture that drives every modern "efficient attention" variant.
