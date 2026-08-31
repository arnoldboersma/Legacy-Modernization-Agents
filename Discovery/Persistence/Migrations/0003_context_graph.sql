-- Discovery Factory phase 5 (issue #3): deterministic dependency graph and candidate logical
-- contexts/module boundaries. Append-only, following the same convention as 0001_initial.sql and
-- 0002_role_assignments.sql — never edited after release.

CREATE TABLE IF NOT EXISTS graph_nodes (
    node_id TEXT PRIMARY KEY,
    run_id TEXT NOT NULL,
    kind TEXT NOT NULL,
    symbol_locator TEXT NOT NULL,
    display_name TEXT NOT NULL,
    artifact_id TEXT,
    evidence_ids_json TEXT NOT NULL,
    created_at_utc TEXT NOT NULL,
    FOREIGN KEY (run_id) REFERENCES discovery_runs(run_id),
    FOREIGN KEY (artifact_id) REFERENCES source_artifacts(artifact_id)
);

CREATE TABLE IF NOT EXISTS graph_edges (
    edge_id TEXT PRIMARY KEY,
    run_id TEXT NOT NULL,
    from_node_id TEXT NOT NULL,
    to_node_id TEXT NOT NULL,
    kind TEXT NOT NULL,
    evidence_ids_json TEXT NOT NULL,
    confidence REAL NOT NULL,
    created_at_utc TEXT NOT NULL,
    FOREIGN KEY (run_id) REFERENCES discovery_runs(run_id),
    FOREIGN KEY (from_node_id) REFERENCES graph_nodes(node_id),
    FOREIGN KEY (to_node_id) REFERENCES graph_nodes(node_id)
);

CREATE TABLE IF NOT EXISTS context_candidates (
    context_candidate_id TEXT PRIMARY KEY,
    run_id TEXT NOT NULL,
    name TEXT NOT NULL,
    kind TEXT NOT NULL,
    status TEXT NOT NULL,
    confidence REAL NOT NULL,
    evidence_ids_json TEXT NOT NULL,
    seeding_rule TEXT NOT NULL,
    provenance_id TEXT NOT NULL,
    supersedes_context_candidate_id TEXT,
    created_at_utc TEXT NOT NULL,
    FOREIGN KEY (run_id) REFERENCES discovery_runs(run_id),
    FOREIGN KEY (provenance_id) REFERENCES provenance(provenance_id),
    FOREIGN KEY (supersedes_context_candidate_id) REFERENCES context_candidates(context_candidate_id)
);

CREATE TABLE IF NOT EXISTS context_memberships (
    context_membership_id TEXT PRIMARY KEY,
    run_id TEXT NOT NULL,
    context_candidate_id TEXT NOT NULL,
    node_id TEXT NOT NULL,
    role TEXT NOT NULL,
    evidence_ids_json TEXT NOT NULL,
    created_at_utc TEXT NOT NULL,
    FOREIGN KEY (run_id) REFERENCES discovery_runs(run_id),
    FOREIGN KEY (context_candidate_id) REFERENCES context_candidates(context_candidate_id),
    FOREIGN KEY (node_id) REFERENCES graph_nodes(node_id)
);

CREATE TABLE IF NOT EXISTS context_dependency_edges (
    context_dependency_edge_id TEXT PRIMARY KEY,
    run_id TEXT NOT NULL,
    from_context_candidate_id TEXT NOT NULL,
    to_context_candidate_id TEXT NOT NULL,
    evidence_ids_json TEXT NOT NULL,
    confidence REAL NOT NULL,
    created_at_utc TEXT NOT NULL,
    FOREIGN KEY (run_id) REFERENCES discovery_runs(run_id),
    FOREIGN KEY (from_context_candidate_id) REFERENCES context_candidates(context_candidate_id),
    FOREIGN KEY (to_context_candidate_id) REFERENCES context_candidates(context_candidate_id)
);

CREATE INDEX IF NOT EXISTS idx_graph_nodes_run ON graph_nodes(run_id);
CREATE INDEX IF NOT EXISTS idx_graph_edges_run ON graph_edges(run_id);
CREATE INDEX IF NOT EXISTS idx_graph_edges_from ON graph_edges(from_node_id);
CREATE INDEX IF NOT EXISTS idx_graph_edges_to ON graph_edges(to_node_id);
CREATE INDEX IF NOT EXISTS idx_context_candidates_run ON context_candidates(run_id);
CREATE INDEX IF NOT EXISTS idx_context_memberships_run ON context_memberships(run_id);
CREATE INDEX IF NOT EXISTS idx_context_memberships_context ON context_memberships(context_candidate_id);
CREATE INDEX IF NOT EXISTS idx_context_dependency_edges_run ON context_dependency_edges(run_id);
