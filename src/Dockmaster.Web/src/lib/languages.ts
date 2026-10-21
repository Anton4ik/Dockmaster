export type CodeLanguage = 'yaml' | 'properties' | 'json' | 'ini' | 'toml' | 'nginx' | 'plain'

/** The colours for a file by its name: nginx.conf, appsettings.json, config.yml… */
export function languageOf(name: string): CodeLanguage {
  const n = name.toLowerCase()
  if (n.endsWith('.yml') || n.endsWith('.yaml')) return 'yaml'
  if (n.endsWith('.json')) return 'json'
  if (n.endsWith('.toml')) return 'toml'
  if (n.endsWith('.ini') || n.endsWith('.cnf')) return 'ini'
  if (n.includes('nginx') || n.endsWith('.conf')) return 'nginx'
  if (n.endsWith('.env') || n.endsWith('.properties')) return 'properties'
  return 'plain'
}
