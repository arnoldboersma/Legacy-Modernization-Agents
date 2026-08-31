-- Discovery Factory phase 6 (issue #6): integration inventory and topology links. Append-only,
-- following the same convention as 0001_initial.sql, 0002_role_assignments.sql, and
-- 0003_context_graph.sql — never edited after release.

CREATE TABLE IF NOT EXISTS integrations (
    integration_id TEXT PRIMARY KEY,
    run_id TEXT NOT NULL,
    category TEXT NOT NULL,
    classification TEXT NOT NULL,
    direction TEXT NOT NULL,
    trigger_or_caller TEXT NOT NULL,
    protocol_or_mechanism TEXT NOT NULL,
    logical_target TEXT NOT NULL,
    configuration_key_semantics TEXT,
    redacted_contract_shape TEXT,
    authentication_semantics TEXT,
    reliability_behavior TEXT,
    owning_context_candidate_id TEXT,
    evidence_ids_json TEXT NOT NULL,
    confidence REAL NOT NULL,
    classification_rule TEXT NOT NULL,
    blind_spots_json TEXT NOT NULL,
    requires_review INTEGER NOT NULL,
    provenance_id TEXT NOT NULL,
    created_at_utc TEXT NOT NULL,
    FOREIGN KEY (run_id) REFERENCES discovery_runs(run_id),
    FOREIGN KEY (owning_context_candidate_id) REFERENCES context_candidates(context_candidate_id),
    FOREIGN KEY (provenance_id) REFERENCES provenance(provenance_id)
);

CREATE TABLE IF NOT EXISTS integration_links (
    integration_link_id TEXT PRIMARY KEY,
    run_id TEXT NOT NULL,
    integration_id TEXT NOT NULL,
    linked_record_kind TEXT NOT NULL,
    linked_record_id TEXT NOT NULL,
    created_at_utc TEXT NOT NULL,
    FOREIGN KEY (run_id) REFERENCES discovery_runs(run_id),
    FOREIGN KEY (integration_id) REFERENCES integrations(integration_id)
);

CREATE INDEX IF NOT EXISTS idx_integrations_run ON integrations(run_id);
CREATE INDEX IF NOT EXISTS idx_integrations_context ON integrations(owning_context_candidate_id);
CREATE INDEX IF NOT EXISTS idx_integration_links_run ON integration_links(run_id);
CREATE INDEX IF NOT EXISTS idx_integration_links_integration ON integration_links(integration_id);
