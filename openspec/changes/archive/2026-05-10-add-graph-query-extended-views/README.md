# add-graph-query-extended-views

Extend the `query_graph` view layer (shipped by `add-graph-query`) with `v_annotations`, `v_diagnostics`, and `v_history` so agents can compose ad-hoc queries across attribute / decorator metadata, Roslyn diagnostics, and per-symbol git history — closing the gap where this data was indexed but only reachable via curated tools.
