```yaml
schema: gentle-ai.sdd-research/v1
change: entity-boundary-cleanup
request_id: ebc-aud10-research-01
revision: 3
outcome: partial
scope_closure: >-
  accepted — maintainer-confirmed 2026-09-19. S1 (official) is authoritative for attach
  original-value semantics; residual G1/G2 classified as a documentation limitation
  (medium-confidence inference); conditional note: any future work unit touching
  detached updates requires a bounded runtime confirmation.
outcome_rationale: >-
  Q1/Q3 fully evidenced (revision 2). Q2 evidenced for the tracked-load path and the
  ExecuteUpdate/ExecuteDelete bypass (revision 2) and, after the scoped follow-up
  (revision 3), for the attach original-value semantics (S1, official). The remaining
  detached-Update WHERE-clause crispness is a classified documentation limitation
  (G1/G2), accepted with medium-confidence inference. No unvalidated claim is emitted.
admission:
  - class: documentation
    admitted: true
    observed_grants: [webfetch]
  - class: open-web
    admitted: true
    observed_grants: [websearch, webfetch]
sources_total: 25
claims_total: 26
contradictions: 1 (documentation-level note DC1, revision 3)
gaps_classified: [G1-detached-update-where-clause, G2-default-token-edge-case]
product_choices_total: 6
supersedes: [revision 1 (blocked), revision 2 (head evidence preserved below)]
persisted_by: gentle-orchestrator
persisted_at: 2026-09-19
```

## Questions (revision 2)

- **Q1** (documentation): Official .NET architecture guidance on preventing EF Core entity types from crossing service/API boundaries — when a dedicated read-model DTO is preferred over reusing a broader DTO, and documented risks of exposing entities.
- **Q2** (documentation): Official EF Core/Npgsql documentation on PostgreSQL `xmin` optimistic concurrency — which update paths preserve the concurrency token and which bypass it (`ExecuteUpdate`/`ExecuteDelete`, detached updates).
- **Q3** (documentation): Documented defaults of ASP.NET Core `System.Text.Json` web serialization (casing, case-insensitive matching) and client failure modes (`System.Net.Http.Json`) when a payload does not match the target DTO shape.

## Sources

```json
[
  { "id": "S1", "class": "documentation", "title": "Handling Concurrency Conflicts - EF Core", "publisher": "Microsoft (learn.microsoft.com)", "URL": "https://learn.microsoft.com/en-us/ef/core/saving/concurrency", "accessed_at": "2026-09-19", "excerpt": "The concurrency token is loaded and tracked when an entity is queried - just like any other property. Then, when an update or delete operation is performed during SaveChanges(), the value of the concurrency token on the database is compared against the original value read by EF Core." },
  { "id": "S2", "class": "documentation", "title": "Concurrency Tokens - Npgsql Documentation", "publisher": "The Npgsql Development Team (npgsql.org)", "URL": "https://www.npgsql.org/efcore/modeling/concurrency.html", "accessed_at": "2026-09-19", "excerpt": "All PostgreSQL tables have a set of implicit and hidden system columns, among which xmin holds the ID of the latest updating transaction. Since this value automatically gets updated every time the row is changed, it is ideal for use as a concurrency token." },
  { "id": "S3", "class": "documentation", "title": "ExecuteUpdate and ExecuteDelete - EF Core", "publisher": "Microsoft (learn.microsoft.com)", "URL": "https://learn.microsoft.com/en-us/ef/core/saving/execute-insert-update-delete", "accessed_at": "2026-09-19", "excerpt": "Since ExecuteUpdate and ExecuteDelete do not interact with the change tracker, they cannot automatically apply concurrency control. However, both these methods do return the number of rows that were affected by the operation; this can come particularly handy for implementing concurrency control yourself." },
  { "id": "S4", "class": "documentation", "title": "Disconnected Entities - EF Core", "publisher": "Microsoft (learn.microsoft.com)", "URL": "https://learn.microsoft.com/en-us/ef/core/saving/disconnected-entities", "accessed_at": "2026-09-19", "excerpt": "We then use SetValues to set the values for all properties on this entity to those that came from the client. The SetValues call will mark the entity to be updated as needed... SetValues will only mark as modified the properties that have different values to those in the tracked entity." },
  { "id": "S5", "class": "documentation", "title": "How to enable case-insensitive property name matching with System.Text.Json", "publisher": "Microsoft (learn.microsoft.com)", "URL": "https://learn.microsoft.com/en-us/dotnet/standard/serialization/system-text-json/character-casing", "accessed_at": "2026-09-19", "excerpt": "By default, deserialization looks for case-sensitive property name matches between JSON and the target object properties... Note: The web default is case-insensitive." },
  { "id": "S6", "class": "documentation", "title": "How to instantiate JsonSerializerOptions with System.Text.Json", "publisher": "Microsoft (learn.microsoft.com)", "URL": "https://learn.microsoft.com/en-us/dotnet/standard/serialization/system-text-json/configure-options", "accessed_at": "2026-09-19", "excerpt": "The following options have different defaults for web apps: PropertyNameCaseInsensitive = true; PropertyNamingPolicy = CamelCase; NumberHandling = AllowReadingFromString. In .NET 9 and later versions, you can use the JsonSerializerOptions.Web singleton..." },
  { "id": "S7", "class": "documentation", "title": "Handle unmapped members during deserialization - .NET", "publisher": "Microsoft (learn.microsoft.com)", "URL": "https://learn.microsoft.com/en-us/dotnet/standard/serialization/system-text-json/missing-members", "accessed_at": "2026-09-19", "excerpt": "By default, if the JSON payload you're deserializing contains properties that don't exist in the plain old CLR object (POCO) type, they're simply ignored. Starting in .NET 8, you can specify that all payload properties must exist in the POCO. If they're not, a JsonException exception is thrown during deserialization." },
  { "id": "S8", "class": "documentation", "title": "HttpContentJsonExtensions.ReadFromJsonAsync Method", "publisher": "Microsoft (learn.microsoft.com)", "URL": "https://learn.microsoft.com/en-us/dotnet/api/system.net.http.json.httpcontentjsonextensions.readfromjsonasync", "accessed_at": "2026-09-19", "excerpt": "options - Options to control the behavior during deserialization. The default options are those specified by Web." },
  { "id": "S9", "class": "documentation", "title": "Create Data Transfer Objects (DTOs)", "publisher": "Microsoft (learn.microsoft.com)", "URL": "https://learn.microsoft.com/en-us/aspnet/web-api/overview/data/using-web-api-with-entity-framework/part-5", "accessed_at": "2026-09-19", "excerpt": "Right now, our web API exposes the database entities to the client... However, that's not always a good idea... Remove circular references; Hide particular properties that clients are not supposed to view; Omit some properties in order to reduce payload size; Flatten object graphs; Avoid 'over-posting' vulnerabilities; Decouple your service layer from your database layer." },
  { "id": "S10", "class": "documentation", "title": "Implementing the microservice application layer using the Web API", "publisher": "Microsoft (learn.microsoft.com)", "URL": "https://learn.microsoft.com/en-us/dotnet/architecture/microservices/microservice-ddd-cqrs-patterns/microservice-application-layer-implementation-web-api", "accessed_at": "2026-09-19", "excerpt": "A command is a special kind of Data Transfer Object (DTO), one that is specifically used to request changes or transactions. The command itself is based on exactly the information that is needed for processing the command, and nothing more." },
  { "id": "S11", "class": "documentation", "title": "How to deserialize JSON in C# - .NET", "publisher": "Microsoft (learn.microsoft.com)", "URL": "https://learn.microsoft.com/en-us/dotnet/standard/serialization/system-text-json/deserialization", "accessed_at": "2026-09-19", "excerpt": "Any JSON properties that aren't represented in your class are ignored by default. Also, if any properties on the type are required but not present in the JSON payload, deserialization will fail." },
  { "id": "S12", "class": "documentation", "title": "Part 8, Razor Pages with EF Core in ASP.NET Core - Concurrency", "publisher": "Microsoft (learn.microsoft.com)", "URL": "https://learn.microsoft.com/en-us/aspnet/core/data/ef-rp/concurrency", "accessed_at": "2026-09-19", "excerpt": "On relational databases EF Core checks for the value of the concurrency token in the WHERE clause of UPDATE and DELETE statements to detect a concurrency conflict." },
  { "id": "S13", "class": "open-web", "title": "Simple update token (a.k.a etags) specification - Issue #18121 - dotnet/efcore", "publisher": "GitHub / dotnet/efcore (vendor design discussion)", "URL": "https://github.com/dotnet/efcore/issues/18121", "accessed_at": "2026-09-19", "excerpt": "In cases in which the original values of properties marked for concurrency are not preserved between requests, there is no guarantee of data consistency on concurrent updates... The concurrency token is populated from original values as opposed to current values." }
]
```

## Claims (each mapped to source IDs)

```json
[
  { "id": "C1", "question_id": "Q1", "claim": "Microsoft's own Web API guidance states that exposing database entities directly to clients is 'not always a good idea' and that a DTO should be introduced to reshape the contract.", "source_ids": ["S9"], "confidence": "high", "contradiction": null },
  { "id": "C2", "question_id": "Q1", "claim": "Documented reasons to introduce DTOs instead of exposing entities: remove circular references, hide properties clients must not view, omit properties to reduce payload size, flatten nested graphs, avoid over-posting vulnerabilities, decouple the service layer from the database layer.", "source_ids": ["S9"], "confidence": "high", "contradiction": null },
  { "id": "C3", "question_id": "Q1", "claim": "The guidance defines a narrower purpose-specific DTO for a list endpoint alongside a broader one for a detail endpoint, projecting per endpoint via LINQ Select — when an endpoint needs a different shape/subset, a dedicated DTO is defined rather than reusing the broader one.", "source_ids": ["S9"], "confidence": "high", "contradiction": null },
  { "id": "C4", "question_id": "Q1", "claim": "The .NET microservices architecture guidance states a command DTO is 'based on exactly the information that is needed for processing the command, and nothing more' — contracts should be minimal and purpose-specific.", "source_ids": ["S10"], "confidence": "high", "contradiction": null },
  { "id": "C5", "question_id": "Q2", "claim": "EF Core implements optimistic concurrency via a concurrency token loaded and tracked when the entity is queried; on SaveChanges the database value is compared against the original tracked value, and a mismatch produces a DbUpdateConcurrencyException. On relational databases the original token value is included in the UPDATE/DELETE WHERE clause.", "source_ids": ["S1", "S12"], "confidence": "high", "contradiction": null },
  { "id": "C6", "question_id": "Q2", "claim": "PostgreSQL has no auto-updating rowversion-style column, so Npgsql documents mapping a uint property to the xmin system column (latest updating transaction ID, changes on every row update) as a concurrency token via [Timestamp]/IsRowVersion().", "source_ids": ["S2"], "confidence": "high", "contradiction": null },
  { "id": "C7", "question_id": "Q2", "claim": "The tracked-load path preserves concurrency checking: for a tracked entity, applying incoming values through entry.CurrentValues.SetValues(...) updates the tracked instance (marking only changed properties modified), and SaveChanges still uses the tracked original token value for the conflict check.", "source_ids": ["S1", "S4", "S12"], "confidence": "high", "contradiction": null },
  { "id": "C8", "question_id": "Q2", "claim": "ExecuteUpdate and ExecuteDelete bypass automatic concurrency control because they do not interact with EF's change tracker and modify data immediately at the database.", "source_ids": ["S3"], "confidence": "high", "contradiction": null },
  { "id": "C9", "question_id": "Q2", "claim": "For ExecuteUpdate/ExecuteDelete, concurrency control must be implemented manually: include the token in the LINQ Where predicate and check the returned affected-row count (zero implies a likely concurrent change).", "source_ids": ["S3"], "confidence": "high", "contradiction": null },
  { "id": "C10", "question_id": "Q2", "claim": "EF Core's update-token design discussion states that when original values of concurrency-marked properties are not preserved between requests there is no guaranteed data consistency on concurrent updates, and the token is populated from original values rather than current values — implying detached flows that do not round-trip the token lose the automatic guarantee.", "source_ids": ["S13"], "confidence": "medium", "contradiction": null },
  { "id": "C11", "question_id": "Q3", "claim": "System.Text.Json web defaults (ASP.NET Core Web API and System.Net.Http.Json ReadFromJsonAsync) set PropertyNameCaseInsensitive = true, PropertyNamingPolicy = camelCase, NumberHandling = AllowReadingFromString; outside web defaults deserialization is case-sensitive. In .NET 9+ JsonSerializerOptions.Web exposes these defaults.", "source_ids": ["S5", "S6", "S8"], "confidence": "high", "contradiction": null },
  { "id": "C12", "question_id": "Q3", "claim": "By default, JSON properties present in a payload but absent from the target POCO are silently ignored; starting in .NET 8 strict mapping (JsonUnmappedMemberHandling/UnmappedMemberHandling) can be opted into and throws JsonException for unmapped properties.", "source_ids": ["S7"], "confidence": "high", "contradiction": null },
  { "id": "C13", "question_id": "Q3", "claim": "By default, JSON properties not represented in the class are ignored, and if a property on the type is required but absent from the payload, deserialization fails (plain non-required absent properties are left at their default).", "source_ids": ["S11"], "confidence": "high", "contradiction": null },
  { "id": "C14", "question_id": "Q3", "claim": "Combined client failure mode: with default options a renamed or restructured JSON field does not throw — it is ignored, so the target DTO member silently keeps its default (silent data loss); JsonException only occurs with strict unmapped-member handling enabled (opt-in, .NET 8+) or a missing required member.", "source_ids": ["S7", "S11"], "confidence": "high", "contradiction": null },
  { "id": "C15", "question_id": "Q3", "claim": "System.Net.Http.Json ReadFromJsonAsync uses JsonSerializerDefaults.Web when no options are supplied, so web casing/case-insensitivity defaults also govern client-side response deserialization.", "source_ids": ["S8", "S6"], "confidence": "high", "contradiction": null }
]
```

## Uncertainty

```json
[
  { "id": "U1", "question_id": "Q2", "note": "No crisp official-documentation verdict for applying DbContext.Update/Attach to a fully detached entity graph regarding how the concurrency token's original value is populated. The 'detached updates bypass it' premise is UNVALIDATED by official docs; only a vendor design discussion (S13) supports the consistency concern, at medium confidence." },
  { "id": "U2", "question_id": "Q3", "note": "The direction 'POCO property missing from JSON -> left at CLR default' is inferred from S11's statement about required members failing; the docs do not add an explicit sentence for non-required missing members." }
]
```

## Freshness

All documentation sources were current as of the access date (2026-09-19) and updated within the last ~1.5 years: EF Core concurrency 2025-10-30; ExecuteUpdate/Delete 2026-06-24; disconnected entities 2025-10-30; character-casing 2025-05-02; configure-options 2026-03-30; missing-members 2025-01-31; ReadFromJsonAsync API 2026-05-27; Web API DTO page 2026-02-21; microservices app-layer page 2025-07-31; JSON deserialization 2026-03-30. API reference pages span monikers up to net-11.0.

## Gaps

- **Q2**: explicit official-documentation verdict for detached `DbContext.Update`/`Attach` concurrency-token semantics is missing; only the tracked-load+SetValues and ExecuteUpdate/ExecuteDelete paths are conclusively documented.

## Product choices (separate, non-authoritative)

```json
[
  { "topic": "token-type", "note": "Whether to standardize on PostgreSQL xmin as the concurrency token versus an application-managed token (e.g., GUID). Evidence shows both are supported; the choice is a design decision." },
  { "topic": "dto-granularity", "note": "Whether to introduce a dedicated per-endpoint read-model DTO for each cleaned boundary or to reuse an existing broader DTO. Documented guidance favors a purpose-specific minimal shape; adopting it here is a product choice." },
  { "topic": "strict-json", "note": "Whether to opt into strict unmapped-member handling (JsonUnmappedMemberHandling.Disallow) on client DTOs to convert silent field mismatches into JsonException. Tradeoff: stricter failures vs. resilience to additive server changes." }
]
```

## Status notes

- Revision 2 supersedes blocked revision 1 of the same `request_id`: the maintainer enabled the runtime evidence grants (`webfetch` for `documentation`; `websearch` + `webfetch` for `open-web`); both classes were admitted and exercised.
- No unvalidated claim is emitted. The single unresolved sub-path (detached-update token semantics) is recorded as uncertainty U1 only.
- No contradictions across sources were found.

---

## Revision 3 Evidence — scoped follow-up (detached-update concurrency-token semantics)

Request: `ebc-aud10-research-01` revision 3 · Question: **Q2b** (documentation) · Admitted classes: `documentation` (`webfetch`); `open-web` (`websearch` declared but non-functional — provider HTTP 403 on every call; open-web evidence collected via `webfetch` and the GitHub search API).

Outcome: `partial` — the official wording answers the original-value question (S1); no official page states the detached-`Update` WHERE-clause verdict crisply (that half remains inference-grade, C7/G1).

### Sources (revision 3)

```json
[
  { "id": "S1", "class": "documentation", "title": "Accessing Tracked Entities — EF Core", "publisher": "Microsoft (learn.microsoft.com)", "URL": "https://learn.microsoft.com/en-us/ef/core/change-tracking/entity-entries", "accessed_at": "2026-09-19", "excerpt": "The original value of a property is the value that the property had when the entity was queried from the database. However, original values are not available if the entity was disconnected and then explicitly attached to another DbContext, for example with Attach or Update. In this case, the original value returned will be the same as the current value." },
  { "id": "S2", "class": "documentation", "title": "Handling Concurrency Conflicts — EF Core", "publisher": "Microsoft (learn.microsoft.com)", "URL": "https://learn.microsoft.com/en-us/ef/core/saving/concurrency", "accessed_at": "2026-09-19", "excerpt": "The concurrency token is loaded and tracked when an entity is queried - just like any other property. Then, when an update or delete operation is performed during SaveChanges(), the value of the concurrency token on the database is compared against the original value read by EF Core. … UPDATE [People] SET [FirstName] = @p0 WHERE [PersonId] = @p1 AND [Version] = @p2; … \"Original values\" are the values that were originally retrieved from the database, before any edits were made. … Refresh the original values of the concurrency token to reflect the current values in the database." },
  { "id": "S3", "class": "documentation", "title": "Explicitly Tracking Entities — EF Core", "publisher": "Microsoft (learn.microsoft.com)", "URL": "https://learn.microsoft.com/en-us/ef/core/change-tracking/explicit-tracking", "accessed_at": "2026-09-19", "excerpt": "Update behaves exactly as the Attach methods described above, except that entities are put into the Modified instead of the Unchanged state. … Attach: Calling SaveChanges at this point will have no effect. … Update results in updates or inserts being sent to the database for every property of every tracked entity, even when some property values may not have been changed." },
  { "id": "S4", "class": "documentation", "title": "Change Tracking — EF Core (overview)", "publisher": "Microsoft (learn.microsoft.com)", "URL": "https://learn.microsoft.com/en-us/ef/core/change-tracking/", "accessed_at": "2026-09-19", "excerpt": "The original values of properties are preserved automatically and used for efficient updates." },
  { "id": "S5", "class": "documentation", "title": "Identity Resolution — EF Core", "publisher": "Microsoft (learn.microsoft.com)", "URL": "https://learn.microsoft.com/en-us/ef/core/change-tracking/identity-resolution", "accessed_at": "2026-09-19", "excerpt": "context.Attach(blog); context.Entry(blog).OriginalValues.SetValues(originalValues); … Applying the original values ensures that only property values that have actually changed are updated in the database. … Tip: it does require sending the entity's original values to and from the web client. Carefully consider whether this extra complexity is worth the benefits." },
  { "id": "S6", "class": "documentation", "title": "Tutorial: Handle concurrency — ASP.NET MVC with EF Core", "publisher": "Microsoft (learn.microsoft.com)", "URL": "https://learn.microsoft.com/en-us/aspnet/core/data/ef-mvc/concurrency", "accessed_at": "2026-09-19", "excerpt": "Before you call SaveChanges, you have to put that original RowVersion property value in the OriginalValues collection for the entity. … Then when the Entity Framework creates a SQL UPDATE command, that command will include a WHERE clause that looks for a row that has the original RowVersion value. If no rows are affected … throws a DbUpdateConcurrencyException. … (Detached delete path:) When the Entity Framework creates the SQL DELETE command, it includes a WHERE clause with the original RowVersion value." },
  { "id": "S7", "class": "documentation", "title": "DbContext.Update Method — API reference", "publisher": "Microsoft (learn.microsoft.com)", "URL": "https://learn.microsoft.com/en-us/dotnet/api/microsoft.entityframeworkcore.dbcontext.update", "accessed_at": "2026-09-19", "excerpt": "Begins tracking the given entity and entries reachable from the given entity using the Modified state by default … For entity types with generated keys if an entity has its primary key value set then it will be tracked in the Modified state." },
  { "id": "S8", "class": "documentation", "title": "Concurrency Tokens — Npgsql Documentation", "publisher": "Npgsql Development Team", "URL": "https://www.npgsql.org/efcore/modeling/concurrency.html", "accessed_at": "2026-09-19", "excerpt": "Entity Framework Core supports the concept of optimistic concurrency - a property on your entity is designated as a concurrency token, and EF Core detects concurrent modifications by checking whether that token has changed since the entity was read. … maps uint Version to the PostgreSQL xmin system column via [Timestamp] / IsRowVersion(); xmin … automatically gets updated every time the row is changed." },
  { "id": "S9", "class": "open-web", "title": "dotnet/efcore #4512 — Sql IsConcurrencyToken uses same value (comment by divega, EF team)", "publisher": "GitHub", "URL": "https://github.com/dotnet/efcore/issues/4512", "accessed_at": "2026-09-19", "excerpt": "the optimistic concurrency mechanism relies on EF remembering the original value of the Timestamp property, which is added to the WHERE clause of the UPDATE statement. Setting the current value of the property … won't have the same effects. … When the entity is attached to the second context the value carried in the Timestamp property becomes both the current and the original value. … sample: db.Update(person); db.SaveChanges(); → DbUpdateConcurrencyException. (Issue created 2016-02-07; closed 2022-10-16 not_planned.)" },
  { "id": "S10", "class": "open-web", "title": "dotnet/efcore #32720 — Shadow Concurrency Token of owned Type in TPH Hierarchy is not set automatically", "publisher": "GitHub", "URL": "https://github.com/dotnet/efcore/issues/32720", "accessed_at": "2026-09-19", "excerpt": "EF Core 8 + Npgsql: after dbContext.Update(update), InvalidOperationException: Instances of types 'ConcreteTypeOne' and 'OwnedType' are mapped to the same row with the key value '{Id: 1}', but have different original property values {ConcurrencyToken: 465450} and {_TableSharingConcurrencyTokenConvention_ConcurrencyToken: 0} for the column 'xmin'. (Closed 2024-01-25 not_planned.)" },
  { "id": "S11", "class": "documentation", "title": "Disconnected Entities — EF Core", "publisher": "Microsoft (learn.microsoft.com)", "URL": "https://learn.microsoft.com/en-us/ef/core/saving/disconnected-entities", "accessed_at": "2026-09-19", "excerpt": "Covers single-entity and graph Add/Attach/Update for disconnected scenarios, e.g. public static async Task Update(DbContext context, object entity) { context.Update(entity); await context.SaveChangesAsync(); }. The page contains no occurrence of \"concurrency token\"." },
  { "id": "S12", "class": "open-web", "title": "dotnet/efcore #2339 — Null in concurrency token property fails update (even though the tokens match)", "publisher": "GitHub", "URL": "https://github.com/dotnet/efcore/issues/2339", "accessed_at": "2026-09-19", "excerpt": "Because null is not equal to null on SQL Server, if you have a concurrency token set to null then the comparison in the database fails (even though they actually match). Generated SQL: WHERE [BlogId] = @p0 AND [Url] = @p2 with @p2=NULL. (Closed 2022-10-16 not_planned.)" }
]
```

### Claims (revision 3)

```json
[
  { "id": "C1", "question_id": "Q2b", "claim": "For an entity that was disconnected from a context and then explicitly attached via Attach or Update, EF Core has no database-read original value; the tracked original value of every property is populated from the instance's current value — it is not preserved from a prior load.", "source_ids": ["S1"], "confidence": "high", "contradiction": null },
  { "id": "C2", "question_id": "Q2b", "claim": "The concurrency-token comparison is generated from the token's tracked original value, which EF places in the UPDATE WHERE clause (e.g. ... AND [Version] = @p2).", "source_ids": ["S2", "S9"], "confidence": "high", "contradiction": null },
  { "id": "C3", "question_id": "Q2b", "claim": "Consequently, for a detached graph updated via Update (or Attach + modification), the token value used in the WHERE clause is the value carried on the detached instance (client-supplied), not a value read from the database.", "source_ids": ["S1", "S2", "S9"], "confidence": "medium", "contradiction": null },
  { "id": "C4", "question_id": "Q2b", "claim": "Automatic concurrency checking still runs for an entity attached while detached and then saved: the token is included in the WHERE clause and a zero-rows-affected result raises DbUpdateConcurrencyException. Official docs demonstrate this for the detached attach-then-Remove path; the Update path is stated by the EF team in S9.", "source_ids": ["S3", "S6", "S9"], "confidence": "medium", "contradiction": null },
  { "id": "C5", "question_id": "Q2b", "claim": "DbContext.Update on a graph puts existing entities in Modified and unset-key entities in Added; all property values are then sent, whether changed or not, and the docs state no exception for concurrency tokens.", "source_ids": ["S3", "S7"], "confidence": "high", "contradiction": null },
  { "id": "C6", "question_id": "Q2b", "claim": "Attach alone tracks the graph as Unchanged, so SaveChanges performs no UPDATE at all and therefore no concurrency check occurs until properties or entry.State are changed to Modified.", "source_ids": ["S3"], "confidence": "high", "contradiction": null },
  { "id": "C7", "question_id": "Q2b", "claim": "Official EF Core documentation contains no detached-Update-specific statement that the concurrency token is still checked and its original value populated from the instance; the closest official prose is the generic mechanism (S2), the tracked-instance WHERE-clause explanation (S6), and the detached-delete example (S6).", "source_ids": ["S2", "S5", "S6", "S7", "S11"], "confidence": "high", "contradiction": null },
  { "id": "C8", "question_id": "Q2b", "claim": "Documented patterns for detached updates with meaningful concurrency semantics: (a) fetch-then-modify — query the entity in the saving context, then apply changes (2 round-trips, only changed columns sent); (b) explicit token round-tripping — entry.Property(\"RowVersion\").OriginalValue = <posted token> before SaveChanges; (c) on conflict, refresh originals from database values and retry.", "source_ids": ["S2", "S5", "S6"], "confidence": "high", "contradiction": null },
  { "id": "C9", "question_id": "Q2b", "claim": "Npgsql/PostgreSQL uses the standard EF Core concurrency-token mechanism, including mapping a uint property to the xmin system column ([Timestamp] / IsRowVersion()), described as checking whether that token has changed since the entity was read.", "source_ids": ["S8"], "confidence": "high", "contradiction": null },
  { "id": "C10", "question_id": "Q2b", "claim": "The EF Core page dedicated to disconnected graphs and Update/Attach (S11) does not mention concurrency tokens at all — the detached-token semantics must be assembled from change-tracking and concurrency pages instead.", "source_ids": ["S11", "S1", "S2"], "confidence": "medium", "contradiction": null },
  { "id": "C11", "question_id": "Q2b", "claim": "A null concurrency-token value in the WHERE clause is provider-sensitive: on SQL Server the generated equality against a null parameter never matches, so the documented mitigation is provider null-semantics handling; a token-less detached instance therefore risks spurious conflicts on such providers.", "source_ids": ["S12", "S2"], "confidence": "medium", "contradiction": null }
]
```

### Contradictions (revision 3)

- **DC1 (documentation-level, not factual)**: S2 defines "Original values" as "the values that were originally retrieved from the database, before any edits were made," while S1 states that for a disconnected entity explicitly attached the original value equals the current value (i.e. it was not retrieved from the database by that context). The two statements describe different code paths, but S2's definition omits the detached exception — the exact boundary this change is about.

### Uncertainty (revision 3)

- **U1 (primary, carried from revision 2; now narrowed)**: No official EF Core page states, for Update/Attach on a fully detached graph, that the concurrency token is placed in the UPDATE WHERE clause using the instance's value. C3/C4 are inferred from S1 (official) + S2's general mechanism + S9 (EF team, 2016) + S10 (2024 Npgsql bug report).
- **U2**: S9 is a 2016, EF Core 1.0-era thread; its semantics match current docs (S1) but edge cases (store-generated vs application-managed tokens, shadow/computed tokens, owned types sharing a column — see S10) are not guaranteed unchanged.
- **U3**: websearch (a declared open-web tool) failed with a provider HTTP 403 on every attempt; open-web evidence came only from webfetch on targeted URLs and the GitHub search API, so open-web search coverage was limited.
- **U4**: The exact behavior when a detached instance carries a default/absent token (0/null) is not documented in the checked sources; only the null-comparison caveat (S12) is available.

### Freshness (revision 3)

EF Core conceptual pages: ms.date 2020-12-30 (change-tracking/*) and 2022-10-19 (saving/concurrency), site updated_at 2025-10-30, docs git commit 8d81a48 on the live branch; API reference monikers run through EF Core 10. ASP.NET Core concurrency tutorial: updated_at 2026-07-22, monikers through aspnetcore-11.0. `learn.microsoft.com/en-us/ef/core/modeling/concurrency` redirects to `.../saving/concurrency` (same canonical page). Npgsql docs: © 2026, no explicit page date. GitHub threads: #4512 (2016; closed 2022-10-16 not_planned), #32720 (2024-01; closed 2024-01-25 not_planned), #2339 (2015; closed 2022-10-16 not_planned). No source covers EF Core 11.

### Gaps (revision 3)

- **G1 (Q2b, second half)**: Official documentation does not state a crisp detached-Update verdict that automatic concurrency checking still applies and that the token's original value is populated from the instance; supported indirectly via the detached-delete tutorial (S6) and the EF-team comment (S9).
- **G2 (Q2b, edge case)**: No official statement on handling a detached instance whose token is default/absent; only the null-comparison caveat (S12).

### Product choices (revision 3, non-authoritative)

- **PC1** — Fetch-then-modify in the saving context (documented "Store Wins" pattern; 2 round-trips, only changed columns).
- **PC2** — Round-trip the token to the client and set Entry(...).Property(token).OriginalValue before SaveChanges (documented in S6; requires sending original values to/from the client — S5 caveat).
- **PC3** — Keep detached Update and require callers to carry a valid token on the instance (relies on instance-carried original semantics, C3).

### Risks (revision 3)

- A detached Update does not compare against a database-read original (C1): the token's authority comes entirely from the round-tripped instance value, so a token-less or default-valued instance can produce a spurious concurrency failure (or provider-dependent match) — see C3, C11, S12.
- Switching from Update to Attach silently disables concurrency checking (no UPDATE is emitted at all, C6) — a refactor hazard.
- Update marks every property modified and updates all columns (C5), changing the "only changed columns" behavior that fetch-then-modify provides.
- Npgsql/PostgreSQL xmin is a system column: detached graphs with owned types sharing the xmin column previously threw InvalidOperationException rather than a clean concurrency exception in EF Core 8 (S10, closed not_planned).
- Version drift: no source covers EF Core 11, and the strongest detached-specific statement (S9) is a 2016 EF Core 1.0-era thread.

---

## Closure record (orchestrator, 2026-09-19)

- Maintainer confirmed closure with accepted scope ("Cerrar y seguir").
- **S1 accepted as the authoritative answer** for attach semantics: for a disconnected entity explicitly attached (`Attach`/`Update`), the original value equals the current value; the WHERE-clause token consequently comes from the instance-carried value (C3/C4, medium confidence; EF-team statement S9 + 2024 Npgsql report S10).
- **Residual G1/G2 classified as a documentation limitation** — not an open question. The official sources are exhausted; no further documentation chase is possible.
- **Conditional note**: any future work unit that touches detached update paths MUST add the bounded runtime confirmation (integration test asserting the emitted UPDATE WHERE clause and `DbUpdateConcurrencyException` on Npgsql). This change does not touch those paths.
- **Research condition for `sdd-propose`**: CLOSED by accepted scope — no pending product decisions; evidence references: this file (revisions 2 and 3); store: OpenSpec ready.
