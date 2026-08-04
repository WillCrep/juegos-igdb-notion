# Juegos IGDB → Notion

Servicio serverless para crear y enriquecer fichas de videojuegos en Notion usando la información pública de [IGDB](https://www.igdb.com/). El proyecto recibe eventos de Notion, identifica la página creada o modificada, busca el juego en IGDB y escribe el resultado en Notion: propiedades estructuradas, contenido visual, relaciones de series y registros de DLC, expansiones y bundles.

## Objetivo funcional

El usuario crea una página en la base de datos de juegos de Notion indicando el nombre del videojuego. El sistema:

1. Recibe el evento `page.created` mediante un webhook de Notion.
2. Valida la firma HMAC-SHA256 del evento.
3. Busca hasta diez coincidencias en IGDB.
4. Selecciona una coincidencia exacta o suficientemente próxima por nombre y versión.
5. Consulta el detalle completo del juego en IGDB.
6. Actualiza las propiedades de la página de Notion.
7. Agrega una ficha visual con portada, resumen, imágenes, videos y fechas de lanzamiento.
8. Sincroniza la serie o colección relacionada.
9. Sincroniza DLC, expansiones, expansiones independientes y bundles en una base relacionada de Notion.
10. Marca el resultado como `Completado` o `Revisión manual`.

También se procesan eventos `page.properties_updated`, pero las ediciones automáticas producidas por el propio servicio se ignoran. Una página solo se vuelve a procesar manualmente cuando tiene estado `Revisión manual` y un valor en `IGDB ID`.

## Arquitectura

```text
Notion (base de juegos)
        │ page.created / page.properties_updated
        ▼
Azure Static Web Apps
  ├── Frontend estático: src
  └── API integrada: api (Azure Functions .NET isolated)
        │
        ├── IGDB / Twitch OAuth2: búsqueda y detalle del juego
        ├── Notion API: lectura, actualización y creación de páginas
        ├── Cosmos DB Free Tier: configuración y documentos JSON auxiliares
        └── Application Insights / OpenTelemetry: observabilidad
```

### Componentes del repositorio

| Ruta | Responsabilidad |
| --- | --- |
| `src/index.html` | Página estática mínima y enlace de health check. |
| `src/staticwebapp.config.json` | Runtime de la API, rutas y fallback del frontend. |
| `api/NotionWebhook.cs` | Endpoint HTTP del webhook y validación de firma. |
| `api/GameEnrichmentService.cs` | Orquestación del enriquecimiento y sincronización con Notion. |
| `api/igdbClient.cs` | OAuth2 contra Twitch y consultas a IGDB v4. |
| `api/NotionClient.cs` | Cliente HTTP para Notion API. |
| `api/Health.cs` | Endpoint de salud de la API. |
| `api/AiConsultant.cs` | Endpoints del consultor IA y administración de perfiles. |
| `api/AiConsultantService.cs` | Cliente Groq, modos de consulta y persistencia en Cosmos DB. |
| `api/Program.cs` | Inyección de dependencias, HttpClient y Application Insights. |
| `.github/workflows/...yml` | CI/CD hacia Azure Static Web Apps. |

## Funcionalidades implementadas

### Enriquecimiento de juegos

La integración consulta en IGDB:

- nombre, resumen y URL oficial;
- fecha de lanzamiento y rating;
- portada, screenshots y artworks;
- géneros, plataformas, franquicias, desarrolladores y publishers;
- videos de YouTube;
- fechas de lanzamiento por plataforma y región;
- clasificación por edades e idiomas soportados;
- colección/serie relacionada;
- DLC, expansiones, expansiones independientes y bundles.

La portada se guarda como archivo externo en Notion usando la URL CDN de IGDB. Las imágenes y videos se agregan como bloques de contenido. Los datos principales se conservan como propiedades de la página para permitir búsquedas y filtros.

### Estados y revisión manual

El flujo utiliza los estados:

- `Procesando`: la página está siendo consultada y actualizada.
- `Completado`: se encontró y sincronizó un juego.
- `Revisión manual`: no hubo coincidencia automática o existen varias coincidencias ambiguas.

Cuando se requiere revisión, el servicio agrega a la página las coincidencias disponibles con sus IDs de IGDB. Para reintentar, el usuario debe colocar el estado `Revisión manual` y completar `IGDB ID`; el siguiente evento de edición usa ese ID directamente.

### Bases relacionadas en Notion

La base de juegos puede relacionarse con:

- una base de series/colecciones, creada o actualizada a partir de la colección IGDB;
- una base de DLC, donde cada registro se identifica por el `IGDB ID`, evitando duplicados.

La sincronización de series mantiene información de la colección y puede generar una checklist con los juegos faltantes. La sincronización de DLC guarda el tipo de relación, fecha, plataformas y URL cuando esos datos están disponibles.

## Azure utilizado

### Azure Static Web Apps — plan Free

Se utiliza Azure Static Web Apps como punto de publicación único para el frontend y la API:

- entrega el contenido estático de `src`;
- publica `api` como Azure Functions;
- configura el runtime `dotnet-isolated:9.0`;
- expone la API bajo `/api/*`;
- ejecuta el despliegue desde GitHub Actions;
- usa el token `AZURE_STATIC_WEB_APPS_API_TOKEN_JOLLY_MUD_018639F0F` almacenado como secreto de GitHub.

El workflow se ejecuta en `push` a `main` y en pull requests dirigidos a `main`. La configuración actual usa `src` como `app_location`, `api` como `api_location` y no requiere una carpeta de salida adicional.

Endpoint publicado:

```text
GET https://<static-web-app>.azurestaticapps.net/api/health
```

Respuesta esperada:

```json
{
  "status": "ok",
  "service": "juegos-igdb"
}
```

### Azure Cosmos DB — Free Tier

Cosmos DB se contempla en el plan Free Tier para almacenar documentos JSON de configuración y otros datos auxiliares que no deben vivir dentro del frontend ni en el repositorio. Es adecuado para:

- configuración dinámica del proyecto;
- catálogos o parámetros JSON;
- metadatos auxiliares de sincronización;
- futuras cachés o estados de procesamiento.

La fuente de verdad funcional de los juegos y sus relaciones continúa siendo Notion. En la implementación visible de este repositorio no existe todavía una clase o paquete de acceso a Cosmos DB; por ello, Cosmos debe configurarse como dependencia de infraestructura solo cuando se conecte ese almacenamiento desde la API.

El Free Tier tiene límites de capacidad y consumo definidos por Azure. Deben verificarse en la suscripción antes de producción y no se debe almacenar allí información sensible sin definir previamente cifrado, permisos y retención.

### Observabilidad

La API registra eventos mediante `ILogger` y está preparada para Application Insights y OpenTelemetry. Se registran, entre otros, el estado de procesamiento, el ID de página de Notion, el ID de IGDB y los errores de integración.

## Tecnologías

- C# y .NET 9.
- Azure Functions v4, modelo isolated worker.
- Azure Static Web Apps.
- Azure Cosmos DB Free Tier para JSON/configuración auxiliar.
- Notion API y Notion Webhooks.
- IGDB API v4.
- Twitch OAuth2 Client Credentials.
- GitHub Actions.
- Application Insights y OpenTelemetry.

## Variables de configuración

Configurar estas variables como Application Settings de la Function App/Static Web App o en `local.settings.json` para desarrollo local:

| Variable | Uso | Obligatoria |
| --- | --- | --- |
| `IGDB_CLIENT_ID` | Client ID de IGDB/Twitch. | Sí |
| `IGDB_CLIENT_SECRET` | Secret para obtener el token OAuth2. | Sí |
| `NOTION_TOKEN` | Token de integración de Notion. | Sí |
| `NOTION_VERIFICATION_TOKEN` | Token utilizado para validar la firma del webhook. | Sí |
| `NOTION_GAME_SERIES_PROPERTY` | Nombre de la propiedad de relación con series. Por defecto: `Serie`. | No |
| `NOTION_SERIES_DATA_SOURCE_ID` | Data source de la base de series. | No, si se usa `NOTION_SERIES_DATABASE_ID` |
| `NOTION_SERIES_DATABASE_ID` | Database ID de series; permite resolver el data source. | No, si se usa `NOTION_SERIES_DATA_SOURCE_ID` |
| `NOTION_DLC_DATA_SOURCE_ID` | Data source de la base de DLC. | No, si se usa `NOTION_DLC_DATABASE_ID` |
| `NOTION_DLC_DATABASE_ID` | Database ID de DLC; permite resolver el data source. | No, si se usa `NOTION_DLC_DATA_SOURCE_ID` |
| `GROQ_API_KEY` | Token gratuito de Groq. Se usa únicamente en el backend. | Sí, para el consultor |
| `GROQ_MODEL` | Modelo compatible de Groq. Por defecto: `llama-3.1-8b-instant`. | No |
| `COSMOS_ENDPOINT` | URI de la cuenta Cosmos DB. | Sí, para guardar perfiles |
| `COSMOS_KEY` | Clave de la cuenta Cosmos DB. | Sí, para guardar perfiles |
| `COSMOS_DATABASE_ID` | Base de datos de perfiles. Por defecto: `AiConsultant`. | No |
| `COSMOS_CONTAINER_ID` | Contenedor JSON. Por defecto: `PromptProfiles`. | No |
| `COSMOS_PARTITION_KEY_PATH` | Ruta de partición si el contenedor se crea desde cero. Por defecto: `/id`. Para `configurations`, usar `/partitionKey`. | No |
| `AI_ADMIN_KEY` | Clave para crear/editar/eliminar prompts personalizados. | Sí, para administrar |

Si no se configura la base de series o DLC, el enriquecimiento del juego continúa, pero se omite esa sincronización.

No guardar secretos en el repositorio. Para Azure usar Application Settings/secretos de GitHub; para local usar `local.settings.json` y mantenerlo fuera de control de versiones.

## Requisitos de Notion

La integración de Notion debe tener acceso a:

- la base de datos principal de juegos;
- la base de datos de series, si se desea sincronización de colecciones;
- la base de datos de DLC, si se desea sincronización de contenidos relacionados.

La base principal debe contener, como mínimo, una propiedad de título y las propiedades empleadas por el servicio, entre ellas `Estado` e `IGDB ID`. Las propiedades enriquecidas usadas por la implementación incluyen:

`Estado`, `IGDB ID`, `IGDB rating`, `IGDB URL`, `Franquicia`, `Desarrolladores`, `Publishers`, `Género`, `Plataformas IGDB`, `Última sincronización`, `Año`, `Resumen`, `Portada` y `Serie`.

El webhook debe apuntar a:

```text
POST https://<static-web-app>.azurestaticapps.net/api/NotionWebhook
```

Durante la verificación inicial, Notion envía `verification_token`. Ese valor debe guardarse en `NOTION_VERIFICATION_TOKEN` y el webhook debe configurarse con la suscripción a eventos de la base de juegos.

## API

### `GET /api/health`

Comprueba que la Function App está disponible. No requiere autenticación.

### `POST /api/NotionWebhook`

Procesa eventos de Notion. Acepta:

- `page.created`;
- `page.properties_updated`.

Los demás tipos de evento se responden como ignorados. La API valida `X-Notion-Signature` usando HMAC-SHA256 sobre el cuerpo original y `NOTION_VERIFICATION_TOKEN` como clave.

## Ejecución local

Requisitos:

- .NET 9 SDK;
- Azure Functions Core Tools v4;
- credenciales de IGDB/Twitch;
- una integración y páginas de prueba en Notion.

Crear `api/local.settings.json` a partir de este ejemplo, sin subir valores reales:

```json
{
  "IsEncrypted": false,
  "Values": {
    "AzureWebJobsStorage": "UseDevelopmentStorage=true",
    "FUNCTIONS_WORKER_RUNTIME": "dotnet-isolated",
    "IGDB_CLIENT_ID": "...",
    "IGDB_CLIENT_SECRET": "...",
    "NOTION_TOKEN": "...",
    "NOTION_VERIFICATION_TOKEN": "...",
    "NOTION_SERIES_DATA_SOURCE_ID": "...",
    "NOTION_DLC_DATA_SOURCE_ID": "..."
  }
}
```

Ejecutar la API:

```powershell
cd api
dotnet restore
dotnet build
func start
```

El health check local queda disponible en `http://localhost:7071/api/health`.

Para probar el frontend estático, servir `src` con cualquier servidor HTTP estático. El frontend actual es intencionalmente mínimo y funciona como punto de entrada y comprobación de la API.

## Despliegue

El despliegue recomendado es:

1. Crear una Azure Static Web App en el plan Free.
2. Configurar el repositorio y la rama `main`.
3. Mantener `src` como aplicación y `api` como backend.
4. Configurar las variables de entorno en Azure.
5. Configurar el webhook de Notion con la URL de producción.
6. Crear Cosmos DB en Free Tier únicamente para los documentos JSON auxiliares que requiera la solución.
7. Validar `/api/health` y procesar una página de prueba.

El workflow de GitHub Actions ya incluido realiza el build y deploy mediante `Azure/static-web-apps-deploy@v1`. Los pull requests generan entornos de revisión y, al cerrarse, se ejecuta el job de limpieza de ese entorno.

## Consideraciones de seguridad y operación

- El endpoint del webhook es anónimo a nivel de Azure Functions porque Notion necesita invocarlo; la protección real se realiza mediante `X-Notion-Signature`.
- No exponer `IGDB_CLIENT_SECRET`, `NOTION_TOKEN` ni `NOTION_VERIFICATION_TOKEN` en logs, HTML o commits.
- Validar que las bases de Notion compartidas con la integración tengan únicamente los permisos necesarios.
- Respetar los límites y las políticas de uso de IGDB, Notion y los planes gratuitos de Azure.
- Las URLs de imágenes de IGDB se almacenan como referencias externas; no se descargan ni se duplican los archivos en el repositorio.
- En caso de fallos de IGDB o Notion, revisar Application Insights y los logs de la Function App.
- La API procesa bloques de Notion en lotes de hasta 100 elementos para respetar el límite de la API.

## Estado actual y extensiones posibles

## Consultor IA embebible

La portada en `src/index.html` incluye un widget responsive en JavaScript vanilla. Puede publicarse directamente en la Static Web App o embeberse mediante iframe desde una página de Notion. El navegador nunca recibe `GROQ_API_KEY`: las consultas pasan por la API.

Endpoints:

- `GET /api/ai/prompts`: devuelve los perfiles disponibles (solo metadatos públicos).
- `POST /api/ai/chat`: recibe `{ "question": "...", "mode": "general", "promptId": "..." }` y devuelve `{ "answer": "..." }`.
- `POST /api/ai/prompts`: crea o actualiza un perfil JSON. Requiere `x-ai-admin-key`.
- `DELETE /api/ai/prompts/{id}`: elimina un perfil personalizado. Requiere `x-ai-admin-key`.

Los modos incluidos son `general`, `games` y `achievements`. El modo de logros pide explícitamente juego, plataforma y edición cuando sea relevante; así se evita mezclar listas de PlayStation, Xbox, Steam, Epic, Nintendo u otras versiones. El backend conserva los prompts personalizados en el contenedor `PromptProfiles`, con `id` como clave de partición. Si se reutiliza `configurations` con partición `/partitionKey`, los perfiles nuevos usan `partitionKey: "prompts"`. Si Cosmos DB no está configurado, los tres perfiles incluidos siguen funcionando, pero no se pueden guardar perfiles nuevos.

Implementado: webhook firmado, health check, búsqueda y detalle IGDB, enriquecimiento de páginas, contenido visual, revisión manual, sincronización de series y sincronización de DLC/expansiones/bundles.

Extensiones naturales: conectar formalmente el cliente de Cosmos DB, incorporar caché de tokens/consultas, agregar reintentos con backoff, introducir una cola para procesamiento asíncrono y añadir pruebas automatizadas para la selección de coincidencias y la validación de firmas.

## Licencia y fuentes

El proyecto consume datos de IGDB y utiliza la API de Notion. Revisar sus términos, límites y requisitos de atribución antes de publicar el servicio. Las fichas generadas incluyen una referencia a `https://www.igdb.com/` como fuente de los datos.
