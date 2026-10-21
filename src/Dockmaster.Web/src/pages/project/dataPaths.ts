import type { MountSummary, ProjectVolumes, ServiceSummary } from '../../lib/api'

/**
 * Where well-known images keep their data, for when the server has not pulled the image yet (then it tells us
 * itself: the image's VOLUME). Matched on the image's last path part, without the tag.
 */
const KNOWN: Record<string, string> = {
  postgres: '/var/lib/postgresql/data', postgis: '/var/lib/postgresql/data', timescaledb: '/var/lib/postgresql/data',
  mysql: '/var/lib/mysql', mariadb: '/var/lib/mysql', mongo: '/data/db', redis: '/data', valkey: '/data', keydb: '/data',
  rabbitmq: '/var/lib/rabbitmq', elasticsearch: '/usr/share/elasticsearch/data', opensearch: '/usr/share/opensearch/data',
  clickhouse: '/var/lib/clickhouse', 'clickhouse-server': '/var/lib/clickhouse', influxdb: '/var/lib/influxdb2', mssql: '/var/opt/mssql',
  'mssql-server': '/var/opt/mssql', grafana: '/var/lib/grafana', prometheus: '/prometheus', loki: '/loki', minio: '/data', seq: '/data',
  portainer: '/data', 'uptime-kuma': '/app/data', gitea: '/data', vaultwarden: '/data', n8n: '/home/node/.n8n', jenkins: '/var/jenkins_home',
  registry: '/var/lib/registry', nextcloud: '/var/www/html', wordpress: '/var/www/html', nginx: '/usr/share/nginx/html', httpd: '/usr/local/apache2/htdocs',
}

const baseName = (image: string) => image.split('@')[0].split('/').pop()!.split(':')[0].toLowerCase()

/** The paths the service's image keeps data in: what the server read from the image, else what is known of it. */
export function dataPaths(s: ServiceSummary, volumes?: ProjectVolumes): string[] {
  const read = volumes?.imagePaths[s.name]
  if (read) return read
  const known = KNOWN[baseName(s.image)]
  return known ? [known] : []
}

/** The image's data paths no mount of the service covers: written there, it is lost when the container is re-created. */
export function unkeptPaths(s: ServiceSummary, volumes?: ProjectVolumes, mounts: Pick<MountSummary, 'type' | 'target'>[] = s.mounts): string[] {
  const kept = mounts.filter(m => m.type !== 'anonymous').map(m => m.target.replace(/\/+$/, ''))
  return dataPaths(s, volumes).filter(p => !kept.some(k => p === k || p.startsWith(k + '/')))
}

/** A volume name nobody in the project uses yet: "db-data", "db-data2", … */
export function freshVolumeName(service: string, taken: Set<string>, hint: string | undefined = 'data') {
  const base = `${service}-${hint}`.toLowerCase().replace(/[^a-z0-9_.-]+/g, '-').replace(/^[^a-z0-9]+/, '')
  let name = base, n = 2
  while (taken.has(name)) name = base + n++
  return name
}
