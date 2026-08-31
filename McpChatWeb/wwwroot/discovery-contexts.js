// Discovery Factory candidate contexts — minimal vanilla JS client for the phase 5 pilot slice.
// Talks to /api/discovery/runs and /api/discovery/runs/{runId}/contexts (McpChatWeb/Program.cs).
// Read-only browse: reviewers confirm candidates out-of-band, consistent with the non-authoritative
// LLM/review constraints already enforced by the finding-review lifecycle (discovery-review.js).

const runSelect = document.getElementById('run-select');
const contextsEl = document.getElementById('contexts');
const dependenciesEl = document.getElementById('dependencies');
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

function kindBadgeClass(kind) {
  return kind === 'SharedInfrastructure' ? 'badge-shared' : 'badge-business';
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
      statusEl.textContent = 'No runs found. Run `discovery build-graph` and `discovery seed-contexts` to create candidates.';
      contextsEl.innerHTML = '';
      dependenciesEl.innerHTML = '';
      return;
    }
    for (const run of runs) {
      const opt = document.createElement('option');
      opt.value = run.runId;
      opt.textContent = `${run.runId} — ${run.subject}`;
      runSelect.appendChild(opt);
    }
    statusEl.textContent = '';
    await loadContexts(runSelect.value);
  } catch (err) {
    statusEl.textContent = `Failed to load runs: ${err.message}`;
    statusEl.className = 'error';
  }
}

async function loadContexts(runId) {
  if (!runId) { contextsEl.innerHTML = ''; dependenciesEl.innerHTML = ''; return; }
  contextsEl.innerHTML = '<p class="empty">Loading candidate contexts…</p>';
  dependenciesEl.innerHTML = '';
  try {
    const res = await fetch(`/api/discovery/runs/${encodeURIComponent(runId)}/contexts`);
    if (!res.ok) throw new Error(`HTTP ${res.status}`);
    const body = await res.json();
    renderContexts(body.contexts || body.Contexts || []);
    renderDependencies(body.dependencies || body.Dependencies || []);
  } catch (err) {
    contextsEl.innerHTML = `<p class="error">Failed to load contexts: ${err.message}</p>`;
  }
}

function renderDependencies(deps) {
  if (!deps.length) { dependenciesEl.innerHTML = ''; return; }
  const rows = deps.map(d => `
    <tr>
      <td>${escapeHtml(d.from || d.From)}</td>
      <td>&rarr;</td>
      <td>${escapeHtml(d.to || d.To)}</td>
      <td>${(d.confidence ?? d.Confidence).toFixed(2)}</td>
    </tr>`).join('');
  dependenciesEl.innerHTML = `
    <div class="card deps">
      <h3>Cross-context dependencies</h3>
      <table>
        <thead><tr><th>From</th><th></th><th>To</th><th>Confidence</th></tr></thead>
        <tbody>${rows}</tbody>
      </table>
    </div>`;
}

function renderContexts(items) {
  if (!items.length) {
    contextsEl.innerHTML = '<p class="empty">No candidate contexts for this run yet. Run `discovery seed-contexts --run-id ...`.</p>';
    return;
  }

  contextsEl.innerHTML = '';
  for (const item of items) {
    const card = document.createElement('div');
    card.className = 'card';

    const status = item.status || item.Status;
    const kind = item.kind || item.Kind;
    const name = item.name || item.Name;
    const confidence = item.confidence ?? item.Confidence;
    const seedingRule = item.seedingRule || item.SeedingRule;
    const memberCount = item.memberCount ?? item.MemberCount;
    const ownerCount = item.ownerCount ?? item.OwnerCount;
    const members = item.members || item.Members || [];
    const evidence = item.evidence || item.Evidence || [];

    const membersHtml = members.length
      ? `<div class="members">${members.map(m => `${escapeHtml(m.nodeId || m.NodeId)} <em>(${m.role || m.Role})</em>`).join('<br>')}</div>`
      : '<p class="empty">No members recorded.</p>';

    const evidenceHtml = evidence.map(e => `
      <div class="evidence">
        <div><strong>${e.evidenceId || e.EvidenceId}</strong> (${e.type || e.Type})
          <span class="badge ${(e.wasRedacted ?? e.WasRedacted) ? 'badge-redacted' : 'badge-clean'}">
            ${(e.wasRedacted ?? e.WasRedacted) ? 'Redacted' : 'No redaction needed'}
          </span>
        </div>
        <div>${escapeHtml(e.redactedExcerpt || e.RedactedExcerpt || '(no excerpt)')}</div>
      </div>`).join('');

    card.innerHTML = `
      <h3>${escapeHtml(name)}
        <span class="badge ${kindBadgeClass(kind)}">${kind}</span>
        <span class="badge ${statusBadgeClass(status)}">${status}</span>
      </h3>
      <div><strong>Confidence:</strong> ${confidence?.toFixed ? confidence.toFixed(2) : confidence}</div>
      <div><strong>Seeding heuristics:</strong> ${escapeHtml(seedingRule)}</div>
      <div><strong>Members:</strong> ${memberCount} (${ownerCount} owner-role)</div>
      ${membersHtml}
      <div><strong>Evidence / citations:</strong></div>
      ${evidenceHtml || '<p class="empty">No evidence attached.</p>'}
    `;

    contextsEl.appendChild(card);
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

runSelect.addEventListener('change', () => loadContexts(runSelect.value));
refreshBtn.addEventListener('click', loadRuns);

loadRuns();
