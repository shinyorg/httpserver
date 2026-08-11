# Remote ADO.NET bridge — administering a device's database from a desktop tool

**Status:** plan only — nothing built.
**Target version:** 1.0.0 (`version.json` → `1.0.0-beta.{height}`; release notes go under the existing
`## 1.0.0 TBD` heading in the documentation repo).
**Scope:** two new packages in this repo — `Shiny.Net.HttpServer.Ado` (device half) and
`Shiny.Net.HttpServer.Ado.Client` (tool half) — plus a sample and docs. A small, separately-tracked
follow-up lands in the **documentdb** repo (`~/Desktop/dev/documentdb`) to teach ShinyDocDbMyAdmin
about the new connection kind.

---

## 1. The goal that started this

Debugging a database inside a running .NET MAUI app. Today that means adb pull / a container copy /
a device file browser, and by the time the file is on a desktop it is a snapshot of a state you can
no longer reproduce.

The obvious ask was "run ShinyDocDbMyAdmin inside the MAUI app, served by this server". That is the
wrong shape, and section 3 says why. What this plan builds instead is the inverse: **the phone
exposes its database, and the admin tools you already have connect to it as if it were local.**

## 2. Why an ADO.NET seam is the right one

`ShinyDocDbMyAdmin.Core` never touches `IDocumentStore`. `DocumentAdminService` is built against
`DbConnection` / `DbCommand` / `DbDataReader`, with SQL text produced by `IDatabaseProvider`'s
dialect members (`~/Desktop/dev/documentdb/src/ShinyDocDbMyAdmin.Core/Services/DocumentAdminService.cs:19`).
Every operation opens, uses and disposes a connection through one gate
(`.../Services/AdminConnection.cs:21`).

Two properties of that code make a remote `DbConnection` unusually cheap:

1. **Reads go through `reader.GetValue()`, not typed getters.** 19 of the 21 reader calls in
   `ShinyDocDbMyAdmin.Core` are `GetValue`, normalized afterwards by `Services/Ado.cs` — which
   already exists precisely because nine ADO.NET drivers return different CLR types for the same
   column. A JSON-transported value that arrives as a `string` where SQLite would have handed back a
   `string` is not a new problem for this code; it is the problem `Ado.cs` was written for.
2. **Parameters are bound by name through one helper** (`Ado.Bind`, always `@name` form), so there is
   exactly one place parameters are created and one convention to carry over the wire.

Put a `DbConnection` implementation over HTTP under that, and **everything above it works unchanged**:
the Blazor web UI, the Docker Desktop extension, and the `shinydocdb` terminal tool — browse, edit,
SQL console, JSON indexes, temporal diff, full-text, blobs, vectors, geometry.

## 3. Alternatives considered

| Option | Verdict |
| --- | --- |
| **Port the Blazor Server app onto this server** | Impossible as such. `ShinyDocDbMyAdmin` is `Microsoft.NET.Sdk.Web` + Blazor Server: SignalR circuits and `MapRazorComponents` are ASP.NET Core, which is the thing this server exists because MAUI cannot have. |
| **Ship `ShinyDocDbMyAdmin.Core` into the app and serve a UI from the device** | Deferred, not dead. Core is ASP.NET-free, but it project-references ten relational providers (Oracle, SqlServer, Npgsql, MySql, DuckDB, Cockroach…) and explicitly disables the trim/AOT analyzers. Getting it into a MAUI head means a provider-pluggable Core, an `IDocumentAdminService` extraction, ~40–60 endpoints, and porting 9.6k LOC of Razor from Server to WebAssembly. This server already has `UseBlazorWebAssembly` (including the embedded-in-assembly overload), so the door stays open — but it is an order of magnitude more work for a strictly smaller feature set than section 2. Revisit if handing a tester a device with no desktop attached becomes a real requirement. |
| **MCP instead of a UI** | Complementary, tracked separately. `Shiny.DocumentDb.Mcp` depends on `ModelContextProtocol.AspNetCore` so it cannot load on MAUI, but `Shiny.Net.HttpServer.Mcp` exists and is now AOT-clean, so a re-hosted read-only tool surface would let an agent query a live device. That is a documentdb-repo plan, and it is not a database browser. |

## 4. What gets built here

```
   desktop                                    device (MAUI app)
┌───────────────────────┐               ┌────────────────────────────────┐
│ ShinyDocDbMyAdmin     │               │  Shiny.Net.HttpServer          │
│  (web / TUI / ext.)   │               │   └ Shiny.Net.HttpServer.Ado   │
│         │             │   HTTP/1.1    │        │                       │
│  DocumentAdminService │   HTTP/2      │   AdoBridgeModule (endpoints)  │
│         │             │  ─────────▶   │        │                       │
│  RemoteDatabaseProv.  │   + tunnel    │   SessionManager               │
│         │             │               │        │                       │
│  RemoteDbConnection ──┼───────────────┼──▶ SqliteConnection (the app's) │
│  (Ado.Client)         │               │                                │
└───────────────────────┘               └────────────────────────────────┘
```

Neither package knows anything about Shiny.DocumentDb. The bridge carries SQL and rows; what those
rows mean is the caller's business. That keeps this repo's charter intact — the only dependencies are
`System.Data.Common` (in the box) and, for the device half, this server's own core.

### 4.1 Packages

| Project | Depends on | Notes |
| --- | --- | --- |
| `src/Shiny.Net.HttpServer.Ado` | `Shiny.Net.HttpServer`, `Microsoft.Extensions.*` abstractions | The device half. Analyzers on, AOT/trim clean, must publish clean under `PublishAot`. |
| `src/Shiny.Net.HttpServer.Ado.Client` | nothing but the BCL | The tool half. Plain library, no reference to the server core — a desktop tool should not drag a socket server in to talk to one. |
| `src/Shiny.Net.HttpServer.Ado.Shared` *(not a package)* | — | Wire contract records + `JsonSerializerContext`, `<Compile Include>`-linked into both so there is one definition of the protocol and no third assembly to version. |

Naming is the one thing worth a second opinion before the first commit — `.Ado` is accurate and dull;
`.RemoteData` and `.Sql` were the alternatives. Recorded as an open question (§11).

## 5. The wire protocol

Version-stamped, JSON, source-generated serialization on both sides. Default prefix `/_ado`,
configurable. `protocolVersion` is checked on handshake and a mismatch is a hard error with a
sentence naming both versions — a subtly wrong row decoder is a much worse day than a refused
connection.

### 5.1 Endpoints

| Method + path | Body → response |
| --- | --- |
| `POST /_ado/sessions` | `{}` → `{ sessionId, protocolVersion, providerName, readOnly, serverVersion }`. Opens a real `DbConnection` and runs the app-supplied initializer on it. |
| `DELETE /_ado/sessions/{id}` | closes the connection, rolls back any open transaction |
| `POST /_ado/sessions/{id}/commands` | `{ sql, parameters[], mode, transactionId?, maxRows?, timeoutSeconds? }` → shape depends on `mode` (§5.2) |
| `POST /_ado/sessions/{id}/cursors/{cursorId}` | → next page of rows; only exists when a reader exceeded `maxRows` |
| `DELETE /_ado/sessions/{id}/cursors/{cursorId}` | disposes an abandoned reader |
| `POST /_ado/sessions/{id}/transactions` | `{ isolationLevel? }` → `{ transactionId }` |
| `POST /_ado/sessions/{id}/transactions/{txId}/commit` \| `/rollback` | → `204` |
| `GET /_ado/info` | unauthenticated-by-default liveness + `protocolVersion` only. Says nothing about the database. |

Sessions are the unit of connection affinity, and they exist because transactions do:
`DocumentAdminService` opens one in five places (`DocumentAdminService.Crud.cs:111,198,293`,
`.Vectors.cs:491`, `ImportExportService.cs:305`) and runs arbitrary C# between commands inside it, so
a stateless "post SQL, get rows" endpoint cannot serve it.

Idle sessions expire (default 5 minutes) and return `410 Gone`. The client reopens transparently on
the next command **unless a transaction was open**, in which case it surfaces the failure — silently
restarting a connection that was mid-transaction is how you get half-written data with no error.

### 5.2 Command modes

- `nonquery` → `{ recordsAffected }`
- `scalar` → `{ value }` (encoded per §5.3)
- `reader` → `{ columns: [{ name, clrTypeName, dbTypeName, allowNull }], rows: [[…]], complete: bool, cursorId? }`

The admin's browse paths already paginate (`IDatabaseProvider.BuildPaginationClause`), so `maxRows`
(default 1000) is a backstop for the SQL console and export paths, not the normal case. When it trips,
the host keeps the reader open behind a `cursorId` and the client pages through it — which is what
`DbDataReader.ReadAsync()` looks like from above anyway.

### 5.3 Value encoding

Values travel as plain JSON, with the column's `clrTypeName` (from `reader.GetFieldType`) telling the
client what to reconstruct:

| CLR type | JSON | Client returns from `GetValue` |
| --- | --- | --- |
| `null` / `DBNull` | `null` | `DBNull.Value` |
| `string` | string | `string` |
| `bool` | bool | `bool` |
| `int` / `short` / `byte` | number | that type |
| `long` | number, or string when \|v\| > 2^53 | `long` |
| `double` / `float` | number (non-finite → string) | that type |
| `decimal` | string | `decimal` |
| `DateTime` / `DateTimeOffset` | ISO-8601 round-trip string | that type |
| `Guid` | string | `Guid` |
| `byte[]` | base64 string | `byte[]` |
| anything else | `ToString()` | `string` |

`byte[]` is the one to watch: `DocumentAdminService.Blobs.cs:88` reads a whole blob with
`(byte[])reader.GetValue(0)`. Base64 in a JSON row is fine at the sizes a debug session deals with;
the host enforces a configurable per-value ceiling (default 8 MB) and returns a typed error above it
rather than OOMing a phone. Streaming blob transfer is explicitly out of scope for the first cut.

### 5.4 Errors

`DbException`s come back as `application/problem+json` carrying `{ message, exceptionTypeName,
providerErrorCode?, sqlState? }`, and the client rethrows a `RemoteDbException : DbException`.

`providerErrorCode` is load-bearing, not decoration: `SqliteDatabaseProvider.IsDuplicateKeyException`
is `ex is SqliteException && ex.SqliteErrorCode == 19`
(`~/Desktop/dev/documentdb/src/Shiny.DocumentDb.Sqlite/SqliteDatabaseProvider.cs:237`). A remote
exception is not a `SqliteException`, so without the code on the wire every unique-constraint
violation would be reported as an unknown failure.

## 6. The client shim (`Shiny.Net.HttpServer.Ado.Client`)

The bulk of the work. Five `System.Data.Common` subclasses over one `HttpClient`:

- `RemoteDbConnection` — `Open`/`OpenAsync` create the session; `Close`/`Dispose` end it. `State`,
  `Database`, `DataSource`, `ServerVersion` come from the handshake. `BeginDbTransaction` posts a
  transaction and returns a `RemoteDbTransaction`.
- `RemoteDbCommand` — picks `mode` from which `Execute*` was called, sends `CommandText` +
  parameters, maps `CommandTimeout`, honours the `CancellationToken` by cancelling the HTTP request
  (the host cancels the `DbCommand` with the aborted request).
- `RemoteDbParameter` / `RemoteDbParameterCollection` — a straightforward `DbParameter`
  implementation; the only real requirement is round-tripping `ParameterName` and `Value`, since
  `Ado.Bind` sets nothing else.
- `RemoteDbDataReader` — over a page of rows plus an optional cursor. `GetValue`, `IsDBNull`,
  `GetName`, `GetOrdinal`, `GetFieldType`, `FieldCount`, `HasRows`, `RecordsAffected`, `ReadAsync`,
  the typed getters (implemented over `GetValue` + `Convert`), and `NextResult() => false`.
- `RemoteDbTransaction` — `Commit`/`Rollback` post to the session; `Dispose` without either rolls back.

The connection string is the configuration surface, so a saved profile stays a string:
`Url=http://192.168.1.20:8080;Token=…;Timeout=30`.

**Synchronous over async.** ADO.NET's sync methods are not optional — `DbConnection.Open()`,
`ExecuteReader()`, `Read()` all exist and `ShinyDocDbMyAdmin` calls the async forms throughout, but a
`DbConnection` that throws on the sync path is a trap for anything else that picks this up. Sync
methods block on the async ones via a dedicated `TaskFactory` with no captured context. Documented,
not hidden.

## 7. The device host (`Shiny.Net.HttpServer.Ado`)

Registration is deliberately a decision, not a default:

```csharp
#if DEBUG
services.AddAdoBridge(opts =>
{
    opts.ConnectionFactory = sp => new SqliteConnection(AppPaths.DbPath);
    opts.InitializeConnection = SqliteInitializers.Default;   // UDFs, PRAGMAs — runs device-side
    opts.ReadOnly = false;
    opts.PathPrefix = "/_ado";
});
#endif
```

and mounted as an `IEndpointModule`, so it can be mounted and unmounted while the server runs — a
debug surface that appears when a toggle flips is the example the module docs themselves give
(`src/Shiny.Net.HttpServer/Endpoints/IHttpEndpoint.cs:53`).

Notes that matter:

- **The initializer runs on the device.** `SqliteDatabaseProvider.InitializeConnectionAsync` registers
  a `soundex` UDF and the spatial functions on the real `SqliteConnection`
  (`.../SqliteDatabaseProvider.cs:63`). Because the host opens the real connection and runs the real
  initializer, those keep working over the bridge and `SupportsSoundex`/`SupportsUserFunctions` stay
  truthfully `true`.
- **One command at a time per session.** A `SemaphoreSlim` per session, mirroring
  `AdminConnection.Execute` — SQLite locks the database on writes and holds one connection.
- **A second connection to the app's own file is fine** under WAL with a `busy_timeout`, which is what
  `ConnectionFactory` above does. Apps that would rather share the store's connection can hand back
  the one Shiny.DocumentDb already holds; the option takes a factory precisely so that is the app's
  call and not ours.
- **Read-only is enforced by the engine, not by sniffing SQL.** `opts.ReadOnly` appends `Mode=ReadOnly`
  to the SQLite connection string (and refuses `transactions`/`nonquery` requests). Deciding whether
  a statement writes by parsing it is a losing game.

## 8. Security posture — locked decisions

This endpoint executes arbitrary SQL against the app's data. Treat that as the headline, not a caveat.

| Decision | Why |
| --- | --- |
| Off unless registered, and the sample gates registration behind `#if DEBUG` | The failure mode of a forgotten toggle is a shipped app with a remote SQL console. |
| `AddAdoBridge` throws when no authentication is configured on the server, unless `opts.AllowAnonymous = true` is set explicitly | An opt-out you had to type is a decision; a default you never saw is an accident. |
| Documented as JWT (`Shiny.Net.HttpServer.Jwt`) or Basic, plus the authentication middleware **in front of** the module | Same reasoning as the MAUI sample's `RequireAuthenticationMiddleware` (`samples/Sample.Maui/Server/RequireAuthentication.cs`) — endpoint authorization runs after routing. |
| `UseIpFilter` / `RequireRateLimiting` shown in the docs; loopback + LAN is the assumed default | Tunnelling this to the public internet (`.AzureRelay`, `.Ssh`) is a thing you should have to mean. |
| No credential ever crosses the bridge in the other direction | SQLCipher keys stay on the device: the host opens the encrypted database itself, the desktop only ever sees rows. |
| Warning banner in the readme, the docs page and the skill | Not one sentence buried in an options table. |

## 9. AOT and trim

`src/Directory.Build.props` turns the analyzers on for every shipping project and neither package gets
an exemption. Concretely: all serialization goes through a `JsonSerializerContext` in the shared
contract, registered with `JsonTypeInfoRegistry.Register(...)`; the value encoder switches on
`Type` with no `JsonSerializer.Serialize(object)` anywhere; and the device package publishes clean
under `PublishAot` as part of CI. The client package has the easier job but keeps the same posture,
because a desktop tool that gets published AOT one day should not discover this the hard way.

## 10. Testing

`tests/Shiny.Net.HttpServer.Tests`, against an in-memory or temp-file SQLite database:

1. **Round-trip conformance.** The core test: run a query directly against `SqliteConnection`, run the
   identical query through the bridge, assert the two `DbDataReader`s agree column-for-column,
   row-for-row, on `GetValue`, `GetFieldType`, `GetName`, `IsDBNull` and `FieldCount`. Table-driven
   over every type in the §5.3 matrix, including `null`, empty `byte[]`, an 8-byte-overflow `long`,
   a non-finite `double` and a `DateTimeOffset` with a non-UTC offset.
2. **Transactions.** Commit persists, rollback does not, dispose-without-either rolls back, a command
   sent with a stale `transactionId` fails cleanly.
3. **Sessions.** Idle expiry returns `410`; the client transparently reopens outside a transaction and
   surfaces the error inside one; a closed session's cursors are disposed.
4. **Cursors.** A result larger than `maxRows` pages correctly and totals the same as the direct read.
5. **Errors.** A unique-constraint violation arrives with `providerErrorCode == 19`.
6. **Security.** Registration without authentication throws; an unauthenticated request gets `401`;
   `ReadOnly` refuses a write with an engine error rather than a parse-based one.
7. **AOT.** The device package publishes clean under `PublishAot`.

An end-to-end test that drives `DocumentAdminService` itself belongs in the documentdb repo (§11).

## 11. The documentdb-side follow-up (separate repo, separate plan file)

Small, and worth writing down here so the shape of it is not rediscovered later:

1. **Four members on `SqliteDatabaseProvider` become `virtual`** — `CreateConnection`,
   `InitializeConnectionAsync`, `RequiresSingleConnection` and `IsDuplicateKeyException`. They are
   non-virtual today (`Shiny.DocumentDb.Sqlite/SqliteDatabaseProvider.cs:53,56,63,237`), and because
   `IDatabaseProvider` is implemented implicitly, a `new` member on a subclass would be ignored when
   called through the interface — so `virtual` is required, not stylistic. This is the only change to
   shipped library code and it is source- and binary-compatible.
2. **`RemoteDatabaseProvider : SqliteDatabaseProvider`** overriding exactly those four. It inherits
   the whole SQLite dialect — 100-plus members, of which `IDatabaseProvider` supplies most as
   defaults. That inheritance is the point: a hand-written forwarding decorator would silently fall
   back to interface defaults for any member it forgot, and a wrong-dialect default produces wrong
   SQL rather than a compile error.
3. **`ProviderKind.Remote`** plus a `ProviderDescriptor` in `ProviderCatalog`
   (`ShinyDocDbMyAdmin.Core/Providers/ProviderCatalog.cs`), a connection-string template, and the two
   or three profile fields the connection editor needs. Both front ends pick it up for free — the web
   UI, the TUI and the Docker extension all read the catalog.

Sizing the whole thing: ~700–900 LOC for the client shim, ~400–500 for the host, ~200 shared contract,
~600 tests, and roughly 150 LOC in documentdb.

## 12. Phases

| # | Phase | Contents |
| --- | --- | --- |
| 1 | Contract | Shared records, `JsonSerializerContext`, value encoder/decoder + its unit tests. The encoder is where correctness is won or lost, so it is tested before anything depends on it. |
| 2 | Client shim | The five `System.Data.Common` types, against a hand-rolled fake host. |
| 3 | Device host | Session manager, command/cursor/transaction endpoints, options, `IEndpointModule`. |
| 4 | Round-trip tests | Section 10.1–10.4 — the first point at which the thing is real. |
| 5 | Security | Authentication guard, read-only mode, limits, rate limiting; tests 10.5–10.7. |
| 6 | Sample + docs | A DocumentDb store in `samples/Sample.Maui` with the bridge behind `#if DEBUG`, and the four artifacts below. |
| 7 | documentdb glue | §11, in that repo, with its own plan file and its own release note. |

## 13. Definition of done

Per `CLAUDE.md`, a change is not done until all four are in sync:

1. **Code + tests** — `src/Shiny.Net.HttpServer.Ado`, `src/Shiny.Net.HttpServer.Ado.Client`,
   `tests/Shiny.Net.HttpServer.Tests`, analyzers on, `PublishAot` clean. Scoped explicitly to
   HTTP/1.1 and HTTP/2 over both direct listeners and `ITunnelProvider`s; WebSockets are not involved.
2. **Documentation site** — a new `ado.mdx` under
   `~/Desktop/dev/documentation/src/content/docs/httpserver/`, a sidebar entry in that repo's
   `astro.config.mjs`, and `<RN type="feature">` notes under the existing `## 1.0.0 TBD` heading in
   `release-notes.mdx`.
3. **Skill** — `skills/shiny-httpserver/SKILL.md` does not exist in this repo yet. When it lands, the
   bridge is a tier-3 (source-generated typed endpoints) API and needs `AddAdoBridge`,
   `RemoteDbConnection` and both package names in the `triggers:` list.
4. **readme.md** — the package table gains two rows and the feature list gains a line.

## 14. Open questions

1. **Package naming** — `.Ado` / `.Ado.Client`, versus `.RemoteData` or `.Sql`. Decide before the
   first commit; renaming a published package is not a thing.
2. **Does the client half belong in this repo at all?** It shares the wire contract, which argues yes,
   but it is not a server and never references one. The alternative is publishing it from documentdb
   and keeping only the contract here.
3. **Discovery.** Typing an IP is fine for one device. mDNS/Bonjour advertisement of the bridge would
   make "which of these three phones" a picker instead of a `route -n` — worth it, but not in the
   first cut.
4. **Should `Shiny.DocumentDb` register the bridge itself?** A one-liner like
   `.AddDocumentDbRemoteAdmin()` in the documentdb repo that wires the factory and initializer from
   the already-registered `IDatabaseProvider` would remove the last piece of boilerplate — but it puts
   a dependency on this server into that repo. Decide when §11 is written, not now.
