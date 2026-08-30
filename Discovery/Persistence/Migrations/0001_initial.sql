-- Discovery Factory pilot slice: initial governed-record schema (append-only).
-- Neutral, language-agnostic persistence per docs/discovery-factory-technical-design.md.

CREATE TABLE IF NOT EXISTS schema_migrations (
    version INTEGER PRIMARY KEY,
    applied_at TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS discovery_runs (
    run_id TEXT PRIMARY KEY,
    subject TEXT NOT NULL,
    source_locator TEXT NOT NULL,
    source_revision TEXT NOT NULL,
    inclusions_json TEXT NOT NULL,
    exclusions_json TEXT NOT NULL,
    evidence_boundary TEXT NOT NULL,
    intent TEXT,
    status TEXT NOT NULL,
    supersedes_run_id TEXT,
    created_at_utc TEXT NOT NULL,
    FOREIGN KEY (supersedes_run_id) REFERENCES discovery_runs(run_id)
);

CREATE TABLE IF NOT EXISTS source_artifacts (
    artifact_id TEXT PRIMARY KEY,
    run_id TEXT NOT NULL,
    path TEXT NOT NULL,
    language TEXT NOT NULL,
    content_hash TEXT NOT NULL,
    size_bytes INTEGER,
    created_at_utc TEXT NOT NULL,
    FOREIGN KEY (run_id) REFERENCES discovery_runs(run_id)
);

CREATE TABLE IF NOT EXISTS evidence (
    evidence_id TEXT PRIMARY KEY,
    run_id TEXT NOT NULL,
    evidence_type TEXT NOT NULL,
    artifact_id TEXT,
    locator TEXT NOT NULL,
    redacted_excerpt TEXT,
    was_redacted INTEGER NOT NULL,
    redaction_summary TEXT,
    created_at_utc TEXT NOT NULL,
    FOREIGN KEY (run_id) REFERENCES discovery_runs(run_id),
    FOREIGN KEY (artifact_id) REFERENCES source_artifacts(artifact_id)
);

CREATE TABLE IF NOT EXISTS provenance (
    provenance_id TEXT PRIMARY KEY,
    run_id TEXT NOT NULL,
    producer_kind TEXT NOT NULL,
    producer_version TEXT NOT NULL,
    prompt_version TEXT,
    input_record_ids_json TEXT NOT NULL,
    created_at_utc TEXT NOT NULL,
    FOREIGN KEY (run_id) REFERENCES discovery_runs(run_id)
);

CREATE TABLE IF NOT EXISTS findings (
    finding_id TEXT PRIMARY KEY,
    run_id TEXT NOT NULL,
    correlation_key TEXT,
    created_at_utc TEXT NOT NULL,
    FOREIGN KEY (run_id) REFERENCES discovery_runs(run_id)
);

-- Append-only revision chain. No UPDATE/DELETE is ever performed on this table by the
-- repository; corrections insert a new row with an incremented revision_number.
CREATE TABLE IF NOT EXISTS finding_revisions (
    finding_revision_id TEXT PRIMARY KEY,
    finding_id TEXT NOT NULL,
    run_id TEXT NOT NULL,
    revision_number INTEGER NOT NULL,
    statement TEXT NOT NULL,
    evidence_ids_json TEXT NOT NULL,
    confidence REAL NOT NULL,
    status TEXT NOT NULL,
    provenance_id TEXT NOT NULL,
    supersedes_revision_id TEXT,
    created_at_utc TEXT NOT NULL,
    FOREIGN KEY (finding_id) REFERENCES findings(finding_id),
    FOREIGN KEY (run_id) REFERENCES discovery_runs(run_id),
    FOREIGN KEY (provenance_id) REFERENCES provenance(provenance_id),
    FOREIGN KEY (supersedes_revision_id) REFERENCES finding_revisions(finding_revision_id),
    UNIQUE (finding_id, revision_number)
);

CREATE TABLE IF NOT EXISTS llm_assessments (
    llm_assessment_id TEXT PRIMARY KEY,
    run_id TEXT NOT NULL,
    finding_revision_id TEXT NOT NULL,
    review_priority REAL NOT NULL,
    why_explanation TEXT NOT NULL,
    cited_evidence_ids_json TEXT NOT NULL,
    conflicts_or_unknowns TEXT,
    model_id TEXT NOT NULL,
    provenance_id TEXT NOT NULL,
    created_at_utc TEXT NOT NULL,
    FOREIGN KEY (run_id) REFERENCES discovery_runs(run_id),
    FOREIGN KEY (finding_revision_id) REFERENCES finding_revisions(finding_revision_id),
    FOREIGN KEY (provenance_id) REFERENCES provenance(provenance_id)
);

-- Append-only reviewer decisions. Never edited or deleted.
CREATE TABLE IF NOT EXISTS review_decisions (
    review_decision_id TEXT PRIMARY KEY,
    run_id TEXT NOT NULL,
    finding_revision_id TEXT NOT NULL,
    reviewer_identity TEXT NOT NULL,
    decision TEXT NOT NULL,
    rationale TEXT NOT NULL,
    decided_at_utc TEXT NOT NULL,
    FOREIGN KEY (run_id) REFERENCES discovery_runs(run_id),
    FOREIGN KEY (finding_revision_id) REFERENCES finding_revisions(finding_revision_id)
);

CREATE INDEX IF NOT EXISTS idx_source_artifacts_run ON source_artifacts(run_id);
CREATE INDEX IF NOT EXISTS idx_evidence_run ON evidence(run_id);
CREATE INDEX IF NOT EXISTS idx_findings_run ON findings(run_id);
CREATE INDEX IF NOT EXISTS idx_finding_revisions_finding ON finding_revisions(finding_id);
CREATE INDEX IF NOT EXISTS idx_finding_revisions_run ON finding_revisions(run_id);
CREATE INDEX IF NOT EXISTS idx_review_decisions_finding_revision ON review_decisions(finding_revision_id);
