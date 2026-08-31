// Discovery Factory Specification Factory handoff — minimal vanilla JS client for the phase 7
// pilot slice. Talks to /api/discovery/runs and /api/discovery/runs/{runId}/handoff
// (McpChatWeb/Program.cs), which mirrors the same JSON produced by `discovery export-handoff`.
// Read-only browse of all ten design-doc §6 sections; sections 3/4 are rendered as an explicit
// gap when no use-case/business-rule findings exist, never fabricated.

const runSelect = document.getElementById('run-select');
const handoffEl = document.getElementById('handoff');
const statusEl = document.getElementById('status-msg');
const refreshBtn = document.getElementById('refresh-btn');

function severityBadgeClass(severity) {
  switch (severity) {
    case 'High': return 'badge-high';
    case 'Medium': return 'badge-medium';
    default: return 'badge-low';
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
      statusEl.textContent = 'No runs found.';
      handoffEl.innerHTML = '';
      return;
    }
    for (const run of runs) {
      const opt = document.createElement('option');
      opt.value = run.runId;
      opt.textContent = `${run.runId} — ${run.subject}`;
      runSelect.appendChild(opt);
    }
    statusEl.textContent = '';
    await loadHandoff(runSelect.value);
  } catch (err) {
    statusEl.textContent = `Failed to load runs: ${err.message}`;
    statusEl.className = 'error';
  }
}

async function loadHandoff(runId) {
  if (!runId) { handoffEl.innerHTML = ''; return; }
  handoffEl.innerHTML = '<p class="empty">Loading handoff…</p>';
  try {
    const res = await fetch(`/api/discovery/runs/${encodeURIComponent(runId)}/handoff`);
    if (!res.ok) throw new Error(`HTTP ${res.status}`);
    const h = await res.json();
    renderHandoff(h);
  } catch (err) {
    handoffEl.innerHTML = `<p class="error">Failed to load handoff: ${err.message}</p>`;
  }
}

function field(name, value) {
  if (value === null || value === undefined || value === '') return '';
  return `<div><strong>${name}:</strong> ${escapeHtml(String(value))}</div>`;
}

function get(obj, camel, pascal) {
  return obj[camel] ?? obj[pascal];
}

function renderHandoff(h) {
  const s1 = get(h, 'section1_RunScope', 'Section1_RunScope');
  const s2 = get(h, 'section2_DomainLandscape', 'Section2_DomainLandscape');
  const s3 = get(h, 'section3_UseCases', 'Section3_UseCases');
  const s4 = get(h, 'section4_BusinessRules', 'Section4_BusinessRules');
  const s5 = get(h, 'section5_Architecture', 'Section5_Architecture');
  const s6 = get(h, 'section6_Integrations', 'Section6_Integrations');
  const s7 = get(h, 'section7_DataModel', 'Section7_DataModel');
  const s8 = get(h, 'section8_NonFunctional', 'Section8_NonFunctional');
  const s9 = get(h, 'section9_Risks', 'Section9_Risks');
  const s10 = get(h, 'section10_Navigation', 'Section10_Navigation');

  handoffEl.innerHTML = `
    <div class="card">
      <h3>1. Run scope and reconstruction status</h3>
      ${field('Subject', s1.subject ?? s1.Subject)}
      ${field('Source locator', s1.sourceLocator ?? s1.SourceLocator)}
      ${field('Source revision', s1.sourceRevision ?? s1.SourceRevision)}
      ${field('Evidence boundary', s1.evidenceBoundary ?? s1.EvidenceBoundary)}
      ${field('Run status', s1.runStatus ?? s1.RunStatus)}
    </div>

    <details class="group" open>
      <summary class="group-heading">2. System purpose and domain landscape <span class="count">${(s2.contexts ?? s2.Contexts ?? []).length}</span></summary>
      <div class="group-body">
        ${(s2.contexts ?? s2.Contexts ?? []).map(c => `<div>• <strong>${escapeHtml(c.name ?? c.Name)}</strong> (${c.kind ?? c.Kind}, ${c.status ?? c.Status}, confidence ${(c.confidence ?? c.Confidence).toFixed(2)})</div>`).join('') || '<p class="empty">No candidate contexts.</p>'}
      </div>
    </details>

    <div class="card">
      <h3>3. Use cases and functional flows</h3>
      ${(s3.isSparse ?? s3.IsSparse) ? `<p><span class="badge badge-gap">Gap</span> ${escapeHtml(s3.gapExplanation ?? s3.GapExplanation)}</p>` : `<p>${(s3.useCaseFindingIds ?? s3.UseCaseFindingIds ?? []).length} finding(s).</p>`}
    </div>

    <div class="card">
      <h3>4. Business rules, policies, calculations, and state transitions</h3>
      ${(s4.isSparse ?? s4.IsSparse) ? `<p><span class="badge badge-gap">Gap</span> ${escapeHtml(s4.gapExplanation ?? s4.GapExplanation)}</p>` : `<p>${(s4.businessRuleFindingIds ?? s4.BusinessRuleFindingIds ?? []).length} finding(s).</p>`}
    </div>

    <div class="card">
      <h3>5. Architecture and component responsibility map</h3>
      ${field('Nodes', (s5.nodes ?? s5.Nodes ?? []).length)}
      ${field('Edges', s5.edgeCount ?? s5.EdgeCount)}
    </div>

    <details class="group">
      <summary class="group-heading">6. Interfaces and integration topology <span class="count">${(s6.integrations ?? s6.Integrations ?? []).length}</span></summary>
      <div class="group-body">
        ${(s6.integrations ?? s6.Integrations ?? []).map(i => `<div>• <strong>${escapeHtml(i.category ?? i.Category)}</strong> — ${escapeHtml(i.triggerOrCaller ?? i.TriggerOrCaller)} &rarr; ${escapeHtml(i.logicalTarget ?? i.LogicalTarget)}</div>`).join('') || '<p class="empty">No integrations.</p>'}
      </div>
    </details>

    <div class="card">
      <h3>7. Data model and lifecycle</h3>
      ${field('Entities', (s7.entities ?? s7.Entities ?? []).length)}
      ${field('Data-store integrations', (s7.dataStoreIntegrations ?? s7.DataStoreIntegrations ?? []).length)}
    </div>

    <div class="card">
      <h3>8. Security, configuration, operations, and non-functional constraints</h3>
      ${field('Platform/identity', (s8.platformIdentityIntegrations ?? s8.PlatformIdentityIntegrations ?? []).length)}
      ${field('Observability', (s8.observabilityIntegrations ?? s8.ObservabilityIntegrations ?? []).length)}
      ${field('Delivery', (s8.deliveryIntegrations ?? s8.DeliveryIntegrations ?? []).length)}
    </div>

    <details class="group" open>
      <summary class="group-heading">9. Risks, gaps, assumptions, and decisions needed <span class="count">${(s9.risks ?? s9.Risks ?? []).length}</span></summary>
      <div class="group-body">
        ${(s9.risks ?? s9.Risks ?? []).map(r => `
          <div class="card">
            <strong>${escapeHtml(r.riskId ?? r.RiskId)}</strong> — ${escapeHtml(r.title ?? r.Title)}
            <span class="badge ${severityBadgeClass(r.severity ?? r.Severity)}">${r.severity ?? r.Severity}</span>
            <div class="muted">${r.category ?? r.Category} · ${r.status ?? r.Status}</div>
            <div>${escapeHtml(r.description ?? r.Description)}</div>
            <div><strong>Escalation question:</strong> ${escapeHtml(r.escalationQuestion ?? r.EscalationQuestion)}</div>
          </div>`).join('') || '<p class="empty">No open risks.</p>'}
      </div>
    </details>

    <div class="card">
      <h3>10. Evidence, provenance, and navigation</h3>
      ${field('Artifacts', s10.totalArtifacts ?? s10.TotalArtifacts)}
      ${field('Evidence records', s10.totalEvidence ?? s10.TotalEvidence)}
      ${field('Findings', s10.totalFindings ?? s10.TotalFindings)}
      ${field('Candidate contexts', s10.totalContextCandidates ?? s10.TotalContextCandidates)}
      ${field('Integrations', s10.totalIntegrations ?? s10.TotalIntegrations)}
      ${field('Open risks', s10.totalRisks ?? s10.TotalRisks)}
      <div class="muted">Read-only MCP/API query navigation reference (design doc §8 — not implemented):</div>
      <ul>${(s10.mcpQueryOperationsReference ?? s10.McpQueryOperationsReference ?? []).map(op => `<li>${escapeHtml(op)}</li>`).join('')}</ul>
    </div>
  `;
}

function escapeHtml(text) {
  if (text == null) return '';
  return String(text)
    .replace(/&/g, '&amp;')
    .replace(/</g, '&lt;')
    .replace(/>/g, '&gt;')
    .replace(/"/g, '&quot;');
}

runSelect.addEventListener('change', () => loadHandoff(runSelect.value));
refreshBtn.addEventListener('click', loadRuns);

loadRuns();
