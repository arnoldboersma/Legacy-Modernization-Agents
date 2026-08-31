-- Discovery Factory phase 7 (issue #5): risk register. Append-only, following the same
-- convention as 0001_initial.sql through 0004_integrations.sql — never edited after release.

CREATE TABLE IF NOT EXISTS risk_register_entries (
    risk_id TEXT PRIMARY KEY,
    run_id TEXT NOT NULL,
    title TEXT NOT NULL,
    category TEXT NOT NULL,
    severity TEXT NOT NULL,
    status TEXT NOT NULL,
    description TEXT NOT NULL,
    escalation_question TEXT NOT NULL,
    derivation_rule TEXT NOT NULL,
    source_record_id TEXT NOT NULL,
    evidence_ids_json TEXT NOT NULL,
    provenance_id TEXT NOT NULL,
    supersedes_risk_id TEXT,
    created_at_utc TEXT NOT NULL,
    FOREIGN KEY (run_id) REFERENCES discovery_runs(run_id),
    FOREIGN KEY (provenance_id) REFERENCES provenance(provenance_id),
    FOREIGN KEY (supersedes_risk_id) REFERENCES risk_register_entries(risk_id)
);

CREATE INDEX IF NOT EXISTS idx_risk_register_entries_run ON risk_register_entries(run_id);
CREATE INDEX IF NOT EXISTS idx_risk_register_entries_derivation ON risk_register_entries(run_id, derivation_rule, source_record_id);
