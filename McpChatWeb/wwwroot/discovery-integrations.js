// Discovery Factory integration inventory — minimal vanilla JS client for the phase 6 pilot slice.
// Talks to /api/discovery/runs and /api/discovery/runs/{runId}/integrations (McpChatWeb/Program.cs).
// Read-only browse, grouped by classification (RuntimeApplication/PlatformIdentity/Observability/
// Delivery) so business-behavior integrations are never conflated with platform/observability/
// delivery concerns, consistent with the review-queue/contexts page pattern.

const runSelect = document.getElementById('run-select');
const integrationsEl = document.getElementById('integrations');
const countsEl = document.getElementById('counts');
const statusEl = document.getElementById('status-msg');
const refreshBtn = document.getElementById('refresh-btn');

const CLASSIFICATION_ORDER = ['RuntimeApplication', 'PlatformIdentity', 'Observability', 'Delivery'];

function classificationBadgeClass(classification) {
  switch (classification) {
    case 'RuntimeApplication': return 'badge-runtime';
    case 'PlatformIdentity': return 'badge-platform';
    case 'Observability': return 'badge-observability';
    case 'Delivery': return 'badge-delivery';
    default: return 'badge-runtime';
  }
}

async function loadRuns() {
  statusEl.textContent = 'Loading runs…';
  try {
    const res = await fetch('/api/discovery/runs');
    if (!res.ok) throw new Error(`HTTP ${res.status}`);
    const runs = await res.json();
    runSelect.innerHTML = '';
    if (!runs.length) {
      runSelect.innerHTML = '<option value="">No discovery runs yet</option>';
      statusEl.textContent = 'No runs found. Run `discovery classify-integrations` to create an inventory.';
      integrationsEl.innerHTML = '';
      countsEl.innerHTML = '';
      return;
    }
    for (const run of runs) {
      const opt = document.createElement('option');
      opt.value = run.runId;
      opt.textContent = `${run.runId} — ${run.subject}`;
      runSelect.appendChild(opt);
    }
    statusEl.textContent = '';
    await loadIntegrations(runSelect.value);
  } catch (err) {
    statusEl.textContent = `Failed to load runs: ${err.message}`;
    statusEl.className = 'error';
  }
}

async function loadIntegrations(runId) {
  if (!runId) { integrationsEl.innerHTML = ''; countsEl.innerHTML = ''; return; }
  integrationsEl.innerHTML = '<p class="empty">Loading integration inventory…</p>';
  countsEl.innerHTML = '';
  try {
    const res = await fetch(`/api/discovery/runs/${encodeURIComponent(runId)}/integrations`);
    if (!res.ok) throw new Error(`HTTP ${res.status}`);
    const body = await res.json();
    renderCounts(body.countsByClassification || body.CountsByClassification || {});
    renderIntegrations(body.integrations || body.Integrations || []);
  } catch (err) {
    integrationsEl.innerHTML = `<p class="error">Failed to load integration inventory: ${err.message}</p>`;
  }
}

function renderCounts(counts) {
  const entries = Object.entries(counts);
  if (!entries.length) { countsEl.innerHTML = ''; return; }
  countsEl.innerHTML = entries
    .sort((a, b) => CLASSIFICATION_ORDER.indexOf(a[0]) - CLASSIFICATION_ORDER.indexOf(b[0]))
    .map(([classification, count]) => `<span>${escapeHtml(classification)}: <strong>${count}</strong></span>`)
    .join('');
}

function renderIntegrations(items) {
  if (!items.length) {
    integrationsEl.innerHTML = '<p class="empty">No integrations recorded for this run yet. Run `discovery classify-integrations --source-dir ...`.</p>';
    return;
  }

  const byClassification = new Map();
  for (const item of items) {
    const classification = item.classification || item.Classification;
    if (!byClassification.has(classification)) byClassification.set(classification, []);
    byClassification.get(classification).push(item);
  }

  integrationsEl.innerHTML = '';
  const orderedKeys = [...byClassification.keys()].sort(
    (a, b) => CLASSIFICATION_ORDER.indexOf(a) - CLASSIFICATION_ORDER.indexOf(b));

  for (const classification of orderedKeys) {
    const group = byClassification.get(classification);
    const details = document.createElement('details');
    details.className = 'group';

    const summary = document.createElement('summary');
    summary.className = 'group-heading';
    const reviewCount = group.filter(g => (g.requiresReview ?? g.RequiresReview)).length;
    summary.innerHTML = `${escapeHtml(classification)} <span class="count">${group.length}</span>` +
      (reviewCount ? ` <span class="badge badge-review">${reviewCount} need review</span>` : '');
    details.appendChild(summary);

    const body = document.createElement('div');
    body.className = 'group-body';
    for (const item of group) {
      body.appendChild(renderIntegrationCard(item));
    }
    details.appendChild(body);

    integrationsEl.appendChild(details);
  }
}

function renderIntegrationCard(item) {
  const category = item.category || item.Category;
  const classification = item.classification || item.Classification;
  const direction = item.direction || item.Direction;
  const target = item.logicalTarget || item.LogicalTarget;
  const trigger = item.triggerOrCaller || item.TriggerOrCaller;
  const protocolOrMechanism = item.protocolOrMechanism || item.ProtocolOrMechanism;
  const configKeys = item.configurationKeySemantics || item.ConfigurationKeySemantics;
  const contractShape = item.redactedContractShape || item.RedactedContractShape;
  const authSemantics = item.authenticationSemantics || item.AuthenticationSemantics;
  const reliability = item.reliabilityBehavior || item.ReliabilityBehavior;
  const owningContext = item.owningContextCandidateId || item.OwningContextCandidateId;
  const confidence = item.confidence ?? item.Confidence;
  const rule = item.classificationRule || item.ClassificationRule;
  const blindSpots = item.blindSpots || item.BlindSpots || [];
  const requiresReview = item.requiresReview ?? item.RequiresReview;
  const evidence = item.evidence || item.Evidence || [];

  const evidenceHtml = evidence.map(e => `
    <div class="evidence">
      <div><strong>${e.evidenceId || e.EvidenceId}</strong> (${e.type || e.Type})
        <span class="badge ${(e.wasRedacted ?? e.WasRedacted) ? 'badge-redacted' : 'badge-clean'}">
          ${(e.wasRedacted ?? e.WasRedacted) ? 'Redacted' : 'No redaction needed'}
        </span>
      </div>
      <div>${escapeHtml(e.redactedExcerpt || e.RedactedExcerpt || '(no excerpt)')}</div>
    </div>`).join('');

  const blindSpotsHtml = blindSpots.length
    ? `<div class="blindspots"><strong>Blind spots:</strong><ul>${blindSpots.map(b => `<li>${escapeHtml(b)}</li>`).join('')}</ul></div>`
    : '';

  const card = document.createElement('details');
  card.className = 'card card-item';

  const summary = document.createElement('summary');
  summary.innerHTML = `
    <strong>${escapeHtml(category)} &rarr; ${escapeHtml(target)}</strong>
    <span class="badge ${classificationBadgeClass(classification)}">${classification}</span>
    <span class="badge ${requiresReview ? 'badge-review' : 'badge-ok'}">${requiresReview ? 'Requires review' : 'No review flagged'}</span>
    <span class="muted"> · ${direction} · confidence ${confidence?.toFixed ? confidence.toFixed(2) : confidence}</span>
  `;
  card.appendChild(summary);

  const detailBody = document.createElement('div');
  detailBody.className = 'card-detail';
  detailBody.innerHTML = `
    <div><strong>Direction:</strong> ${direction} &nbsp; <strong>Trigger/caller:</strong> ${escapeHtml(trigger)}</div>
    <div><strong>Protocol/mechanism:</strong> ${escapeHtml(protocolOrMechanism)}</div>
    ${configKeys ? `<div><strong>Configuration key semantics:</strong> ${escapeHtml(configKeys)}</div>` : ''}
    ${contractShape ? `<div><strong>Redacted contract shape:</strong> ${escapeHtml(contractShape)}</div>` : ''}
    ${authSemantics ? `<div><strong>Authentication semantics:</strong> ${escapeHtml(authSemantics)}</div>` : ''}
    ${reliability ? `<div><strong>Reliability behavior:</strong> ${escapeHtml(reliability)}</div>` : ''}
    <div><strong>Owning context:</strong> ${owningContext ? escapeHtml(owningContext) : '<span class="empty">unlinked (best-effort resolution found no match)</span>'}</div>
    <div><strong>Confidence:</strong> ${confidence?.toFixed ? confidence.toFixed(2) : confidence} &nbsp; <strong>Rule:</strong> ${escapeHtml(rule)}</div>
    ${blindSpotsHtml}
    <div><strong>Evidence / citations:</strong></div>
    ${evidenceHtml || '<p class="empty">No evidence attached.</p>'}
  `;
  card.appendChild(detailBody);

  return card;
}

function escapeHtml(text) {
  if (text == null) return '';
  return String(text)
    .replace(/&/g, '&amp;')
    .replace(/</g, '&lt;')
    .replace(/>/g, '&gt;')
    .replace(/"/g, '&quot;');
}

runSelect.addEventListener('change', () => loadIntegrations(runSelect.value));
refreshBtn.addEventListener('click', loadRuns);

loadRuns();
