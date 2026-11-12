# User management

Internal account workspace: Angular 20 standalone SPA, ASP.NET Core 8 controllers, official MongoDB.Driver 2.30.0. Implemented secure sign-in, administrator account creation/read/update/soft deactivation, searchable paginated directory, own-profile/password management, and administrator audit/dashboard views. No public signup or password-recovery action exists.

## Local development

Requirements: .NET SDK 8.0.428, Node 24, npm, MongoDB replica set `umrs` (or an authenticated Atlas deployment). Both tiers build independently. Do not use a standalone Mongo instance for coordinated writes.

The delivered `backend/appsettings.Development.json` contains randomly generated, synthetic development secrets and two configurable seed accounts. It is ignored, excluded from images, and never suitable for production. Inspect it privately for account credentials; the UI deliberately shows no credential hints. Set `Seed:AdminUsername/AdminPassword` and `Seed:UserUsername/UserPassword` before the first startup if desired. Seed upserts preserve existing passwords/roles; changing seed config does **not** reset existing accounts.

After a fresh checkout, generate the ignored config once from the repository root (this command refuses to overwrite existing config):

```bash
python3 - <<'PY'
from pathlib import Path
import json, secrets, base64
p = Path('backend/appsettings.Development.json')
key = lambda: base64.b64encode(secrets.token_bytes(32)).decode()
data = {
  'Mongo': {'Uri': 'mongodb://127.0.0.1:27017/?replicaSet=umrs', 'Database': 'user_management_development'},
  'Jwt': {'Secret': secrets.token_urlsafe(48), 'Issuer': 'user-management-api', 'Audience': 'user-management-client', 'ExpiryMinutes': 15},
  'Security': {'IdentifierKey': key(), 'BlindIndexKey': key()},
  'Seed': {'Enabled': True, 'AdminUsername': 'development.admin', 'AdminPassword': secrets.token_urlsafe(18), 'UserUsername': 'development.user', 'UserPassword': secrets.token_urlsafe(18)},
  'Cors': {'AllowedOrigins': ['http://127.0.0.1:4200']}
}
with p.open('x') as output:
    json.dump(data, output, indent=2)
p.chmod(0o600)
PY
```

Run the API and SPA in separate terminals:

```bash
$HOME/.dotnet/dotnet run --project backend/UserManagement.csproj
cd frontend
npm ci --no-bin-links --no-audit --no-fund
node node_modules/@angular/cli/bin/ng.js serve --host 127.0.0.1
```

Visit `http://127.0.0.1:4200/`. The local proxy uses `API_PROXY_TARGET` (default `http://127.0.0.1:5088`). Shipped browser code uses only same-origin `/api`; nginx reads `BACKEND_ORIGIN` at container startup. No dotenv package is used: ASP.NET reads appsettings and environment variables. Per-tier `.env.example` files document deployment keys, not automatically loaded runtime input. Dummy secret values in those examples are intentionally rejected.

## Contracts and boundaries

- Public `POST /api/login` accepts **only** `{username,password}` and returns `{accessToken,tokenType,expiresAt}`. Username is exact-case, 3–64 safe ASCII letters/digits/dot/underscore/hyphen. Password is 12–72 UTF-8 bytes; bcrypt cost 12, no silent truncation.
- Public `GET /api/health` pings real Mongo: 200 `{status:"healthy"}`, or 503 safe dependency error. Startup refuses unavailable migration/seed dependencies.
- Errors have `type,title,status,errors,correlationId`. Unknown JSON members are rejected; errors never echo credentials, identifiers or Mongo internals. API responses use `Cache-Control: no-store`.
- HS256 tokens use operator-configured secret/issuer/audience, 15-minute lifetime and zero skew. Every protected call additionally checks Mongo account existence/activity, matching role and persisted `credential_version`. Fallback authorization requires authentication; the `Admin` policy is reusable for later administrator endpoints. Test-only gate controllers exist solely in the test assembly.
- Browser tokens are **memory-only**. Reload/closing the tab loses the session. Sign-out clears token, role and shared feedback; it does not claim remote revocation. Existing tokens remain valid until expiry or persisted status/version change. No refresh endpoint exists. Token decoding on the client is presentation-only; server validation is authoritative.
- Roles `User`/`Admin` and `credential_version` are internal persisted policy fields, never client-writable DTO properties. Seed role assignment is operator-only.

## Persistence and security seams

`users` and `logs` are separate collections because audit activity is unbounded and references the actor via ObjectId. One pooled MongoClient is used in production. User DTO projection is explicit and excludes hash, identifiers, role and credential version. Repository methods are real typed Mongo operations, not domain HTTP endpoints.

Login success/failure audits are majority-acknowledged **before** the response. Audit contains exactly `_id`, optional `user_id`, `action`, `status`, `timestamp`; an unknown actor is omitted, never invented. Audit vocabulary is `login`, `user.create`, `user.read`, `user.list`, `user.update`, `user.deactivate`, `profile.update`, `password.change`. Timestamps persist as UTC BSON milliseconds. No payload/IP/user-agent metadata or TTL is added.

Account and profile mutation services use `MongoStore.TransactionAsync`, passing its session to user and audit repositories. Snapshot reads/majority commits provide atomicity; abort on failure, no standalone/non-atomic fallback and bounded retries only after a transaction abort, with fresh validation. Tests prove paired commit and rollback.

`IdentifierProtector` implements AES-256-GCM with fresh 12-byte nonce, 16-byte tag and field-name associated data. Stored values are `v1.` followed by base64 nonce/tag/ciphertext. Separate HMAC-SHA256 keys produce deterministic **field-separated blind indexes** (`aadhaar_index`, `pan_index`) for exact-case equality search without persisted searchable plaintext. This leaks equality/frequency, not plaintext; keys require controlled custody/rotation and synthetic-only development identifiers. Encryption and blind-index keys must be independent. No plaintext identifier is returned in safe DTOs. Account services protect optional synthetic identifiers before persistence; responses never return them.

The repeatable `username_unique_v1` migration preflights collisions, uses Mongo simple collation and never deletes/rewrites records or drops conflicting indexes. Data/index collisions stop startup for operator resolution. Additional query indexes require workload evidence.

## Tests and builds

xUnit service tests and WebApplicationFactory HTTP tests use generated `um_test_<guid>` databases, real Mongo, real startup migration/seeding and safe cleanup of only their owned database. `TestApplication.Service<T>()` enables direct real-service tests. Set `TEST_MONGO_URI` for a different test replica set.

Sandbox database processes may not persist between commands. Before **each** database test command, start Mongo in the same shell if not already reachable, using existing initialized data only, then wait for primary:

```bash
set -e
if ! mongosh --quiet --eval 'db.adminCommand({ping:1}).ok' >/dev/null 2>&1; then
  mongod --dbpath "$HOME/.local/share/user-management-mongo" --replSet umrs --bind_ip 127.0.0.1 --port 27017 --nounixsocket --fork --logpath "$HOME/.local/share/user-management-mongo/mongod.log"
fi
for i in $(seq 1 30); do
  if mongosh --quiet --eval 'quit(db.hello().isWritablePrimary ? 0 : 1)' >/dev/null 2>&1; then break; fi
  sleep 1
done
mongosh --quiet --eval 'quit(db.hello().isWritablePrimary ? 0 : 1)'
$HOME/.dotnet/dotnet test backend/tests/UserManagement.Tests --filter FullyQualifiedName~AuthenticationTests
# Use the identical Mongo setup before running this filter:
$HOME/.dotnet/dotnet test backend/tests/UserManagement.Tests --filter FullyQualifiedName~AuthenticationApiTests
```

Do not restart/delete shared DB data or initialize an existing replica set again. Native Angular component tests exercise actual reactive forms, inline errors, loading, retry, interceptor boundaries, session expiry/clearing and inert recovery. They isolate HTTP transport using Angular's native HttpTestingController, not mocked E2E traffic.

```bash
cd frontend
node node_modules/@playwright/test/cli.js install chromium
# Sandbox full Chrome fails to launch; its installed headless shell works for Karma:
CHROME_BIN="$HOME/.cache/ms-playwright/chromium_headless_shell-1208/chrome-headless-shell-linux64/chrome-headless-shell" node node_modules/@angular/cli/bin/ng.js test --watch=false --browsers=ChromeHeadlessNoSandbox --include=src/app/auth/auth.spec.ts
node node_modules/@angular/cli/bin/ng.js build
# From repository root:
$HOME/.dotnet/dotnet publish backend/UserManagement.csproj -c Release
```

Playwright config is tier-local, launches both real servers, uses desktop/mobile Chromium and captures unexpected browser errors before navigation. Intentional 401 resource-console messages are explicitly annotated; uncaught JS errors still fail. Authentication journeys enter once, then click links, assert real login response content, sign-out and inert recovery. Credentials come privately from ignored config, with independent role overrides: `E2E_ADMIN_USERNAME`/`E2E_ADMIN_PASSWORD` for Admin and `E2E_USER_USERNAME`/`E2E_USER_PASSWORD` for User. Each supplied pair must contain both non-empty values; a missing role pair falls back to that role's ignored seed config. Legacy global `E2E_USERNAME`/`E2E_PASSWORD` overrides are not used. Start Mongo before execution. The parent owns test-critic review, rendered accessibility preflight, cross-feature integration, and E2E execution:

```bash
cd frontend
node node_modules/@playwright/test/cli.js test
```

## Containers

Independent multistage API and Angular/nginx images. Local Compose provisions a **development-only** single-node replica set with a named volume and no exposed Mongo port, a repeatable init job, backend and frontend. It does not replace external production DB operations or supply production auth/TLS.

The ignored development config is mounted read-only, not baked into image layers. Set local UID/GID so the unprivileged backend can read the private file:

```bash
LOCAL_UID=$(id -u) LOCAL_GID=$(id -g) docker compose up --build -d
```

Visit `http://127.0.0.1:8080/`; use `docker compose logs` for diagnostics. Never use `down -v` on valuable data. Production should omit the config mount, use `ASPNETCORE_ENVIRONMENT=Production`, disable seed, and inject all Mongo/JWT/security configuration from managed secrets. Missing/known-default or low-diversity keys fail closed. The Compose topology is not production-safe Mongo hosting.

## Design and remaining release gates

New minimalist internal tool: self-hosted Source Sans 3, deep-teal action/focus accent, warm neutrals, hairlines/small radii and functional 150ms motion; no gradients or decorative animation. Native labels, associated validation alerts, status live regions, skip link and reduced-motion support. Contrast-safe foregrounds are selected; rendered mobile/accessibility/manual review remains the parent's verification duty.

Production legal/identifier-processing policy, key custody and rotation/migration, external Mongo authentication/TLS/backups/restore/retention, edge HTTPS/CSP, incident response, abuse limits and numeric performance SLO approval remain release gates. No AWS provisioning, compliance certification, restore guarantee or performance target is claimed. Recovery, bulk operations and export are not enabled. All six feature slices are complete. Development verification passed 413 backend tests (including two cross-feature chains), all per-feature Angular suites, and all 16 desktop/mobile Playwright journeys after a responsive navigation fix. Both production builds and Docker Compose build/start passed. Automated journey and semantic review is not WCAG certification; manual assistive-technology review and formal security/performance/restore assessment remain release gates.
