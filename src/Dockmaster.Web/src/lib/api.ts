// Thin fetch wrapper for the Dockmaster REST API (same-origin cookie auth).

export class ApiError extends Error {
  status: number
  constructor(status: number, message: string) {
    super(message)
    this.status = status
  }
}

export async function api<T = void>(path: string, init?: RequestInit & { json?: unknown }): Promise<T> {
  const headers: Record<string, string> = { Accept: 'application/json' }
  let body = init?.body
  if (init?.json !== undefined) {
    headers['Content-Type'] = 'application/json'
    body = JSON.stringify(init.json)
  }
  const res = await fetch('/api' + path, { ...init, headers: { ...headers, ...(init?.headers as Record<string, string>) }, body, credentials: 'same-origin' })
  if (res.status === 401 && !path.startsWith('/auth/')) {
    window.location.assign('/login?returnUrl=' + encodeURIComponent(window.location.pathname))
    throw new ApiError(401, 'not signed in')
  }
  if (!res.ok) {
    let message = res.statusText
    try {
      const data = await res.json()
      if (data?.error) message = data.error
    } catch { /* ignore */ }
    throw new ApiError(res.status, message)
  }
  if (res.status === 204) return undefined as T
  const text = await res.text()
  if (!text) return undefined as T
  const ct = res.headers.get('content-type') ?? ''
  return (ct.includes('json') ? JSON.parse(text) : text) as T
}

/** The message of a caught error (an ApiError carries the server's own words). */
export const errorText = (e: unknown) => e instanceof Error ? e.message : String(e)

// ---- types (mirror Api/Dtos.cs) ----
export interface HostStats { cpuPercent: number; memoryUsed: number; memoryTotal: number; diskUsed: number; diskTotal: number; load1: number; uptimeSeconds: number }
export interface Container {
  id: string; name: string; image: string; state: string; health: string; status: string; createdUnix: number; startedUnix: number
  restartCount: number; project: string; service: string; cpuPercent: number; memoryUsage: number; memoryLimit: number; cpuLimit: number
  netRx: number; netTx: number; ports: string[]; imageRef: string; update: string; updateDetail: string | null
  /** no / always / unless-stopped / on-failure; empty from agents that predate it */
  restartPolicy: string; restartMax: number; exitCode: number
  /** how its last run ended, also once it runs again (docker forgets it then); null: not seen since the agent started */
  lastExit: ContainerExit | null
  /** a job on a schedule (x-dockmaster.schedule): exited with 0 is "finished", not "stopped" */
  job?: boolean
  /** the job's schedule in words, the next time and how its last run went; null for a container that is no job */
  jobInfo?: JobInfo | null
  /** client side: the server the container was reported by (a project may span several) */
  agentId?: number
}
export interface Agent {
  id: number; name: string; online: boolean; lastSeenAt: string | null; hostname: string | null; os: string | null; arch: string | null
  dockerVersion: string | null; cpus: number; memoryTotal: number; agentIp: string | null; agentVersion: string | null
  stats: HostStats | null; running: number; total: number; unhealthy: number; statsUnixMs: number; cpuLimits: boolean; agentImage: string
  /** containers of jobs on a schedule; the ones waiting for their next run are not in `total` */
  jobs?: number
  /** limits of the running containers added up */
  limitMemory: number; limitCpus: number
  /** address in the mesh between servers, null = not joined */
  meshIp: string | null; meshUp: boolean
}
export interface AgentDetail { agent: Agent; containers: Container[] }
export interface NewAgentResponse { id: number; token: string; fingerprint: string; command: string }
export interface ServiceSummary {
  name: string; image: string; replicas: number; memoryLimit: string | null; cpuLimit: string | null; hasHealthcheck: boolean
  hasContainerName: boolean
  /** `container_name:` as written; null = compose names the containers (project-service-1, -2…) */
  containerName?: string | null
  ports: number; rolling: boolean; readiness: string; updateDelay: string | null; monitor: string | null; parallelism: number
  /** x-dockmaster.agent / agents: the servers this service runs on (several: its replicas are spread); empty = the project's server */
  agentNames: string[]
  /** compose `expose`: ports the other services of the project may use, from any server */
  expose: string[]
  /** x-dockmaster.autoscale; null = the replica count stays as written */
  autoscale: AutoscaleSpec | null
  /** the service's `volumes:` in order: where it keeps its data */
  mounts: MountSummary[]
  /** the secrets it reads (its `secrets:` list) */
  secrets: string[]
  /** the published ports as written ("8080:80"); null = written with options, only the compose file changes them */
  portList: string[] | null
  /** the environment as written; null = cannot be shown as name = value */
  environment: { name: string; value: string }[] | null
  /** `restart:` as written */
  restart: string | null
  /** x-dockmaster.balance: a balancer holds the fixed ports and spreads the connections over the replicas */
  balance?: boolean
  /** the most replicas the fixed host ports let run on one server; null = nothing limits them */
  portRoom?: number | null
  /** x-dockmaster.schedule: a job Dockmaster starts on a cron schedule, which does its work and exits; null = kept running */
  schedule?: ScheduleSpec | null
}
/** A job's last run, in short. `ok` null with a `finishedAt`: cut short (the container was removed, or its end was not seen). */
export interface JobRunBrief { startedAt: string; finishedAt: string | null; ok: boolean | null; exitCode: number | null; note: string }
/** What a job's container shows: its schedule in words, the next time (UTC), its last run. */
export interface JobInfo { schedule: string; cron: string; timeZone: string | null; nextAt: string | null; lastRun: JobRunBrief | null }
/** When a job runs: a cron expression, the time zone its times are in (null = UTC) and how long a run may take ("1h", null = no limit). */
export interface ScheduleSpec { cron: string; timeZone: string | null; timeout: string | null }
/** One run of a job. `trigger`: schedule / manual (run now) / other (a deploy, docker, someone on the server). */
export interface JobRun {
  id: number; startedAt: string; finishedAt: string | null; exitCode: number | null; ok: boolean | null; trigger: string
  /** "stopped after the 1h timeout", "out of memory", "retried 2 times" */
  note: string
  /** the container's last log lines, kept when the run failed */
  logTail: string
}
/** Where a job stands: its schedule in words, the next time, whether a run is going on, its last runs. */
export interface JobStatus {
  service: string; cron: string; description: string; timeZone: string | null
  /** the time zone is not known to the server: UTC is used */
  zoneProblem: string | null
  timeout: string | null; nextAt: string | null
  /** running / waiting (for the next time) / undeployed (no container yet) / never (the expression names no time) */
  state: 'running' | 'waiting' | 'undeployed' | 'never'
  runningSince: string | null
  runs: JobRun[]
}
/** A cron expression checked by the server: in words, and the next times it names (UTC). */
export interface SchedulePreview { ok: boolean; error: string | null; description: string; next: string[]; zoneProblem: string | null }
/** Several replicas and a fixed host port: a balancer holds the port, or each replica takes a port of a range. */
export type PortMode = 'balance' | 'range'
/** A file of a project (nginx.conf…): written to <project>/files/ on deploy, mounted as ./files/<name>. */
/** hidden: the content is not shown (it takes the Secrets permission); size: its bytes all the same */
export interface ProjectFile { name: string; content: string; updatedAt: string; hidden?: boolean; size?: number }
/**
 * One entry of a service's `volumes:`. volume = a docker volume (docker keeps the data), bind = a folder of the server,
 * anonymous = a volume docker names itself (a new container gets a new, empty one); long entries may say tmpfs etc.
 */
export interface MountSummary {
  type: 'volume' | 'bind' | 'anonymous' | string
  /** the volume's key in the compose file, or the folder; "" for an anonymous volume */
  source: string
  target: string; readOnly: boolean
  /** false: written with options the page does not show — it can only be removed */
  editable: boolean
  /** the name docker gives the volume on the server */
  volumeName: string | null
  /** an existing volume of the server, not created by the project */
  external: boolean
}
/** A mount as the editor sends it back: `keep` = the index of an entry kept as written, otherwise a new or changed one. */
export interface MountEdit { keep: number | null; type: string; source: string; target: string; readOnly: boolean; externalName?: string | null }
/** A container that mounts a volume. */
export interface VolumeUser { name: string; project: string; service: string; destination: string; rw: boolean; running: boolean }
/** A docker volume of a server (Agent/Docker/VolumeStore.cs). */
export interface Volume {
  name: string; driver: string; mountpoint: string; created: number
  /** the compose project that created it, and its key in that compose file */
  project: string; composeName: string
  /** made by docker for an image's VOLUME nobody named: a re-created container gets a new, empty one */
  anonymous: boolean
  /** bytes, -1 = not measured (yet) */
  size: number
  containers: VolumeUser[]
  /** "nfs 10.0.0.5:/export" for a share of another machine; null = on this server */
  share: string | null
  labels: Record<string, string>
}
export interface VolumeList { volumes: Volume[]; images: Record<string, string[]> }
/** The volumes of every server of a project, and the paths the services' images keep data in. */
export interface ProjectVolumes {
  servers: { agentId: number; agentName: string; error: string | null; volumes: Volume[] | null }[]
  /** service → the paths its image declares (VOLUME) */
  imagePaths: Record<string, string[]>
}
/** replicas between deploy.replicas and max, by the CPU of the running ones against their limit */
export interface AutoscaleSpec {
  max: number
  /** % of the CPU limit (average of the replicas) above which one more replica starts */
  cpu: number
  /** servers extra replicas may go to; empty = the servers the service runs on */
  agentNames: string[]
  /** auto starts and removes replicas; recommend only says what it would do */
  mode: 'auto' | 'recommend'
}
export interface ProjectListItem {
  id: number; name: string; agentId: number; agentName: string; services: string[]; updatedAt: string; lastDeployedAt: string | null
  lastDeployOk: boolean | null; running: number; total: number; problems: number; updates: number
  /** every server the project's services are placed on, the default one first */
  servers: string[]
  /** containers of jobs on a schedule; the ones waiting for their next run are not in `total` */
  jobs?: number
  /** the template the project is an instance of; null for a project made by hand */
  template?: string | null
}
/** balanced: several replicas, reached through balancers; otherwise the one replica holds the ports on the address */
export interface ExposedService { service: string; ports: string[]; address: string; balanced: boolean }
export interface HostPlan {
  agentId: number; agentName: string; online: boolean; inMesh: boolean; meshIp: string | null; services: string[]; exposed: ExposedService[]; composeYaml: string
  /** service → its replicas on this server */
  replicas: Record<string, number>
  /** services this server reaches through a balancer of its own (their replicas run elsewhere) */
  balancers: string[]
}
/** how a project is spread over servers and what would stop a deploy */
export interface ProjectPlan { spanning: boolean; hosts: HostPlan[]; problems: string[]; warnings: string[] }
export interface PlacementResult { project: Project; plan: ProjectPlan }
export interface Link { service: string; fromHost: string; toHost: string; ports: string[]; address: string }
export interface Connection { projectId: number; name: string; hosts: string[]; links: Link[]; problems: boolean }
export interface Project {
  id: number; name: string; agentId: number; composeYaml: string; envFile: string; healthTimeoutSeconds: number; updatedAt: string
  lastDeployedAt: string | null; lastDeployOk: boolean | null; services: ServiceSummary[]
  /** set for an instance of a template: its files come from the template on every deploy through the API */
  templateId?: number | null
  /** the .env is left out: the user has no Secrets permission (envFile is empty then) */
  envHidden?: boolean
  /** what the user may do with it: the permissions they have on every server it touches, also ones they do not see */
  may?: Perm[]
}
export interface ProjectSecret { name: string; updatedAt: string }
export interface Deployment {
  id: number; action: string; startedAt: string; finishedAt: string | null; ok: boolean | null; message: string
  /** who started it: the signed-in user ("anna", "anna: rollback to #12"), "webhook", "API key ci" */
  source: string

  /** its compose file and .env are kept: what changed since, going back to it */
  hasFiles: boolean
}
/** A deploy's files next to the project's files now. */
export interface DeploymentFiles { composeYaml: string; envFile: string; currentComposeYaml: string; currentEnvFile: string; envHidden?: boolean }
export interface Registry { id: number; name: string; url: string; username: string; insecure: boolean; hasPassword: boolean; aliases: string }
export interface Settings {
  agentPublicUrl: string; agentImage: string; fingerprint: string; fingerprintPretty: string
  meshSubnet: string; meshPort: number; meshInUse: boolean
  /** a tunnel MTU set by hand for every host, 0 = each host's from its measured paths; BBR and bigger TCP buffers on the hosts */
  meshMtu: number; meshTcpTuning: boolean
}
/** hasPassword: someone can sign in (false only on the first run, which makes the first admin) */
export interface Me { authenticated: boolean; hasPassword: boolean; user?: MeUser | null }

// ---- users, roles and what they may do (Api/Dtos/UserDtos.cs, Access/Perm.cs) ----
export type Perm = 'view' | 'control' | 'deploy' | 'edit' | 'secrets' | 'terminal' | 'volumes' | 'server'
/** The signed-in user: an admin may do everything; anyone else what their roles give them, server by server. */
export interface MeUser {
  id: number; name: string; isAdmin: boolean
  /** permissions on every server, also ones added later */
  everywhere: Perm[]
  /** server id → permissions on it (on top of `everywhere`) */
  servers: Record<string, Perm[]>
}
/** A role on a server; agentId 0 = on every server. */
export interface Grant { agentId: number; roleId: number }
export interface User { id: number; name: string; isAdmin: boolean; disabled: boolean; grants: Grant[]; createdAt: string; lastLoginAt: string | null }
/** users: the names of the users who have it somewhere */
export interface Role { id: number; name: string; description: string; permissions: Perm[]; users: string[] }
export interface PermissionInfo { key: Perm; label: string; description: string }

// mesh: the WireGuard network between servers
export interface MeshNode {
  agentId: number; name: string; online: boolean; joined: boolean; meshIp: string | null
  /** reachable from the internet (from the addresses, or publicOverride) */
  public: boolean; publicOverride: boolean | null; endpoint: string | null; endpointOverride: string | null; port: number
  backend: string; hub: boolean; serverHost: boolean; up: boolean; inSync: boolean; problems: string[]
  /** the agent understands mesh commands (older agents do not) */
  supported: boolean; hostIps: string[]; peerIp: string | null
  /** the networks the server is in; only servers sharing one see each other */
  networkIds: number[]
  /** the tunnel MTU its config has, and the smallest packet its paths carry as measured by Tune speed (0 = never measured) */
  mtu: number; pathMtu: number
  /** worth knowing, nothing to fix (userspace WireGuard on a host without the module, no BBR in the kernel) */
  notes: string[]
  /** the host's TCP congestion control ("bbr", "cubic"); "" = not reported */
  tcpCongestion: string
}
/** one direction between two servers of a common network */
export interface MeshPair {
  /** plain: over the LAN of a trusted network, without the tunnel */
  from: number; to: number; path: 'direct' | 'lan' | 'plain' | 'via' | 'none'; viaName: string | null; rttMs: number | null; handshakeAgeS: number | null
  rx: number; tx: number; networkId: number
}
/** an isolated group of servers; hubAgentId = the hub in use, hubOverride = the one the user picked; trusted = members on one LAN talk without the tunnel */
export interface MeshNetwork { id: number; name: string; hubAgentId: number | null; hubOverride: number | null; members: number[]; trusted: boolean }
/** hints: what would make the mesh faster (nothing is wrong); mtu: set by hand for every host in Settings, 0 = each host's measured one */
export interface Mesh { subnet: string; port: number; networks: MeshNetwork[]; nodes: MeshNode[]; pairs: MeshPair[]; problems: string[]; hints: string[]; mtu: number; tcpTuning: boolean }

/** What the agent's InspectContainer returns (Agent/Docker/ContainerDetails.cs). */
export interface ContainerDetails {
  id: string; name: string; image: string; imageId: string; created: string; platform: string
  command: string; entrypoint: string[]; cmd: string[]; workingDir: string; user: string; hostname: string
  state: {
    status: string; running: boolean; oomKilled: boolean; exitCode: number; error: string; startedAt: string; finishedAt: string; restartCount: number
    health: { status: string; failingStreak: number; log: { start: string; end: string; exitCode: number; output: string }[] } | null
  }
  healthcheck: { test: string[]; interval: number; timeout: number; retries: number } | null
  env: { name: string; value: string; fromImage: boolean }[]
  labels: Record<string, string>
  restartPolicy: string | null; maxRetries: number | null
  memoryLimit: number; memoryReservation: number; cpus: number; cpuShares: number; privileged: boolean
  networkMode: string | null; logDriver: string | null
  networks: { name: string; ip: string; prefix: number; gateway: string; mac: string; aliases: string[] }[]
  ports: { container: string; published: string[] }[]
  mounts: { type: string; name: string; source: string; destination: string; mode: string; rw: boolean }[]
  /** the values of env are left out: the user has no Secrets permission on the server */
  envHidden?: boolean
}

/** The agent's own image has a newer version in the registry (or it predates versioned builds and self-update). */
export const agentOutdated = (a: Agent) => a.agentImage === 'outdated' || a.agentImage === 'redeploy' || a.agentVersion === '1.0.0'

/** The server a compose file names (x-dockmaster.agent): names match case-insensitively. */
export const agentByName = (agents: Agent[], name: string | null | undefined) =>
  name ? agents.find(a => a.name.toLowerCase() === name.trim().toLowerCase()) : undefined

/** Something that happened: a failed deploy, a server gone, a replica short of memory, an autoscaler decision. */
export interface DmEvent {
  id: number; at: string; level: 'info' | 'warning' | 'error'; kind: string
  projectId: number | null; project: string | null; agentId: number | null; agent: string | null; service: string | null; message: string
  /** the container it is about, by name */
  container?: string | null
  /** more to it: the last log lines of a container that stopped */
  details?: string | null
  /** the signed-in user who did it (kind "audit") */
  user?: string | null
}
export interface ContainerExit { unixMs: number; exitCode: number; oom: boolean }
/** [minute, replicas, cpu (% of a core), cpu limit (cores, 0 = none), memory, memory limit (0 = none), rx, tx (bytes/s), memory peak (0 = not known)] */
export type ServicePoint = [number, number, number, number, number, number, number, number, number]
export interface ProjectMetrics { from: number; to: number; services: { service: string; points: ServicePoint[] }[] }
/** [minute, cpu %, memory used, memory total, disk used, disk total, load1] */
export type HostPoint = [number, number, number, number, number, number, number]
export interface HostMetrics { from: number; to: number; points: HostPoint[] }
/** [minute, cpu (% of a core), cpu limit (cores, 0 = none), memory, memory limit (0 = none), rx, tx (bytes/s), memory peak (0 = not known)] */
export type ContainerPoint = [number, number, number, number, number, number, number, number]
export interface ContainerMetrics { from: number; to: number; points: ContainerPoint[] }
export interface NotificationSettings { seqUrl: string; seqApiKeySet: boolean }
/** Where the autoscaler stands with a service. */
export interface AutoscaleStatus {
  service: string; mode: 'auto' | 'recommend'; min: number; max: number; threshold: number
  /** replicas it runs (auto) or would run (recommend) now */
  desired: number
  /** CPU of the replicas in % of their limit, as with `desired` of them */
  cpu: number
  /** server name → replicas it added there */
  extra: Record<string, number>
  state: string; lastChangeAt: string | null; lastChange: string | null
}

// ---- templates, their instances and API keys (Api/Dtos/TemplateDtos.cs) ----
/** pattern: a regular expression the whole value must match; null = a word of letters, digits, _ . - (no / or :) */
export interface TemplateVariable { name: string; pattern: string | null; required: boolean; default: string | null; description: string | null }
export interface Template {
  id: number; name: string; description: string; composeYaml: string; envFile: string; variables: TemplateVariable[]
  /** servers instances may go to; empty = any */
  agentIds: number[]
  /** 0 = no limit */
  maxInstances: number; healthTimeoutSeconds: number; instances: number; updatedAt: string
  /** variables used where the caller's value decides what a container may reach (host paths, ports, the image) */
  warnings: string[]
}
export type InstanceState = 'new' | 'deploying' | 'running' | 'starting' | 'unhealthy' | 'partial' | 'stopped'
export interface Instance {
  name: string; template: string; projectId: number; server: string; state: InstanceState; running: number; total: number
  variables: Record<string, string>; createdAt: string; lastDeployedAt: string | null; lastDeployOk: boolean | null; cpuPercent: number; memoryUsage: number
}
export interface InstanceResult { ok: boolean; message: string; instance: Instance | null }
export interface ApiKey { id: number; name: string; hint: string; templateIds: number[]; templates: string[]; createdAt: string; lastUsedAt: string | null }
export interface NewApiKeyResponse { apiKey: ApiKey; key: string }
