# Infrastructure as Code

Infrastructure as Code means your servers, networks, databases, and permissions are defined in text files that live in Git and get applied by a tool. Instead of clicking through the AWS or Azure portal to create a VM, a load balancer, and a firewall rule, you write a file that says these things should exist, configured this way. A tool reads that file, compares it to what actually exists in your cloud account, and makes reality match. The cloud console stops being the source of truth.

You reach for it when staging needs to actually match production rather than approximately match it, when more than one person changes infrastructure and you want review and an audit trail, when you build the same stack per customer or per region, or when disaster recovery has to be a rebuild instead of a restore-from-memory. You skip it for a one-off experiment you will delete this afternoon, during a real outage where you fix by hand first and reconcile after, and on fully managed platforms where there is barely any infrastructure to describe.

The useful mental model is a recipe versus a food order. An imperative script is a recipe: create the VPC, then the subnet, then the gateway. It works the first time; run it again and you get two VPCs. Declarative IaC is a food order: a table set for six with three plates of pasta. The kitchen looks at what is already on the table and adjusts. Run the same order twice and nothing happens, because the table already matches. That property is idempotency, and it is the whole reason IaC scales.

---

Full notes: https://quang-dobe.github.io/Practice.Agents-Concepts/cloud/infrastructure-as-code/present/index.html
