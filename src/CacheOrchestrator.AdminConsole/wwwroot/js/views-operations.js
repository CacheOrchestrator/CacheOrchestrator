/**
 * Operations page — invalidate / version / settings fan-out.
 */

import { api } from "./api.js";
import { $, main } from "./dom.js";
import { esc } from "./format.js";
import { setBreadcrumb } from "./router.js";
import * as shell from "./shell.js";
import { shortError } from "./views-shared.js";

/** Normalize legacy action=ttl → settings. */
function normalizeAction(raw) {
  const a = String(raw || "invalidate").trim().toLowerCase();
  if (a === "ttl" || a === "patch-ttl" || a === "settings") return "settings";
  return a;
}

/**
 * @param {Array<{ id: string, displayName?: string, group?: string, kind?: string, runtimeOverlay?: boolean, enumValues?: string[] }>} catalog
 */
function overlayCatalog(catalog) {
  return (catalog || []).filter((s) => s.runtimeOverlay);
}

export async function renderOperations(params) {
  setBreadcrumb([]);
  const domainParam = params.get("domain") || "";
  const action = normalizeAction(params.get("action") || "invalidate");

  const [distribution, domainsFan, catalogDto] = await Promise.all([
    api("/api/distribution").catch(() => null),
    api("/api/domains").catch(() => ({ data: [] })),
    api("/api/domain-settings/catalog").catch(() => ({ settings: [] })),
  ]);

  const domains = (domainsFan?.data || [])
    .map((d) => d.name)
    .filter(Boolean)
    .sort((a, b) => a.localeCompare(b));
  if (domainParam && !domains.includes(domainParam)) domains.unshift(domainParam);
  const selectedDomain = domainParam || domains[0] || "";

  const catalog = overlayCatalog(catalogDto?.settings || []);

  const mode = distribution?.recommendedMode || "fan-out";
  const busAvailable = !!distribution?.busAvailable;
  const modeClass = mode === "bus-distribute" ? "mode-bus" : "mode-fanout";
  const modeLabel = mode === "bus-distribute" ? "Cluster bus" : "Direct to each instance";
  const modeDetail = distribution?.summary
    || "How this Console will deliver the operation.";

  const probeInstances = distribution?.instances || [];
  const unreachableIds = probeInstances.filter((p) => !p.succeeded).map((p) => p.id);

  const probeRows = probeInstances.map((p) => {
    const bus = p.busEnabled
      ? `<span class="badge ok">bus</span>`
      : `<span class="badge muted">no bus</span>`;
    const mem = p.membership ? esc(p.membership) : "—";
    const peers = p.peerCount != null ? p.peerCount : "—";
    const probeLabel = p.succeeded ? "Reachable" : "Down";
    const probeClass = p.succeeded ? "status-Healthy" : "status-Down";
    return `<tr>
      <td>${esc(p.id)}</td>
      <td class="${probeClass}">${probeLabel}</td>
      <td>${bus}</td>
      <td>${mem}</td>
      <td>${peers}</td>
      <td class="muted" title="${p.error ? esc(p.error) : ""}">${p.error ? esc(shortError(p.error)) : ""}</td>
    </tr>`;
  }).join("");

  main().innerHTML = `
    <div class="card">
      <h2>Cluster bus probe</h2>
        <div id="distBanner" class="dist-banner ${modeClass}">
            <div class="dist-banner-title">
                <span class="badge ${mode === "bus-distribute" ? "ok" : "warn"}">${esc(modeLabel)}</span>
                ${busAvailable && distribution?.preferredBusOriginId
                ? `<span class="muted">preferred origin: <code>${esc(distribution.preferredBusOriginId)}</code></span>`
                : ""}
            </div>
            <p class="muted dist-banner-detail">${esc(modeDetail)}</p>
            <p class="muted small">
                <strong>Direct</strong> — this Console calls every configured instance.
                <strong>Cluster bus</strong> — one origin receives the command; peers apply it via the bus.
            </p>
        </div>
      <div class="table-wrap">
        <table class="data">
          <thead>
            <tr><th>Instance</th><th>Probe</th><th>Bus</th><th>Membership</th><th>Peers</th><th>Error</th></tr>
          </thead>
          <tbody>
            ${probeRows || `<tr><td colspan="6" class="muted">No instances configured.</td></tr>`}
          </tbody>
        </table>
      </div>
    </div>
    <div class="card">
      <h2>Operations</h2>
      <form id="opForm" class="op-form">
        <div class="op-field">
          <label for="opAction">Action</label>
          <select id="opAction" name="action">
            <option value="invalidate" ${action === "invalidate" ? "selected" : ""}>Invalidate domain</option>
            <option value="entity" ${action === "entity" ? "selected" : ""}>Invalidate entity</option>
            <option value="version" ${action === "version" ? "selected" : ""}>Bump version</option>
            <option value="settings" ${action === "settings" ? "selected" : ""}>Patch settings</option>
          </select>
        </div>
        <div class="op-field">
          <label for="opDomain">Domain</label>
          <select id="opDomain" name="domain" required ${domains.length ? "" : "disabled"}>
            ${domains.length
              ? domains.map((d) =>
                  `<option value="${esc(d)}" ${d === selectedDomain ? "selected" : ""}>${esc(d)}</option>`).join("")
              : `<option value="">No domains available</option>`}
          </select>
        </div>
        <div id="opActionPanel" class="op-panel"></div>
        <div class="op-actions">
          <button type="submit">Run</button>
        </div>
      </form>
      <div id="opWriteAlert" class="op-write-alert" hidden></div>
      <div id="opModeUsed" class="dist-result-meta muted">No operation yet.</div>
      <pre id="opResult" class="result">No operation yet.</pre>
    </div>`;

  const actionEl = $("#opAction");
  const panel = $("#opActionPanel");
  /** @type {SettingRowsController|null} */
  let settingsCtrl = null;

  function mountActionPanel() {
    const a = normalizeAction(actionEl.value);
    settingsCtrl = null;
    if (a === "entity") {
      panel.innerHTML = `
        <div class="op-field">
          <label for="opEntityKind">Entity kind</label>
          <input id="opEntityKind" type="text" placeholder="products" autocomplete="off" />
        </div>
        <div class="op-field">
          <label for="opEntity">Entity id</label>
          <input id="opEntity" type="text" placeholder="resource id" autocomplete="off" />
        </div>`;
      return;
    }
    if (a === "version") {
      panel.innerHTML = `
        <div class="op-field">
          <label for="opVersion">Version (optional)</label>
          <input id="opVersion" type="text" placeholder="auto if empty" autocomplete="off" />
        </div>`;
      return;
    }
    if (a === "settings") {
      panel.innerHTML = `
        <p class="muted small op-panel-hint">Choose one or more runtime settings, then Run. Overlay keys only (bool / enum / numbers / comma-separated string lists for vary allowlists).</p>
        <div id="opSettingRows" class="op-setting-rows"></div>
        <label class="op-check">
          <input id="opApplyImmediately" type="checkbox" />
          Apply supported policy reductions immediately (invalidate affected entries)
        </label>`;
      settingsCtrl = new SettingRowsController($("#opSettingRows"), catalog);
      settingsCtrl.render();
      return;
    }
    panel.innerHTML = `<p class="muted small op-panel-hint">Invalidates the selected domain across the cluster.</p>`;
  }

  actionEl.addEventListener("change", mountActionPanel);
  mountActionPanel();

  function renderModeUsed(result) {
    const meta = $("#opModeUsed");
    if (!result) {
      meta.textContent = "No operation yet.";
      return;
    }
    const m = result.distributionMode || "fan-out";
    const badge = m === "bus-distribute"
      ? `<span class="badge ok">cluster bus</span>`
      : `<span class="badge warn">direct</span>`;
    const origin = result.busOriginInstanceId
      ? ` · origin <code>${esc(result.busOriginInstanceId)}</code>`
      : "";
    const dist = result.distribute ? "distribute:true" : "distribute:false";
    const outcome = result.outcome
      ? ` · <span class="badge ${result.outcome === "success" ? "ok" : "bad"}">${esc(result.outcome)}</span>`
      : "";
    meta.innerHTML = `${badge} · ${dist}${origin}${outcome}<br/><span class="muted">${esc(result.distributionSummary || "")}</span>`;
  }

  function renderWriteAlert(result) {
    const el = $("#opWriteAlert");
    if (!el) return;
    if (!result || result.outcome === "success" || result.allSucceeded) {
      el.hidden = true;
      el.innerHTML = "";
      return;
    }
    const failed = result.failedInstanceIds?.length
      ? result.failedInstanceIds
      : (result.results || []).filter((r) => !r.succeeded).map((r) => r.instanceId);
    const rows = (result.results || [])
      .filter((r) => !r.succeeded)
      .map((r) => `<tr><td><code>${esc(r.instanceId)}</code></td><td>${esc(r.error || "failed")}</td></tr>`)
      .join("");
    el.hidden = false;
    el.innerHTML = `
      <div class="banner critical op-write-incomplete">
        <strong>Cluster write incomplete</strong>
        <p>${esc(result.warning || "One or more instances did not apply the change. Cache settings may be inconsistent across the cluster.")}</p>
        ${failed.length ? `<p class="small">Failed: ${failed.map((id) => `<code>${esc(id)}</code>`).join(", ")}</p>` : ""}
        ${rows ? `<div class="table-wrap"><table class="data"><thead><tr><th>Instance</th><th>Error</th></tr></thead><tbody>${rows}</tbody></table></div>` : ""}
      </div>`;
  }

  /**
   * Confirm when some probe instances are down. Returns true to proceed.
   * @param {string[]} downIds
   * @returns {Promise<boolean>}
   */
  function confirmRunWithUnreachable(downIds) {
    return new Promise((resolve) => {
      const existing = document.getElementById("opConfirmBackdrop");
      if (existing) existing.remove();

      const list = downIds.map((id) => `<li><code>${esc(id)}</code></li>`).join("");
      const backdrop = document.createElement("div");
      backdrop.className = "chart-modal-backdrop";
      backdrop.id = "opConfirmBackdrop";
      backdrop.innerHTML = `
        <div class="chart-modal op-confirm-modal" role="dialog" aria-modal="true" aria-labelledby="opConfirmTitle">
          <div class="chart-modal-head">
            <h2 id="opConfirmTitle">Not all instances are reachable</h2>
            <div class="chart-modal-actions">
              <button type="button" class="secondary chart-modal-icon-btn" data-op-confirm-cancel aria-label="Cancel" title="Cancel">✕</button>
            </div>
          </div>
          <p>This command will <strong>not</strong> be delivered to every configured instance.</p>
          <p class="muted small">Unreachable (${downIds.length}):</p>
          <ul class="op-confirm-list">${list}</ul>
          <p class="banner warn op-confirm-warn">
            Settings or version overlays can become <strong>inconsistent</strong> across the cluster.
            Continue only if you know what you are doing (for example intentional maintenance).
          </p>
          <p class="muted small op-confirm-note">
            The result will be an <strong>Error</strong> (incomplete cluster write).
            Check the result details for which peers succeeded and which did not.
          </p>
          <div class="op-confirm-actions">
            <button type="button" class="secondary" data-op-confirm-cancel>Cancel</button>
            <button type="button" class="danger" data-op-confirm-run>Run anyway</button>
          </div>
        </div>`;
      document.body.appendChild(backdrop);
      document.body.classList.add("chart-modal-open");

      const close = (proceed) => {
        if (backdrop._onKey) document.removeEventListener("keydown", backdrop._onKey);
        backdrop.remove();
        document.body.classList.remove("chart-modal-open");
        resolve(proceed);
      };
      backdrop.querySelectorAll("[data-op-confirm-cancel]").forEach((btn) => {
        btn.addEventListener("click", () => close(false));
      });
      backdrop.querySelector("[data-op-confirm-run]")?.addEventListener("click", () => close(true));
      backdrop.addEventListener("click", (ev) => {
        if (ev.target === backdrop) close(false);
      });
      const onKey = (ev) => {
        if (ev.key === "Escape") close(false);
      };
      backdrop._onKey = onKey;
      document.addEventListener("keydown", onKey);
    });
  }

  $("#opForm").addEventListener("submit", async (ev) => {
    ev.preventDefault();
    const a = normalizeAction(actionEl.value);
    const dom = $("#opDomain").value.trim();
    const out = $("#opResult");
    const alertEl = $("#opWriteAlert");
    if (alertEl) {
      alertEl.hidden = true;
      alertEl.innerHTML = "";
    }

    if (!dom) {
      $("#opModeUsed").textContent = "Error";
      out.textContent = "Error: Domain is required.";
      return;
    }

    if (unreachableIds.length) {
      const ok = await confirmRunWithUnreachable(unreachableIds);
      if (!ok) {
        $("#opModeUsed").textContent = "Cancelled";
        out.textContent = "Cancelled — operation not sent.";
        return;
      }
    }

    out.textContent = "Running…";
    $("#opModeUsed").textContent = "Running…";

    try {
      let result;
      if (a === "invalidate") {
        result = await api("/api/invalidate", {
          method: "POST",
          body: JSON.stringify({ scope: "domain", domain: dom }),
        });
      } else if (a === "entity") {
        result = await api("/api/invalidate", {
          method: "POST",
          body: JSON.stringify({
            scope: "entity",
            domain: dom,
            entityKind: $("#opEntityKind").value.trim(),
            entityId: $("#opEntity").value.trim(),
          }),
        });
      } else if (a === "version") {
        const version = $("#opVersion").value.trim();
        result = await api(`/api/domains/${encodeURIComponent(dom)}/version`, {
          method: "POST",
          body: JSON.stringify({ version: version || null }),
        });
      } else {
        if (!settingsCtrl) throw new Error("Settings panel is not ready.");
        const built = settingsCtrl.buildSettings();
        if (built.error) {
          $("#opModeUsed").textContent = "Error";
          out.textContent = "Error: " + built.error;
          return;
        }
        result = await api(`/api/domains/${encodeURIComponent(dom)}/settings`, {
          method: "PATCH",
          body: JSON.stringify({
            settings: built.settings,
            applyImmediately: $("#opApplyImmediately")?.checked === true,
          }),
        });
      }
      renderModeUsed(result);
      renderWriteAlert(result);
      out.textContent = formatResultJson(result);
      shell.refreshHeader();
    } catch (err) {
      const body = err?.body;
      if (err?.status === 409 && body && Array.isArray(body.results)) {
        renderModeUsed(body);
        renderWriteAlert(body);
        out.textContent = formatResultJson(body);
        shell.refreshHeader();
        return;
      }
      $("#opModeUsed").textContent = "Error";
      if (body && typeof body === "object") {
        out.textContent = formatResultJson(body);
      } else {
        out.textContent = "Error: " + formatResultJson(err?.message || String(err));
      }
    }
  });
}

/** Pretty-print JSON for the Operations result panel. */
function formatResultJson(value) {
  if (value == null) return "";
  if (typeof value === "string") {
    const trimmed = value.trim();
    if ((trimmed.startsWith("{") && trimmed.endsWith("}"))
      || (trimmed.startsWith("[") && trimmed.endsWith("]"))) {
      try {
        return JSON.stringify(JSON.parse(trimmed), null, 2);
      } catch {
        return value;
      }
    }
    return value;
  }
  try {
    return JSON.stringify(value, null, 2);
  } catch {
    return String(value);
  }
}

/**
 * Multi-row setting editor driven by domain-settings catalog (overlay entries).
 */
class SettingRowsController {
  /**
   * @param {HTMLElement} host
   * @param {Array<{ id: string, displayName?: string, group?: string, kind?: string, enumValues?: string[] }>} catalog
   */
  constructor(host, catalog) {
    this.host = host;
    /** @type {Array<{ key: string, value: string }>} */
    this.rows = [{ key: "", value: "" }];
    this.catalog = catalog || [];
  }

  render() {
    if (!this.catalog.length) {
      this.host.innerHTML = `<p class="muted">No overlay settings in catalog (is Admin API reachable?).</p>`;
      return;
    }
    const used = new Set(this.rows.map((r) => r.key).filter(Boolean));
    this.host.innerHTML = this.rows.map((row, idx) => {
      const isLast = idx === this.rows.length - 1;
      const options = this.catalog
        .filter((s) => s.id === row.key || !used.has(s.id))
        .map((s) => {
          const label = s.group ? `${s.group}: ${s.displayName || s.id}` : (s.displayName || s.id);
          return `<option value="${esc(s.id)}" ${s.id === row.key ? "selected" : ""}>${esc(label)}</option>`;
        })
        .join("");
      const entry = this.catalog.find((s) => s.id === row.key);
      const valueControl = row.key
        ? this.valueControlHtml(entry, row.value, idx)
        : "";
      const removeBtn = !isLast || row.key
        ? `<button type="button" class="secondary op-setting-remove" data-idx="${idx}" title="Remove row" aria-label="Remove">×</button>`
        : `<span class="op-setting-remove-spacer"></span>`;
      return `
        <div class="op-setting-row" data-idx="${idx}">
          <select class="op-setting-key" data-idx="${idx}" aria-label="Setting">
            <option value="">Select setting…</option>
            ${options}
          </select>
          <div class="op-setting-value-wrap">${valueControl}</div>
          ${removeBtn}
        </div>`;
    }).join("");

    this.host.querySelectorAll(".op-setting-key").forEach((sel) => {
      sel.addEventListener("change", (ev) => {
        const i = Number(ev.target.dataset.idx);
        const key = ev.target.value;
        this.rows[i] = { key, value: "" };
        const last = this.rows[this.rows.length - 1];
        if (last.key) this.rows.push({ key: "", value: "" });
        this.render();
      });
    });
    this.host.querySelectorAll(".op-setting-value").forEach((inp) => {
      inp.addEventListener("input", (ev) => {
        const i = Number(ev.target.dataset.idx);
        this.rows[i].value = ev.target.type === "checkbox"
          ? (ev.target.checked ? "true" : "false")
          : ev.target.value;
      });
      inp.addEventListener("change", (ev) => {
        const i = Number(ev.target.dataset.idx);
        if (ev.target.type === "checkbox") {
          this.rows[i].value = ev.target.checked ? "true" : "false";
        }
      });
    });
    this.host.querySelectorAll(".op-setting-remove").forEach((btn) => {
      btn.addEventListener("click", () => {
        const i = Number(btn.dataset.idx);
        this.rows.splice(i, 1);
        if (!this.rows.length || this.rows[this.rows.length - 1].key) {
          this.rows.push({ key: "", value: "" });
        }
        this.render();
      });
    });
  }

  /**
   * @param {{ kind?: string, enumValues?: string[] }|undefined} entry
   * @param {string} value
   * @param {number} idx
   */
  valueControlHtml(entry, value, idx) {
    const kind = normalizeKind(entry?.kind);
    if (kind === "bool") {
      const checked = value === "true" || value === "1";
      return `<label class="op-setting-bool"><input type="checkbox" class="op-setting-value" data-idx="${idx}" ${checked ? "checked" : ""} /> enabled</label>`;
    }
    if (kind === "enum" && entry?.enumValues?.length) {
      const opts = entry.enumValues.map((v) =>
        `<option value="${esc(v)}" ${v === value ? "selected" : ""}>${esc(v)}</option>`).join("");
      return `<select class="op-setting-value" data-idx="${idx}" aria-label="Value"><option value="">Select…</option>${opts}</select>`;
    }
    if (kind === "datetimeoffset") {
      // datetime-local wants local wall time; store ISO on submit
      return `<input type="datetime-local" class="op-setting-value" data-idx="${idx}" value="${esc(value)}" aria-label="Value" />`;
    }
    if (kind === "double") {
      return `<input type="number" step="any" class="op-setting-value" data-idx="${idx}" value="${esc(value)}" aria-label="Value" />`;
    }
    if (kind === "stringarray") {
      return `<input type="text" class="op-setting-value" data-idx="${idx}" value="${esc(value)}" placeholder="comma-separated, e.g. a, b" aria-label="Value" />`;
    }
    if (kind === "string") {
      return `<input type="text" class="op-setting-value" data-idx="${idx}" value="${esc(value)}" aria-label="Value" />`;
    }
    // Int / IntSeconds default
    return `<input type="number" min="0" step="1" class="op-setting-value" data-idx="${idx}" value="${esc(value)}" aria-label="Value" />`;
  }

  /** @returns {{ settings?: Record<string, unknown>, error?: string }} */
  buildSettings() {
    /** @type {Record<string, unknown>} */
    const settings = {};
    for (const row of this.rows) {
      if (!row.key) continue;
      const entry = this.catalog.find((s) => s.id === row.key);
      const raw = String(row.value ?? "").trim();
      const kind = normalizeKind(entry?.kind);
      // bool unchecked → false; stringarray empty → [] (explicit empty list)
      if (raw === "" && kind !== "bool" && kind !== "stringarray") {
        return { error: `Value required for '${entry?.displayName || row.key}'.` };
      }
      try {
        settings[row.key] = coerceValue(entry, raw);
      } catch (e) {
        return { error: e.message || String(e) };
      }
    }
    if (!Object.keys(settings).length) {
      return { error: "Add at least one setting." };
    }
    return { settings };
  }
}

/** @param {string|number|undefined|null} kind */
function normalizeKind(kind) {
  if (typeof kind === "number") {
    const map = [
      "intseconds", "bool", "string", "datetimeoffset", "enum", "double", "int", "intarray", "stringarray",
    ];
    return map[kind] || "intseconds";
  }
  return String(kind || "IntSeconds").toLowerCase();
}

/**
 * @param {{ kind?: string|number }|undefined} entry
 * @param {string} raw
 */
function coerceValue(entry, raw) {
  const kind = normalizeKind(entry?.kind);
  if (kind === "bool") return raw === "true" || raw === "1";
  if (kind === "enum" || kind === "string") {
    if (!raw) throw new Error("Value is required.");
    return raw;
  }
  if (kind === "stringarray") {
    // Empty → [] (explicit empty allowlist). Comma-separated otherwise.
    if (!raw) return [];
    return raw.split(",").map((s) => s.trim()).filter(Boolean);
  }
  if (kind === "datetimeoffset") {
    if (!raw) throw new Error("Date/time is required.");
    const d = new Date(raw);
    if (Number.isNaN(d.getTime())) throw new Error("Invalid date/time.");
    return d.toISOString();
  }
  if (kind === "double") {
    const n = Number(raw);
    if (!Number.isFinite(n)) throw new Error("Number is required.");
    return n;
  }
  const n = Number(raw);
  if (!Number.isFinite(n) || !Number.isInteger(n) || n < 0) {
    throw new Error("Non-negative integer is required.");
  }
  return n;
}
