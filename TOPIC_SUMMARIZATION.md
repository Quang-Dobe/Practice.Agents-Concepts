# Autoscaling

Autoscaling is a control loop that watches a load signal, such as CPU, queue depth, or requests per second, and adds or removes compute capacity on its own. It measures load every few seconds, compares it to a target you set, like "keep average CPU near 60%", and changes the number or size of instances to stay near that target.

It matters because traffic is never flat. If you provision for the peak, you pay for idle machines most of the day; if you provision for the average, the site falls over at the peak. Engineers reach for autoscaling when traffic follows a daily pattern, when workers pull from a queue whose backlog grows and shrinks, or when stateless services sit behind a load balancer. It is not a fix for a slow app, and it is not instant: new capacity takes seconds to minutes to arrive. Stateful systems and flat, predictable load usually do better without it.

Picture a supermarket manager watching the checkout lines. Line length is the signal, and "no more than three people per lane" is the target. When lines grow, the manager opens more lanes; when it is quiet, lanes close and staff go home. A new cashier needs a couple of minutes to log in, so a sudden rush still hurts briefly, and a cooldown stops the manager from opening and closing the same lane every 30 seconds. That loop of measure, compare, adjust, wait, and repeat is autoscaling.

---

Full notes: https://quang-dobe.github.io/Practice.Agents-Concepts/cloud/autoscaling/present/index.html
