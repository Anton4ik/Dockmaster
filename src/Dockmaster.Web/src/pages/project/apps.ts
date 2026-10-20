/**
 * Ready-made apps for a new service: the image, the ports people open, the settings it needs (passwords made up
 * here) and where it keeps its data. Only what works as one container; anything else starts from "Custom image".
 */
export interface AppTemplate {
  id: string
  title: string
  /** one line for a person who does not know the app */
  text: string
  /** service name */
  name: string
  image: string
  /** "8080:80": the server's port : the container's */
  ports: string[]
  env: { name: string; value: string }[]
  /** where it keeps its data: a volume named <service>-<suffix> at each path */
  data: { suffix: string; target: string }[]
  /** after the deploy: how to reach it / log in */
  hint?: string
}

/** a password nobody has to remember (it stays in the compose file, readable in the project) */
export function password(length = 20) {
  const chars = 'abcdefghijkmnpqrstuvwxyzABCDEFGHJKLMNPQRSTUVWXYZ23456789'
  const bytes = crypto.getRandomValues(new Uint8Array(length))
  return Array.from(bytes, b => chars[b % chars.length]).join('')
}

export function templates(): AppTemplate[] {
  return [
    { id: 'nginx', title: 'Nginx', text: 'Web server for a static site or a reverse proxy', name: 'web', image: 'nginx:1.27-alpine', ports: ['8080:80'], env: [],
      data: [{ suffix: 'html', target: '/usr/share/nginx/html' }], hint: 'open http://<server>:8080' },
    { id: 'postgres', title: 'PostgreSQL', text: 'Relational database', name: 'db', image: 'postgres:17', ports: [],
      env: [{ name: 'POSTGRES_USER', value: 'app' }, { name: 'POSTGRES_PASSWORD', value: password() }, { name: 'POSTGRES_DB', value: 'app' }],
      data: [{ suffix: 'data', target: '/var/lib/postgresql/data' }], hint: 'other services reach it as db:5432' },
    { id: 'mysql', title: 'MySQL', text: 'Relational database', name: 'mysql', image: 'mysql:8.4', ports: [],
      env: [{ name: 'MYSQL_ROOT_PASSWORD', value: password() }, { name: 'MYSQL_DATABASE', value: 'app' }, { name: 'MYSQL_USER', value: 'app' }, { name: 'MYSQL_PASSWORD', value: password() }],
      data: [{ suffix: 'data', target: '/var/lib/mysql' }], hint: 'other services reach it as mysql:3306' },
    { id: 'mariadb', title: 'MariaDB', text: 'MySQL-compatible database', name: 'mariadb', image: 'mariadb:11', ports: [],
      env: [{ name: 'MARIADB_ROOT_PASSWORD', value: password() }, { name: 'MARIADB_DATABASE', value: 'app' }, { name: 'MARIADB_USER', value: 'app' }, { name: 'MARIADB_PASSWORD', value: password() }],
      data: [{ suffix: 'data', target: '/var/lib/mysql' }], hint: 'other services reach it as mariadb:3306' },
    { id: 'redis', title: 'Redis', text: 'In-memory cache and queue', name: 'redis', image: 'redis:7-alpine', ports: [], env: [],
      data: [{ suffix: 'data', target: '/data' }], hint: 'other services reach it as redis:6379' },
    { id: 'mongo', title: 'MongoDB', text: 'Document database', name: 'mongo', image: 'mongo:7', ports: [],
      env: [{ name: 'MONGO_INITDB_ROOT_USERNAME', value: 'root' }, { name: 'MONGO_INITDB_ROOT_PASSWORD', value: password() }],
      data: [{ suffix: 'data', target: '/data/db' }], hint: 'other services reach it as mongo:27017' },
    { id: 'rabbitmq', title: 'RabbitMQ', text: 'Message broker with a web console', name: 'rabbitmq', image: 'rabbitmq:3-management', ports: ['15672:15672'],
      env: [{ name: 'RABBITMQ_DEFAULT_USER', value: 'admin' }, { name: 'RABBITMQ_DEFAULT_PASS', value: password() }],
      data: [{ suffix: 'data', target: '/var/lib/rabbitmq' }], hint: 'console at http://<server>:15672; services reach it as rabbitmq:5672' },
    { id: 'adminer', title: 'Adminer', text: 'Database admin in the browser (Postgres, MySQL…)', name: 'adminer', image: 'adminer:4', ports: ['8081:8080'], env: [], data: [],
      hint: 'open http://<server>:8081, server: the database service\'s name' },
    { id: 'grafana', title: 'Grafana', text: 'Dashboards and charts', name: 'grafana', image: 'grafana/grafana:11.2.0', ports: ['3000:3000'],
      env: [{ name: 'GF_SECURITY_ADMIN_PASSWORD', value: password() }], data: [{ suffix: 'data', target: '/var/lib/grafana' }], hint: 'open http://<server>:3000, user admin' },
    { id: 'uptime-kuma', title: 'Uptime Kuma', text: 'Watches websites and sends alerts', name: 'uptime', image: 'louislam/uptime-kuma:1', ports: ['3001:3001'], env: [],
      data: [{ suffix: 'data', target: '/app/data' }], hint: 'open http://<server>:3001' },
    { id: 'seq', title: 'Seq', text: 'Log server with search', name: 'seq', image: 'datalust/seq:latest', ports: ['5341:80'], env: [{ name: 'ACCEPT_EULA', value: 'Y' }],
      data: [{ suffix: 'data', target: '/data' }], hint: 'open http://<server>:5341' },
  ]
}

const q = (s: string) => JSON.stringify(s)

/** A whole compose file with just this app, for a new project. */
export function composeFor(t: AppTemplate): string {
  const lines = ['services:', `  ${t.name}:`, `    image: ${t.image}`, '    restart: unless-stopped']
  if (t.ports.length) lines.push('    ports:', ...t.ports.map(p => `      - ${q(p)}`))
  if (t.env.length) lines.push('    environment:', ...t.env.map(e => `      ${e.name}: ${q(e.value)}`))
  if (t.data.length) {
    lines.push('    volumes:', ...t.data.map(d => `      - ${q(`${t.name}-${d.suffix}:${d.target}`)}`))
    lines.push('volumes:', ...t.data.map(d => `  ${t.name}-${d.suffix}: {}`))
  }
  return lines.join('\n') + '\n'
}
