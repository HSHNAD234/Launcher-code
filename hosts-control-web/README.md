# HSHONAD Host Registry Web

A protected admin website and PostgreSQL-backed registry for the four fixed domain names used by HSHONAD Launcher. The registry stores enabled/disabled status, purpose, and notes. It does not store IP-routing rules and does not modify the Windows Hosts file.

## Deploy

1. In Render, create a Blueprint and choose `hosts-control-web/render.yaml` as the Blueprint file.
2. Provide `DATABASE_URL` as a secret environment variable for a persistent PostgreSQL database. Keep the connection string private.
3. Set `DATABASE_SSL=true` if your database provider requires TLS; the service validates the database certificate.
4. Render generates `ADMIN_TOKEN`. Copy its value from the service Environment page and use it to sign in.
5. Open the service over HTTPS and edit the domain registry.

## API

- `GET /healthz`: checks database connectivity.
- `GET /api/hosts-config`: public read-only registry JSON for Launcher clients to display/diagnose.
- `GET /api/admin/session`: checks admin session.
- `POST /api/admin/login`: exchanges the configured token for a short-lived HttpOnly cookie.
- `GET /api/admin/hosts`: reads the registry (admin session required).
- `PUT /api/admin/hosts`: validates and atomically saves all four entries (admin session required).
- `GET /api/admin/audit`: reads recent changes (admin session required).

## Local development

Use Node.js 20+ and PostgreSQL. Copy `.env.example` to `.env`, configure the values, then run `npm install`, `npm test`, and `npm start`. Open `http://localhost:10000`.

Do not commit database credentials or `ADMIN_TOKEN`. The Blueprint intentionally does not create a database resource or incur a database charge automatically. Render's free Postgres expires after 30 days; use a persistent database provider for long-lived settings: https://render.com/docs/free.
