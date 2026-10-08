# Gradient Descent — Overview

> Gradient descent is the algorithm that trains almost every machine-learning model: measure how wrong the model is, work out which direction makes it less wrong, take a small step that way, and repeat.

## The 30-second version

A model is a pile of numbers called parameters. A loss function turns "how wrong are the predictions?" into one number. Gradient descent computes the gradient, which says which way each parameter should move to lower the loss fastest. It nudges every parameter a little that way and repeats, often millions of times. Every neural network you will touch, from a small classifier to an LLM, is trained by this loop or a close relative such as Adam.

## The mental model

You are standing on a mountainside in **thick fog**, and your goal is the valley floor. You can't see the valley. You can only feel the slope of the ground under your boots.

So you feel which way is steepest downhill, take one step, feel again, step again. When the ground goes flat, you stop.

- The **mountain** is the loss landscape, which gives the loss for every possible setting of the parameters.
- **Your position** is the current parameter values.
- **The slope under your boots** is the gradient.
- **Your stride length** is the learning rate.

The stride causes most of the trouble. Tiny steps and you spend all day on the hill. Huge steps and you leap across the valley and land higher up the other side.

Concrete (Illustrative): fit `y = w · x` to one point, `x = 2, y = 6`, with squared-error loss. The right answer is `w = 3`. Start at `w = 0`. Each step does `w ← w − lr · gradient`:

```text
lr = 0.05  ->  w: 0 -> 1.20 -> 1.92 -> 2.35 -> ...  converges to 3
lr = 0.25  ->  w: 0 -> 6 -> 0 -> 6 -> ...           bounces forever
lr = 0.5   ->  w: 0 -> 12 -> -24 -> 84 -> ...       blows up
```

Same algorithm, same data. Only the step size changed.

*This is a simplification.* Real models have millions or billions of parameters, so the mountain has that many dimensions. You usually measure the slope on a small random sample of data, so your footing is noisy. The deep dive covers what that changes, including why saddle points matter more than local minima at that scale.

## What it is NOT

- Not backpropagation. Backprop *computes* the gradient efficiently. Gradient descent *uses* it to update the weights.
- Not a guarantee of the best answer. Only on convex problems does it reliably reach the global minimum, and neural networks are not convex.
- Not gradient boosting. Gradient boosting builds an ensemble of decision trees, each fit to the previous ones' errors.
- Not a rival to Adam. Adam, AdamW and RMSProp are gradient-descent variants that adapt the step size per parameter.

## When you would reach for it

- You are training a neural network of any size.
- Your model has too many parameters to solve for in closed form.
- Your loss is differentiable, so a gradient exists to follow.
- Your data is too large for memory, and you can learn from it in mini-batches.
- You are fine-tuning a pretrained model on your own data.

## When you would NOT reach for it

- Small linear regression: a least-squares solver gives the exact answer in one shot.
- Discrete or non-differentiable objectives, such as choosing hyperparameters or a config. Use grid, random or Bayesian search instead.
- Small, smooth problems where second-order methods like L-BFGS or Newton converge in far fewer steps.
- You only call a hosted model through an API. You have no weights to update.

## Key vocabulary (just enough to keep reading)

- **Loss function**: maps predictions and targets to one number. Lower is better.
- **Gradient**: the partial derivatives of the loss for every parameter. It points uphill, so you step the opposite way.
- **Learning rate (`lr`)**: the multiplier on the gradient that sets the step size.
- **Batch / mini-batch / SGD**: the gradient is computed on all the data, on a small random subset, or on one example (classic stochastic gradient descent). In practice "SGD" usually means mini-batch.
- **Epoch**: one full pass over the training data.
- **Momentum**: carry part of the previous step forward, like a rolling ball, to smooth out zig-zagging.
- **Adam / AdamW**: adaptive optimizers that give each parameter its own effective step size. AdamW is the common default for transformers.
- **Learning-rate schedule**: changes `lr` during training, for example a short warmup and then a gradual decay.

## What's next

The next document answers What / Where / When / How / Why in detail. It covers the update rule, batch vs mini-batch gradients, momentum and Adam, and the usual failure modes: a bad learning rate, saddle points, ill-conditioned valleys, and vanishing or exploding gradients.
