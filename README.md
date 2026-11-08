# Arcadia Games: backend en Azure

Este repositorio conserva el despliegue existente de Azure Static Web Apps y su workflow de GitHub Actions. La API integrada se ha enfocado en autenticación, sincronización de bibliotecas y llamadas seguras a IGDB/Groq para el cliente Flutter de `arcadia_games`.

## Arquitectura

- Azure Static Web Apps Free sirve el contenido estático de `src`.
- El mismo deployment publica `api` como Azure Functions .NET isolated 8.
- Azure SQL contiene cuentas, sesiones y una copia JSON versionada por usuario.
- Flutter mantiene SQLite/Drift como almacenamiento local y sincroniza cambios al iniciar sesión, al modificar datos (con debounce), al volver a primer plano o manualmente.
- Cosmos DB no es necesario para la nueva API y no se consulta.

El cliente Flutter no se publica desde este workflow. Para que la app móvil/web se conecte a la API, compílala con `ARCADIA_API_BASE_URL` apuntando al hostname de esta Static Web App.

## Despliegue actual

Se mantiene sin cambios `.github/workflows/azure-static-web-apps-jolly-mud-018639f0f.yml`:

- `app_location: src`
- `api_location: api`
- API runtime: `dotnet-isolated:8.0`
- rama de despliegue: `main`

Los endpoints de Azure Functions integrados se publican bajo `/api/*` en el mismo hostname del sitio.

## Preparar Azure SQL

Ejecuta [`api/schema.sql`](./api/schema.sql) **una vez en la base Azure SQL existente**. Crea únicamente las tablas nuevas `ArcadiaUsers`, `ArcadiaSessions` y `ArcadiaLibraries`; no crea otra base ni borra las tablas antiguas. La API nueva no compila ni publica las funciones anteriores.

La tabla de biblioteca guarda una sola instantánea JSON por cuenta. Cada escritura valida `baseRevision` en una transacción serializable; una revisión antigua recibe HTTP 409 y no reemplaza los datos actuales.

## Application Settings

Configura estas variables en la configuración de la API integrada de Static Web Apps:

| Variable | Obligatoria | Uso |
| --- | --- | --- |
| `SQL_CONNECTION_STRING` | Sí | Conexión cifrada a Azure SQL. |
| `IGDB_CLIENT_ID` | Para IGDB | Client ID guardado solo en el backend. |
| `IGDB_CLIENT_SECRET` | Para IGDB | Secreto guardado solo en el backend. |
| `GROQ_API_KEY` | Para IA | Clave guardada solo en el backend. |
| `GROQ_MODEL` | No | Modelo de Groq; predeterminado `llama-3.1-8b-instant`. |

No agregues estas credenciales al repositorio, al cliente Flutter ni a `--dart-define`. Rota las claves que anteriormente estuvieran incluidas en la app y guarda las nuevas solo en Application Settings o en secretos locales excluidos de Git.

## API

Todas las rutas requieren HTTPS. Las rutas de cuenta son públicas; sincronización e integración externa requieren `Authorization: Bearer <token>`.

### Cuenta

- `POST /api/auth/register` — `{ "email", "password", "displayName" }`; contraseña de 12 a 128 caracteres.
- `POST /api/auth/login` — `{ "email", "password" }`.
- `GET /api/auth/me` — datos de la cuenta autenticada.
- `POST /api/auth/logout` — revoca la sesión actual.

Las contraseñas se guardan con PBKDF2-SHA256 y salt aleatorio. Los tokens aleatorios de sesión se guardan como hashes y vencen en 30 días.

### Sincronización

- `GET /api/sync/snapshot` — devuelve `revision`, `snapshot` y `updatedAt`.
- `PUT /api/sync/snapshot` — recibe `{ "baseRevision": n, "snapshot": { "schemaVersion": 1, ... } }`.

El cliente combina cambios independientes entre la última base común, el dispositivo y la nube. Si ambos modificaron el mismo registro/campo, presenta la elección; hasta entonces no sube una revisión que sobrescriba a la otra. El payload máximo es 2 MB.

### Proveedores externos

- `POST /api/igdb/query` — recibe `{ "endpoint": "games|collections|franchises", "query": "..." }`.
- `POST /api/groq/chat/completions` — proxy autenticado con modelo fijado en el backend, tamaño/token acotados y límite temporal por usuario.

## Desarrollo local

1. Restaura y compila la API:

   ```powershell
   cd api
   dotnet restore
   dotnet build
   ```

2. Usa `local.settings.json` ignorado por Git para las variables necesarias y ejecuta `func start`.
3. Compila Flutter indicando el hostname de Static Web Apps:

   ```powershell
   flutter run --dart-define=ARCADIA_API_BASE_URL=https://<tu-static-web-app>.azurestaticapps.net
   ```

El sitio estático sigue teniendo su ciclo de publicación habitual. El plan Free no cobra el alojamiento de los archivos estáticos dentro de sus cuotas; las funciones integradas y Azure SQL sí tienen límites propios. Revisa consumo y la opción de pausa/overage de Azure SQL antes de producción.
