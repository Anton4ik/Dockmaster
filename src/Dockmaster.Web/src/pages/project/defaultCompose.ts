/** What a new project starts with: a small service that shows the rolling update settings. */
export const DEFAULT_COMPOSE = `services:
  web:
    image: nginx:alpine
    restart: always
    healthcheck:
      test: ["CMD", "wget", "-qO-", "http://127.0.0.1/"]
      interval: 10s
      timeout: 5s
      retries: 3
      start_period: 10s
    deploy:
      replicas: 2
      update_config:
        parallelism: 1   # replicas replaced at a time
        delay: 5s        # pause before the next one
      resources:
        limits:
          memory: 128M
`
