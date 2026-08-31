-- Discovery Factory phase 4 (issue #8): deterministic artifact-role classification.
-- Append-only, following the same convention as 0001_initial.sql — never edited after release.

CREATE TABLE IF NOT EXISTS role_assignments (
    role_assignment_id TEXT PRIMARY KEY,
    run_id TEXT NOT NULL,
    artifact_id TEXT NOT NULL,
    symbol_locator TEXT NOT NULL,
    roles_json TEXT NOT NULL,
    confidence REAL NOT NULL,
    evidence_ids_json TEXT NOT NULL,
    classification_rule TEXT NOT NULL,
    requires_review INTEGER NOT NULL,
    provenance_id TEXT NOT NULL,
    created_at_utc TEXT NOT NULL,
    FOREIGN KEY (run_id) REFERENCES discovery_runs(run_id),
    FOREIGN KEY (artifact_id) REFERENCES source_artifacts(artifact_id),
    FOREIGN KEY (provenance_id) REFERENCES provenance(provenance_id)
);

CREATE INDEX IF NOT EXISTS idx_role_assignments_run ON role_assignments(run_id);
CREATE INDEX IF NOT EXISTS idx_role_assignments_artifact ON role_assignments(artifact_id);
