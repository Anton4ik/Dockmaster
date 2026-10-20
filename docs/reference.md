# Dockmaster — reference

The full description of what every page and setting does. The [README](../README.md) is the tour.

Self-hosted container orchestration for a handful of Linux hosts: one dashboard (runs in Docker on the NAS),
one lightweight agent per machine. Docker Compose projects, replicas, health checks, zero-downtime rolling
updates, private registries, live container stats — without Swarm or Kubernetes.

```
 ┌──────────────────── NAS (docker) ────────────────────┐
 │  dockmaster-server  :8080  UI (React) + REST + SignalR │
 │                     :9443  agents (gRPC/TLS, pinned)   │
 └──────────────────────────▲───────────────────────────┘
                            │ outbound only, one persistent stream per agent
           ┌────────────────┴────────────────┐
   ┌───────┴────────┐                ┌───────┴────────┐
   │   web host     │                │   other host   │
   │ dockmaster-    │  docker.sock   │ dockmaster-    │
   │ agent          │  + compose     │ agent          │
   └────────────────┘                └────────────────┘
```

* **Agents never listen.** They connect *out* to the server's agent port over TLS and pin the server's
  certificate fingerprint. Each agent has its own token. Nothing has to be opened on the agent hosts.
* **Projects = docker-compose.yml + .env**, stored on the server, deployed to one agent.
* **Registries**: private registries (basic or bearer auth) are stored encrypted; the UI browses repositories
  and tags, and agents receive credentials only for registries a project's images actually reference.
* **Live view**: per host CPU / memory / disk / load, per container CPU %, memory vs limit, network, restarts,
  uptime, health; start / stop / restart / remove; following logs. Pushed over SignalR as the agents report: every
  5 s for a server whose containers are on screen, every 10 s while a browser is open elsewhere, every 30 s when
  nobody looks (the minute charts average whatever comes in). The agent only inspects a container again when docker
  reported an event for it, so a quiet host costs one stats call per container per report.
* **Pull & redeploy** from any container or replica row: containers of a Dockmaster project redeploy their service
  (rolling, one replica at a time, skipped when image and config are unchanged); other containers are re-created by
  the agent with the same settings (command, working dir, user, healthcheck left to the new image unless the container
  set its own), and the old container comes back unless the new one gets healthy — or, without a healthcheck, runs
  10 s without a restart.
* **Images page** per agent (agent menu → images): every image with its size, tags and the containers using it, split
  into untagged leftovers, unused and all; remove a selection or a whole category. Every pull of a newer version leaves
  the old image untagged, and the agent removes those by itself after each successful pull & deploy.

## How a deploy works

For every service in the project (or the ones you picked) the agent decides between two strategies:

| | Rolling update (zero downtime) | Recreate |
| --- | --- | --- |
| when | no `container_name`, no published `ports` (or only ones a balancer holds, see below), and `deploy.replicas > 1` (or `x-dockmaster: {rolling: true}`) | everything else |
| how | new replica(s) start next to the old ones → wait until READY → optional drain hook in the old one → stop & remove old → pause → next batch | `compose up --force-recreate` → wait until READY |

* **READY** = the `healthcheck` reports healthy. Services **without a healthcheck** are ready when they stayed
  running (no restart) for `deploy.update_config.monitor` (default `10s`).
* `deploy.update_config.parallelism` — replicas replaced at a time (default 1).
* `deploy.update_config.delay` — pause before the next replica, e.g. `35s` (default none). With a healthcheck the
  new replica is already healthy when the pause starts, so it is rarely needed.
* label `dockmaster.pre-stop-hook: <sh command>` runs inside the old replica before it is stopped (drain).
  `docker-rollout.pre-stop-hook` is accepted as an alias.
* `x-dockmaster: {drain: unhealthy}` on the service — after the hook, wait until the old replica's healthcheck
  fails (the load balancer drops unhealthy containers), then `drain_grace` (default `5s`) for requests in flight.
  Use it with a hook that only flips the check, e.g. `touch /tmp/drain` and a healthcheck starting with
  `test ! -f /tmp/drain &&`, instead of a fixed `sleep` in the hook.
* the project's *ready timeout* (default 180 s) bounds every wait; on failure the new replicas are removed and
  the old ones keep running.
* a new replica takes over the slot of the one it replaces: it starts as `project-service-N` with compose number
  `N`, so names stay `1..N` and metrics (cAdvisor, Loki) keep one series per slot.

### Several replicas of a service with a fixed port

Only one container can hold a host port such as `8765:8080`, and only one can carry a `container_name`. When you
give such a service more replicas in the Services tab (or turn autoscaling on), Dockmaster removes the
`container_name` (the replicas become `project-service-1`, `-2`…, reached by the service name) and asks how the
replicas share the port:

* **Balancer** (recommended) — `x-dockmaster: {balance: true}`; the file keeps `8765:8080`. At deploy the service
  publishes nothing on that port and a `<service>-balancer` container (HAProxy) holds it, spreading the connections
  over the replicas it finds by the service name in docker's DNS — also the ones a rolling update or the autoscaler
  adds. The service then gets rolling updates. TCP only; a `${VARIABLE}` port or `network_mode` stops the deploy.
  Back at 1 replica the balancer goes and the service holds the port itself again (the choice stays in the file for
  the next time) — unless `rolling: true` or autoscale in mode auto still needs it.
* **Port range** — the port becomes `8765-8766:8080`, one port per replica (sized to the replicas, or the autoscale
  maximum). The first port reaches only the first replica: for when something in front picks the replica itself.

Services are updated one after another, dependencies (`depends_on`) first. `x-dockmaster: {parallel: 2}` at the top
of the compose file updates up to two services at a time; each rolling service still keeps a ready replica, but for
a while every service being updated runs one replica more.

## Layout

| Path | What |
| --- | --- |
| `src/Dockmaster.Shared` | protobuf contract between agent and server (`Protos/agent.proto`) |
| `src/Dockmaster.Server` | ASP.NET Core 9: REST API (`Api/`, templates and API keys in `TemplatesApi.cs`), SignalR hub (`Hubs/`), gRPC agent hub, SQLite (EF Core), registry client |
| `src/Dockmaster.Web` | React 19 + Vite + TypeScript UI, built into the server's `wwwroot` |
| `src/Dockmaster.Agent` | .NET 9 worker: Docker Engine API (Docker.DotNet) + `docker compose` CLI, rollout logic |
| `deploy/docker-compose.yml` | server on the NAS |
| `.github/workflows/build.yml` | CI: tests, then builds both images and pushes them to GHCR |

## Install and update

Both images are built by GitHub Actions on every push to `main` and published on GHCR:

* `ghcr.io/anton4ik/dockmaster-server:latest` (+ `:<commit>`)
* `ghcr.io/anton4ik/dockmaster-agent:latest` (+ `:<commit>`)

Every tag is a multi-arch manifest for **linux/amd64** and **linux/arm64**, so the same `docker run` works on an x86
server, a Raspberry Pi 4/5, an ARM NAS or an Apple-silicon Docker Desktop. The Dockerfiles cross-compile the .NET
code (and the UI) on the build host and only run the final alpine stage for the target platform, so building the
images yourself for another architecture needs no emulated compiler, just `docker buildx`:

```bash
docker buildx build --platform linux/arm64 -f src/Dockmaster.Agent/Dockerfile -t dockmaster-agent:arm64 .
```

(An `apk add` step still runs under QEMU: `docker run --privileged --rm tonistiigi/binfmt --install arm64` once on a
Linux host; Docker Desktop has it built in.)

**Server** (NAS or any docker host):

```bash
docker compose -f deploy/docker-compose.yml up -d
```

To update: `docker compose -f deploy/docker-compose.yml pull && docker compose -f deploy/docker-compose.yml up -d`.
The `/data` volume holds the SQLite db, the agent TLS certificate (agents pin it — keep the volume) and the
data-protection keys for stored registry passwords and secrets.

**Backup** (*Settings → Backup*): one a day in `/data/backups` (the last 7), a download on demand, and **restore from a
file**: the zip is checked, put in place on the next start (the data before it goes to `/data/before-restore-<time>`)
and the server stops for docker to start it again (`restart: always`). A backup holds the database (projects with
their files and secrets, registries, servers, history), the agents' certificate and the keys that decrypt the
secrets — not the containers' volumes.

**Agents**: the *Add server* page prints the `docker run` of the agent container (token and fingerprint included); the
image it names is the *Agent image* in *Settings* (`ghcr.io/anton4ik/dockmaster-agent:latest` by default; a private
registry with a stored login adds a `docker login` line to the snippet). To update an agent: **update agent** on its
page — it pulls its image (with the host's `docker login` from `/root/.docker`, override with
`DOCKMASTER_HOSTDOCKERCONFIG`) and replaces its own container; the page flags agents built from an older commit as
*outdated*. By hand: `docker pull <image> && docker rm -f dockmaster-agent` and run the same `docker run` again —
`/var/lib/dockmaster` on the host keeps the project files.

The agent's data dir is bind-mounted at the **same path** on the host (`-v /var/lib/dockmaster:/var/lib/dockmaster`):
compose hands project paths (secret files, `./relative` bind mounts) to the Docker daemon, which resolves them on the
host. Agents started with the old `-v dockmaster_agent:/var/lib/dockmaster` volume keep working but log a warning,
and secrets / relative mounts do not reach the containers until they are re-created with the bind mount.

The host's cgroup tree is mounted read-only (`-v /sys/fs/cgroup:/host/cgroup:ro`): the agent reads each container's
`memory.peak` there, so the memory charts show a spike between two heartbeats — the one the OOM killer acts on — as
the minute's *peak* (cgroup v2, kernel 5.19+; on cgroup v1 docker's own `max_usage` is used, no mount needed). An agent
started without it gets the mount on its next **update agent**.

* UI: `http://<host>:8080` — the first visit makes the first admin, name and password, with the one-time setup code the
  server prints to its log at start (`docker logs dockmaster`); or set `DOCKMASTER_ADMINPASSWORD` once: the user
  `admin`. Wrong passwords are limited to 10 per name and address in 5 minutes. More people are added on the **Users** page (see *Users and permissions*).
  Put a reverse proxy with TLS in front (Traefik, nginx, the NAS's own; WebSockets must be allowed — SignalR).
* Agents: `https://<host>:9443` — self-signed certificate generated on first start. Do **not** put a reverse
  proxy in front of this port; agents pin the fingerprint shown in *Settings*.
* Environment: `DOCKMASTER_UIPORT`, `DOCKMASTER_AGENTPORT`, `DOCKMASTER_DATADIR`, `DOCKMASTER_ADMINPASSWORD`.

Without Docker for the agent itself: `dotnet publish src/Dockmaster.Agent -r linux-x64 --self-contained -p:PublishSingleFile=true`,
copy the binary and run it as a systemd service with the same `DOCKMASTER_*` variables
(`DOCKMASTER_SERVERURL`, `DOCKMASTER_TOKEN`, `DOCKMASTER_SERVERFINGERPRINT`, optional `DOCKMASTER_DATADIR`).

## Using it

*Projects → New*: name (compose project name), agent, compose YAML (or one click on a **ready-made app** —
Nginx, PostgreSQL, MySQL, MariaDB, Redis, MongoDB, RabbitMQ, Adminer, Grafana, Uptime Kuma, Seq — which writes it with
passwords made up and a volume for its data), optional `.env`. Then

* **Pull & deploy** — pulls images and deploys every service
* **Deploy (no pull)** — same without pulling (config changes)
* *Services* tab — per-service pull & deploy / restart, strategy, readiness rule and image freshness. Replicas, memory and
  CPU are editable: the values are written into the compose file in place (comments survive); memory / CPU changes are
  applied **live** to the running replicas (`docker update`, no restart), a new replica count only starts or removes
  replicas (drained like in a rolling update) — the running ones stay, unless the image or config changed too
* **Restart / Stop / Start / Down** — plain compose actions on the whole project
* *Services* tab → **+ add service** (a ready-made app or any image) and a click on a service's name — forms for the
  image, the ports opened on the server (`8080 → 80`), the settings (environment variables, `${VARIABLES}` still from
  the `.env`) and when docker restarts it; only what changed is written into the compose file. *remove service* in its
  menu takes its containers down first; its volumes stay on the server.
* *Services* tab → **Data** — where a service keeps files that must survive an update, as a form: a **volume**
  (docker keeps it; a new one, one the project already has — shared with another service — or any volume of the
  server) or a **folder of the server** (`./data` in the project's folder, or `/srv/app`), at a path in the container,
  read-only or not. The paths the image keeps its data in are offered (read from the image on the server, or known for
  postgres, mysql, redis…), and a service whose image keeps data in a path nothing keeps is flagged: it is lost on the
  next update. Written into the service's `volumes:` (entries the form did not touch stay as written) with the
  top-level `volumes:` it needs. The plan (Servers tab, deploy log) warns when data would stay behind: a service with
  data that moves to another server starts empty there, one spread over servers has a copy on each, an unnamed volume
  is easily lost, and a rolling update of a single replica runs two containers on the same data (never for a database).
  **Down** removes only Dockmaster's own files of the project; `./data` folders stay.
* *Files* tab — the project's config files (`nginx.conf`, `appsettings.json`, a start script) edited in the browser.
  Every deploy writes them to `<project>/files/` on each server of the project; a service mounts one at a path in its
  container (from the tab, or *Services → Data → Project file*: `./files/nginx.conf:/etc/nginx/nginx.conf:ro`). A
  changed file re-creates the services that mount it on their next deploy (a label with its hash goes into the compose
  file the agent gets). Up to 1 MB each.
* *Secrets* tab — tick the services that read a secret (their `secrets:` list); write-only values stored encrypted on the server. On deploy the agent writes them to
  `<project>/secrets/<name>` and declares them in a generated override file, so a service only needs
  `secrets: [db_password]` and reads `/run/secrets/db_password`. Don't declare them in the top-level `secrets:` yourself.
  A secret is also a `${VARIABLE}` of the compose file, filled in on deploy like a `.env` line (a secret wins over a
  `.env` line of the same name) — for apps that read their settings from the environment. The *Environment* tab offers
  to move the passwords, keys and connection strings of the `.env` and of the services' own settings there in one go;
  a server whose agent predates this refuses such a deploy instead of starting services with empty values.
* *History* — every deployment with its full log. A deploy keeps the compose file and `.env` it put out: **changes**
  shows what changed since (red gone, green new), and **go back to this version** puts them back and pulls & deploys
  (secrets stay as they are now). A save from a page that loaded the project before someone else changed it (a
  webhook's tag, a rollback, a limit set on the server page) is refused instead of running over it.
* *Automate* — a **deploy webhook** for CI: one secret URL per project; `curl -fsS -X POST "<url>"` pulls and
  deploys. `?services=api` deploys only that service, `&tag=1.4.2` first writes that tag into the service's image,
  `&wait=true` answers when the deploy is over (500 when it failed, so `curl -f` fails the CI step). The tab has the
  step for Drone, GitHub Actions and GitLab CI; the history marks these deploys *webhook*.

**Volumes** (server page → *Volumes*) — every docker volume of the server with its size, the containers that use it
(and where), the project it belongs to; unused and unnamed ones apart. New volumes: on the server, or a folder of a NAS
over **NFS** or **SMB** (mounted once right away, so a wrong address or password shows there). Volumes a container uses
cannot be removed. Each volume's menu: **browse files** (folder by folder, a file or a folder downloads), **download** as a
`.tar.gz`, **upload** a `.tar.gz` into it (added, or instead of what is there), and **copy to another server** — packed
there, sent through the Dockmaster server, unpacked on the other one, for a service that moves with its data. A helper
container from the agent's own image does the reading and writing.

**Docker networks** (server page) — the networks of the server with the containers on each and the project that made
it; new bridge networks (internal: no way to the internet) to let services of different projects reach each other,
removing the unused ones.

**Console** (container page → *Console*) — a shell inside a running container in the browser (bash where the image
has it, sh otherwise), with colours and the window's size; leaving the page closes it.

**Agent page** — every container with its image status (*latest*, *update available*, *pulled · redeploy*; registries are
asked every 15 minutes or on *check for updates*), live logs, and memory / CPU limits you can click and change on the fly.
Containers of Dockmaster projects keep the new limit (it goes into the compose file); others lose it when re-created.
Removing a limit needs a redeploy. Synology kernels cannot limit CPU (no CFS quota) — the CPU field is disabled there.
The restart policy is changed on the container page (*change* next to it): applied live (`docker update --restart`),
written into the compose file (`restart:`) for all replicas of a Dockmaster project's service.

**Taking over existing projects** — compose projects started without Dockmaster (Portainer stacks, `deploy.sh`,
Container Manager) show an **import** button on the agent page. The agent reads the compose files named in the
containers' labels — Portainer stacks through the `portainer_data` mount, moved files from the newest `vN` folder — and
stores them normalised by `docker compose config --no-interpolate` (relative paths become absolute host paths, so volumes
keep their data). `${VARIABLES}` without a value in an env file (Portainer keeps them in its database) are recovered from
what the running containers received. If no file is left, the compose file is rebuilt from the containers and flagged
for review. Running containers are untouched; the next deploy from Dockmaster takes the project over.

## Users and permissions

Everyone signs in with a name and a password. An **admin** may do everything: every server, the Users page, Settings,
Registries, the Network page, Templates and API keys, backups, adding and removing servers. Anyone else sees only the
servers they were given a **role** on, and does there what the role allows; a role on *every server* also covers servers
added later, and a role on a single server adds to it.

A role is a set of these permissions (the **Roles** tab shows them as a table; new installations start with Viewer,
Operator, Developer and Server admin, all of them editable):

| Permission | What it allows on the server |
|---|---|
| View | the server, its containers, projects, logs, metrics, events and deploy history (comes with every role) |
| Start & stop | start, stop, restart, kill and remove containers; start / stop / take down projects; run a job now |
| Deploy | deploy projects, pull & re-create containers, roll back to an earlier deploy |
| Edit projects | create, import, change and delete projects: compose file, services, files, limits, placement |
| Secrets | project secrets, the `.env`, the project's files, the deploy webhook, the values of containers' and the compose file's `environment:` (shown as `(hidden)` without it; a save keeps them) |
| Terminal | a shell in containers |
| Volumes | browse, download, upload, copy, create and remove volumes |
| Server | images, docker networks, updating the agent, renaming the server |

A project spread over several servers needs the permission on each of them to be changed or deployed (seeing it takes
View on its own server); a service cannot be placed on a server the user may not edit projects on. The server checks
every request and every live (SignalR) call; the UI only leaves out what would be refused. A change to a user or a role
applies at once; a new password or a disabled account also ends that user's sessions. The admin password of an older
installation becomes the user `admin` on the first start of this version (everyone signs in again once).

## Templates and API keys

A **template** (*Templates* page) is a compose project a program starts many times over — a game server per player, a
preview per branch. Each **instance** is an ordinary project named `<template>-<instance>` (history, metrics, events,
logs work as for any other); the Projects page hides them behind a checkbox.

* The template holds the compose file, a base `.env`, the **variables** a caller may set (name, required, default,
  pattern), the servers instances may go to (none ticked: any) and the most instances at a time.
* `${INSTANCE}` and `${TEMPLATE}` are always set. The instance's values are written single-quoted into its `.env` under
  the template's lines; compose fills them in after the YAML is parsed, so a value can never add keys, mounts or
  services. Quotes and line breaks are refused whatever the pattern says; a variable without a pattern takes a word of
  letters, digits, `_`, `.` and `-` (no `/` or `:`, not starting with `.` or `-`), so `${DATA}:/data` cannot become a
  host path. The template page warns about variables used in `image`, `volumes`, `ports`, `devices`, `cap_add` and the
  like, where the caller's value decides what the container may reach.
* Every deploy of an instance through the API takes the template's files again: an edited template reaches the
  instances on their next deploy (start, stop and restart keep what they run).

An **API key** (*Settings → API keys*) manages instances of the templates it lists, and nothing else: it never sends
compose of its own, so it cannot mount the docker socket or reach other projects. The key is shown once; only its
SHA-256 is stored.

```
Authorization: Bearer dmk_…

GET    /api/templates/{template}/instances
POST   /api/templates/{template}/instances          {"name":"abc","variables":{"SETTINGS":"{…}"},"server":null,"deploy":true}
GET    /api/templates/{template}/instances/{name}   → state: new, deploying, running, starting, unhealthy, partial, stopped
PUT    /api/templates/{template}/instances/{name}   {"variables":{"BOTS":"40"},"deploy":true}
POST   /api/templates/{template}/instances/{name}/{deploy|start|stop|restart}
DELETE /api/templates/{template}/instances/{name}[?force=true]
```

* A new instance without `server` goes to the online allowed server with the most memory free (then the fewest instances).
* Operations answer when they are over (`200`/`201`, `500` when it failed, `503` when the server is offline);
  `?wait=false` answers `202` at once and the operation goes on. `start` on an instance never deployed deploys it.
* `DELETE` takes the containers down first; when the server cannot (offline) the instance is kept unless `force=true`.
* The signed-in admin may call the same endpoints (the Templates page does).

## Metrics, events and notifications

* **Metrics** — every minute the server keeps a point of each server (CPU, memory, disk, load) and each service (its
  replicas on all servers added up: CPU against the CPU limit, memory against the memory limit, traffic in and out,
  the replica count) and each container by name (CPU, memory, traffic — compose or not, and on over a pull & redeploy)
  for 24 hours; the project's *Metrics* tab charts the services, a container's *Metrics* tab that container. It lives
  in memory and is written to
  `/data/metrics.json.gz` on shutdown, so an update of the server does not start the charts over.
* **Events** — failed deploys; a server away for more than 2 minutes (and back); containers that restart, turn
  unhealthy (and healthy again), stop with an error or are killed for running out of memory (not while their project
  is being deployed: a rollout stops and drains replicas on purpose); a host above 90 % memory for 5 minutes or 90 % docker disk; a
  service above 90 % of its CPU limit for 10 minutes or of its memory limit for 5; a replica whose lowest memory of
  the last day is well above the lowest of the day before (so neither garbage collection nor the daily rise and fall
  with the traffic counts; not in its first two days, when caches fill up) — most likely a leak;
  and what the autoscaler does or would do. Each is reported when it starts and when it is over. The *Events* page
  lists them (30 days), the dashboard shows the warnings of the last day, a project's *Metrics* tab its own.
* **Seq** — with a Seq address (and an API key if Seq wants one) in *Settings → Notifications* every event is also
  sent there (`Application = 'Dockmaster'`, with `Kind`, `Project`, `Server`, `Service`) — from the moment the
  address is set, not the backlog. E-mail is Seq's job: an alert on `Application = 'Dockmaster' and @Level in
  ['Warning', 'Error']` sends them through its SMTP. Events wait in the database while Seq is unreachable (also over
  a restart) and go out when it is back. What is going on is remembered over a restart too, so an update of the
  server does not report it all again.

## Autoscaling

A service may run more replicas while the ones it has are busy (*Services* tab → *Autoscale*, or in the file):

```yaml
    deploy:
      replicas: 2                 # the least
      resources: {limits: {cpus: "1", memory: 512M}}
    x-dockmaster:
      autoscale:
        max: 4                    # the most
        cpu: 70                   # one more when the replicas average above 70 % of their CPU limit
        agents: [beta, gamma]     # where extra replicas may go; default: the servers the service runs on
        mode: recommend           # or auto
```

* It measures against the **CPU limit** (like Kubernetes against requests): without one it does nothing. Not memory —
  a leak would only get more replicas; memory decides where a replica fits.
* **One more** after 3 minutes above the threshold, **one less** after 15 minutes in which the rest would stay below
  ¾ of it; 5 / 10 minutes between changes. The load is taken to spread over the replicas: 2 at 90 % would be 3 at 60 %.
* **mode: recommend** (the default) changes nothing: it reports under *Events* (and to Seq) what it would do and when,
  with the replicas it would have run — run it for a few weeks to see whether autoscaling would help at all.
* **mode: auto** does it. A replica goes to an allowed server that is online, not busy (CPU below 80 %) and keeps 10 %
  of its memory free after the new replica's limit and the limits of everything already there (what a container
  without a limit uses) — a server without a replica of the service first, then the one with the most room. Its
  own containers come first: a server whose memory stays above 92 % or CPU above 90 % gets its guest replica taken
  back. Adding a replica deploys only that server (the running replicas stay), removing one drains it (pre-stop
  hook) or takes the service down where it has none left; the history lists each change. It never waits for or
  overlaps a deploy of the project, and adds nothing while the service has changes in the compose file (or .env)
  that were never deployed — a new replica would bring them out on one server only; deploy first.
* In mode auto a service that may go to other servers is reached through the balancers from the start (see below), so
  a replica elsewhere changes nothing on the servers it already runs on — it needs `expose` for that, the servers
  in the mesh, and a reverse proxy pointed at the balancer address the *Servers* tab shows; deploy after switching to
  auto. Switching auto off removes the replicas it added; deploy afterwards to take the balancers out of the layout.

## Jobs on a schedule

Some containers are not meant to keep running: a backup, a report, a clean-up. Like a CronJob in Kubernetes, a service
with a schedule is **started by Dockmaster at its time, does its work and exits** — and that is not an error. In a
service's settings (*Services* tab → its name → *Restart it* → *it is a job*) pick every N minutes / hours, every day,
Mon–Fri, some days of the week or a day of the month at a time, or write a cron expression; the times are in the time
zone you choose (the browser's by default). In the file:

```yaml
    backup:
      image: my/backup:1
      restart: "no"               # docker must not start it again when it exits
      x-dockmaster:
        schedule: "0 3 * * *"     # minute hour day month weekday, or @hourly / @daily / @weekly
        timezone: Europe/Riga     # default UTC
        timeout: 1h               # optional: a run still going after this is stopped
```

* A **deploy creates the container but does not start it** (`compose up --no-start`); each time on the schedule the
  server tells the agent to `compose start` it. Nothing runs at the deploy itself — **run now** in the job's row does.
* `restart:` is set to `"no"` when a schedule is saved (`on-failure:N` is kept: docker retries a failed run N times,
  which counts as one run). A job runs one container at a time: one replica, no rolling update, no autoscaling.
* A run that is still going when the next time comes is **skipped** (and said so under *Events*), never doubled.
  Times that passed while the Dockmaster server was not running are missed, not caught up (a backup meant for 03:00
  is not wanted at 11:00) — an event says how many.
* **Runs**: the job's row shows its schedule in words and the next run; *runs & schedule…* lists the last runs with
  how long each took, how it ended and who started it (the schedule, *run now*, or something else). A run that ends
  with an error is a warning under *Events* (and in Seq) with the container's last log lines; the next good run says
  it works again. A container of a job that exited with 0 is *finished*, not *stopped*: it is not counted as a
  stopped container anywhere, and only a failed run counts as a problem.
* Jobs need the agent from this version to be created without starting; an older agent runs the job once at the deploy
  and waits for it to end (the deploy fails when that takes longer than the ready timeout).

## Network between servers

*Network* page: put servers into a **WireGuard mesh** the server manages. The agent starts a `dockmaster-mesh` helper
container from its own image (host network, `NET_ADMIN`) that owns a `dm0` interface on the host; keys are generated on
the host and only the public key reaches the server. Other WireGuard interfaces on the host are left alone: own
interface, UDP port 51821 (not 51820), own subnet `10.77.0.0/16` (both in *Settings*). Kernel WireGuard when the module
exists, `wireguard-go` otherwise (Synology).

Who talks to whom is decided from the addresses, no probing: a server reachable from the internet is a direct peer for
everyone; a server behind NAT keeps a keepalive to the public ones; two servers behind the same NAT talk over the LAN;
two servers behind different NATs go through a public **hub**. A server counts as public when its own address is the
one the internet sees (a VPS); with one such server nothing has to be forwarded on any router — the others dial out to
it. With none, forward a UDP port on the router to one server's mesh port (the outside port may differ, as the agent
port often does), mark that server public and set its endpoint to `host:outside-port`. The page shows the path of
every pair, a live ping matrix, and an iperf3 benchmark per pair. Nothing is ever restarted by the mesh; containers on
docker bridges reach the other servers through a MASQUERADE on `dm0`, and the WireGuard port plus `dm0` are let in
ahead of a host firewall (ufw). In a Proxmox LXC the WireGuard module must be loaded on the Proxmox host
(`modprobe wireguard`).

**A project across servers** — in a project's *Services* tab pick the servers of a service: one moves it
(`x-dockmaster: {agent: <name>}`), several spread its replicas over them (`x-dockmaster: {agents: [a, b]}`, evenly, the
first ones take the rest); the *Servers* tab shows the plan. Each server gets a compose file with only its services and
their share of replicas. A service with `expose: [5432]` stays reachable by name from every server, so `db:5432` keeps
working from anywhere — no code changes: one replica on one server holds its port on the host's mesh address and the
others get an `extra_hosts` entry for it; several replicas are published on ports docker picks, and every server
reaches them through a balancer on its own mesh address (HAProxy in the mesh sidecar: TCP, least connections, a
replica that stops answering is left out until it is back). A service spread over servers goes through the balancer
also where it has replicas of its own, so the replicas on the other servers get their share; point a reverse proxy
on a server at that balancer (`<mesh ip>:<port>`, shown in the *Servers* tab) instead of at the containers. Several
replicas of a service that stays on one server are reached directly there.
Nothing moves by itself: when a server goes down its replicas are gone until it is back, the others keep serving.
UDP ports cannot be balanced; `depends_on` across servers is dropped with a warning (the app must retry its
connection). Deploys go to the servers with exposed services first; a service moved elsewhere is taken down where
it ran before. By default everything goes through the tunnel, encrypted. A network marked **trusted LAN** lets its
members on one LAN (a home network, a provider's private network) talk without it: their mesh addresses are routed
through their LAN addresses, so the latency and CPU of WireGuard go away, but that traffic is not encrypted and anyone on
the LAN can reach the mesh addresses. Everything else — over the internet, through a hub — stays in the tunnel, and a
pair only leaves it when both agents support it.

## Development

```bash
dotnet build Dockmaster.sln
DOCKMASTER_ADMINPASSWORD=changeme DOCKMASTER_DATADIR=./data dotnet run --project src/Dockmaster.Server   # API on :8080
cd src/Dockmaster.Web && npm install && npm run dev                                                # UI on :5173, proxies /api and /hub
npm run build   # writes the production UI into src/Dockmaster.Server/wwwroot
```

## Roadmap

* **Multi-host projects, next steps**: a load balancer the server generates (Traefik) so replicas of a stateless
  service can spread over hosts; a plain-LAN mode that publishes on LAN addresses without the tunnel; a manual
  "re-place on another server" when a server is gone.
* Optional git-backed compose sources.
