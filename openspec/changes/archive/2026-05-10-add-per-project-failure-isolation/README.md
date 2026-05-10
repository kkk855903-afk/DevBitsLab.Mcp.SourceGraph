# add-per-project-failure-isolation

Make scope cold-indexing tolerate per-project compilation failures and per-document Pass 1 throws so a single bad project no longer marks the entire scope `degraded` — the scope reports `partial` and surfaces the failed projects/files via `list_scopes`.
