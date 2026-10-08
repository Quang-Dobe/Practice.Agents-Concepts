# Gradient Descent

Gradient descent is the algorithm that trains almost every machine-learning model. A model is a pile of numbers called parameters, and a loss function turns how wrong its predictions are into one number. Gradient descent computes the gradient, which says which way each parameter should move to lower that loss fastest, nudges every parameter a small step that way, and repeats, often millions of times.

It matters because every neural network you will touch, from a small classifier to a large language model, is trained by this loop or a close relative such as Adam or AdamW. Engineers reach for it when a model has too many parameters to solve in closed form, the loss is differentiable, and the data is too large for memory so it must be learned in mini-batches. It is not backpropagation, which only computes the gradient, and it does not guarantee the best answer on non-convex problems like neural networks.

Picture standing on a mountainside in thick fog, trying to reach the valley floor. You can only feel the slope under your boots, so you step downhill, feel again, and step again. Your stride length is the learning rate, and it causes most of the trouble. Fitting y = w times x to the single point x = 2, y = 6 starting from w = 0, a learning rate of 0.05 settles on the right answer w = 3, a rate of 0.25 bounces between 0 and 6 forever, and a rate of 0.5 blows up. Same algorithm, same data, only the step size changed.

---

Full notes: https://quang-dobe.github.io/Practice.Agents-Concepts/ai/gradient-descent/present/index.html
