## Codebase Memory MCP

**MANDATORY: use Codebase Memory MCP graph tools FIRST — before reading files or making code changes.**

This rule applies to every request involving this codebase.

All tools are exposed as `mcp__cbm__<name>` in this harness (CBM server 0.12.1, 17 tools).

### Workflow

1. `mcp__cbm__list_projects` — pick the project whose `root_path` contains your working directory. Pass its `name` (e.g. `RMM-API`) as `project`, never a repo or folder name.
2. `mcp__cbm__get_architecture(project)` — understand the codebase structure.
3. `mcp__cbm__search_graph` to find relevant symbols; `mcp__cbm__trace_path` for call chains.
4. `mcp__cbm__get_code_snippet` to read specific function implementations.
5. Only use the plain `read` tool when you need exact raw content to edit a specific line.
6. Before claiming something is absent, unused, or exhaustively covered, call `mcp__cbm__check_index_coverage` once with every evidence path. A clean result means no recorded gap, not proof of completeness.

### Indexing

- `index_repository(repo_path, mode, async)` — index a repository. For a large repo pass `async: true`, then poll the same call with `status: true` until state is `succeeded`, `failed` or `cancelled`.
- `index_status(project)` — readiness, counts, root, watch state, coverage gaps.
- `list_projects` — all indexed projects with node/edge counts.
- `delete_project(project)` — remove a project and all its graph data.

### Querying

- `search_graph(query | name_pattern | qn_pattern | label | file_pattern, ...)` — structured/BM25 search by label, name, qualified name, file. Supports `min_degree`/`max_degree` + `relationship` for fan-in/out and dead-code queries.
- `search_code(pattern, project, mode)` — text search with graph ranking; `mode: files` lists matching paths.
- `trace_path(function_name, direction, depth, mode)` — call-chain traversal (`calls`), argument-aware `data_flow`, `cross_service`, `topology`, `deploy`, `run`. Use `risk_labels: true` for risk-classified hops.
- `query_graph(query, project)` — read-only openCypher subset for multi-hop, aggregation and complexity questions.
- `get_graph_schema(project)` — node/edge label counts; `diagnostics: full` adds queryable properties.
- `get_code_snippet(qualified_name, project)` — read source for one symbol. Qualified names are `<project>.<path_parts>.<name>`; discover them with `search_graph` first.
- `get_file_outline(project, file_path)` — declaration outline of one repository-relative file, in source order.
- `get_architecture(project, aspects)` — overview: languages, packages, entry points, routes, hotspots, boundaries, layers, cycles, `system_structure`.
- `detect_changes(project, scope, depth)` — map a git diff to changed files, symbols and impact; `scope: tests` finds related tests.
- `check_index_coverage(project, paths, scopes)` — confirm exact paths/scopes are indexed and fresh. Best-effort; clean is not proof of completeness.
- `compare_graphs(base_project, target_project)` — deterministic additions/removals between two indexed snapshots.
- `manage_adr(project, mode)` — read/update the project's architecture decision record.
- `ingest_traces(traces, project, label)` — record observed runtime calls so the graph distinguishes observed from merely possible paths.

### Gotchas

- `search_graph(relationship="HTTP_CALLS")` filters nodes by degree — use `query_graph` with Cypher to see actual edges.
- `query_graph` has a 100k row ceiling — add a Cypher `LIMIT` for broad queries or paginate with `search_graph`.
- `trace_path` needs exact names — run `search_graph(name_pattern=".*PartialName.*")` first.
- `direction="outbound"` misses cross-service callers — use `direction="both"`.
- `search_graph` returns 50 rows per page by default — check `has_more` and use `offset`.
