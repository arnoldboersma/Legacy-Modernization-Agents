// Discovery Factory review queue — minimal vanilla JS client for the pilot slice.
// Talks to /api/discovery/* endpoints added in McpChatWeb/Program.cs.

const runSelect = document.getElementById('run-select');
const queueEl = document.getElementById('queue');
const statusEl = document.getElementById('status-msg');
const refreshBtn = document.getElementById('refresh-btn');

function statusBadgeClass(status) {
  switch (status) {
    case 'Candidate': return 'badge-candidate';
    case 'HumanReview': return 'badge-review';
    case 'Published': return 'badge-published';
    case 'Rejected': return 'badge-rejected';
    case 'NeedsEvidence': return 'badge-needs-evidence';
    default: return 'badge-candidate';
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
      statusEl.textContent = 'No runs found. Run `discovery seed-demo` to create one.';
      queueEl.innerHTML = '';
      return;
    }
    for (const run of runs) {
      const opt = document.createElement('option');
      opt.value = run.runId;
      opt.textContent = `${run.runId} — ${run.subject}`;
      runSelect.appendChild(opt);
    }
    statusEl.textContent = '';
    await loadQueue(runSelect.value);
  } catch (err) {
    statusEl.textContent = `Failed to load runs: ${err.message}`;
    statusEl.className = 'error';
  }
}

async function loadQueue(runId) {
  if (!runId) { queueEl.innerHTML = ''; return; }
  queueEl.innerHTML = '<p class="empty">Loading review queue…</p>';
  try {
    const res = await fetch(`/api/discovery/runs/${encodeURIComponent(runId)}/queue`);
    if (!res.ok) throw new Error(`HTTP ${res.status}`);
    const items = await res.json();
    renderQueue(items);
  } catch (err) {
    queueEl.innerHTML = `<p class="error">Failed to load queue: ${err.message}</p>`;
  }
}

function renderQueue(items) {
  if (!items.length) {
    queueEl.innerHTML = '<p class="empty">No findings awaiting review for this run.</p>';
    return;
  }

  queueEl.innerHTML = '';
  for (const item of items) {
    const card = document.createElement('div');
    card.className = 'card';

    const evidenceHtml = (item.evidence || item.Evidence || []).map(e => `
      <div class="evidence">
        <div><strong>${e.evidenceId || e.EvidenceId}</strong> (${e.type || e.Type})
          <span class="badge ${(e.wasRedacted ?? e.WasRedacted) ? 'badge-redacted' : 'badge-clean'}">
            ${(e.wasRedacted ?? e.WasRedacted) ? 'Redacted' : 'No redaction needed'}
          </span>
        </div>
        <div>${escapeHtml(e.redactedExcerpt || e.RedactedExcerpt || '(no excerpt)')}</div>
        ${(e.redactionSummary || e.RedactionSummary) ? `<div><em>${escapeHtml(e.redactionSummary || e.RedactionSummary)}</em></div>` : ''}
      </div>`).join('');

    const llm = item.llmAssessment || item.LlmAssessment;
    const llmHtml = llm ? `
      <div class="why">
        <div><strong>Non-authoritative LLM review priority:</strong> ${llm.reviewPriority ?? llm.ReviewPriority}</div>
        <div><strong>Why:</strong> ${escapeHtml(llm.whyExplanation || llm.WhyExplanation)}</div>
      </div>` : '<p class="empty">No LLM assessment attached.</p>';

    const status = item.status || item.Status;
    const findingRevisionId = item.findingRevisionId || item.FindingRevisionId;

    card.innerHTML = `
      <h3>${findingRevisionId} <span class="badge ${statusBadgeClass(status)}">${status}</span></h3>
      <div><strong>Statement:</strong> ${escapeHtml(item.statement || item.Statement)}</div>
      <div><strong>Confidence:</strong> ${item.confidence ?? item.Confidence}</div>
      ${llmHtml}
      <div><strong>Evidence / citations:</strong></div>
      ${evidenceHtml || '<p class="empty">No evidence attached.</p>'}
      <div class="actions">
        <input type="text" placeholder="Rationale (required)" data-role="rationale" />
        <button class="btn-publish" data-action="publish">✅ Publish</button>
        <button class="btn-reject" data-action="reject">⛔ Reject</button>
        <button class="btn-evidence" data-action="request-evidence">📎 Request evidence</button>
      </div>
      <div class="action-status" style="margin-top:6px;"></div>
    `;

    card.querySelectorAll('button[data-action]').forEach(btn => {
      btn.addEventListener('click', () => submitDecision(card, findingRevisionId, btn.dataset.action));
    });

    queueEl.appendChild(card);
  }
}

async function submitDecision(card, findingRevisionId, action) {
  const rationaleInput = card.querySelector('input[data-role="rationale"]');
  const rationale = (rationaleInput.value || '').trim();
  const actionStatus = card.querySelector('.action-status');
  if (!rationale) {
    actionStatus.innerHTML = '<span class="error">Rationale is required before recording a decision.</span>';
    return;
  }

  actionStatus.textContent = 'Submitting…';
  try {
    const res = await fetch(`/api/discovery/findings/${encodeURIComponent(findingRevisionId)}/${action}`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ rationale })
    });
    const body = await res.json().catch(() => ({}));
    if (!res.ok) {
      actionStatus.innerHTML = `<span class="error">${escapeHtml(body.error || `HTTP ${res.status}`)}</span>`;
      return;
    }
    actionStatus.textContent = `Recorded: ${body.decision || action}`;
    await loadQueue(runSelect.value);
  } catch (err) {
    actionStatus.innerHTML = `<span class="error">${escapeHtml(err.message)}</span>`;
  }
}

function escapeHtml(text) {
  if (text == null) return '';
  return String(text)
    .replace(/&/g, '&amp;')
    .replace(/</g, '&lt;')
    .replace(/>/g, '&gt;')
    .replace(/"/g, '&quot;');
}

runSelect.addEventListener('change', () => loadQueue(runSelect.value));
refreshBtn.addEventListener('click', loadRuns);

loadRuns();
