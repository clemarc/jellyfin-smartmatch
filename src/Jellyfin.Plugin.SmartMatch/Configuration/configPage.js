/* Smart Match admin page controller (ES module loaded by Jellyfin's view manager).
 * Every untrusted string (filenames, titles, paths, provider names) is rendered with textContent,
 * never as HTML. All calls go to admin-only /SmartMatch endpoints with the session token. */

const PAGE_SIZE = 50;

function api(method, path, body) {
    const request = {
        type: method,
        url: window.ApiClient.getUrl('SmartMatch/' + path),
        dataType: 'json'
    };
    if (body !== undefined) {
        request.data = JSON.stringify(body);
        request.contentType = 'application/json';
    }
    return window.ApiClient.ajax(request).catch(async (err) => {
        let payload = null;
        if (err && typeof err.json === 'function') {
            try { payload = await err.json(); } catch (e) { payload = null; }
        }
        const error = new Error((payload && (payload.Message || payload.message)) || (err && err.statusText) || 'Request failed');
        error.status = err && err.status;
        error.payload = payload;
        throw error;
    });
}

function el(tag, attrs, children) {
    const node = document.createElement(tag);
    if (attrs) {
        for (const [key, value] of Object.entries(attrs)) {
            if (value === undefined || value === null || value === false) continue;
            if (key === 'text') node.textContent = String(value);
            else if (key === 'className') node.className = value;
            else if (key.startsWith('on') && typeof value === 'function') node.addEventListener(key.substring(2), value);
            else node.setAttribute(key, value === true ? '' : String(value));
        }
    }
    for (const child of [].concat(children || [])) {
        if (child === null || child === undefined || child === false) continue;
        node.appendChild(typeof child === 'string' ? document.createTextNode(child) : child);
    }
    return node;
}

function button(label, onclick, opts) {
    // Every button is "raised": Jellyfin's flat style is unreadable on some themes. Primary actions also get button-submit.
    const b = el('button', Object.assign({ type: 'button', is: 'emby-button', className: 'emby-button raised' + ((opts && opts.raised) ? ' button-submit' : ''), onclick: onclick }, (opts && opts.attrs) || {}), [el('span', { text: label })]);
    return b;
}

function fmtDate(value) {
    if (!value) return '—';
    const d = new Date(value);
    if (isNaN(d.getTime()) || d.getUTCFullYear() < 2000) return '—';
    return d.toLocaleString();
}

function toast(message) {
    if (window.Dashboard && window.Dashboard.alert) {
        window.Dashboard.alert({ message: message, title: 'Smart Match' });
    } else {
        window.alert(message);
    }
}

function loading(show) {
    if (!window.Dashboard) return;
    if (show) window.Dashboard.showLoadingMsg(); else window.Dashboard.hideLoadingMsg();
}

function newKey() {
    if (window.crypto && window.crypto.randomUUID) return window.crypto.randomUUID();
    return 'k-' + Date.now() + '-' + Math.random().toString(16).slice(2);
}

const STATUS_LABELS = {
    Queued: 'Not checked',
    Proposed: 'Proposed',
    NeedsReview: 'Needs review',
    NoMatch: 'No match',
    ProviderError: 'Provider error',
    Applying: 'Applying',
    Applied: 'Applied',
    Partial: 'Partially applied',
    Conflict: 'Conflict',
    Ignored: 'Ignored',
    Rejected: 'Rejected',
    RolledBack: 'Rolled back',
    Resolved: 'Resolved'
};

export default function (view) {
    const $ = (sel) => view.querySelector(sel);
    const state = {
        status: null,
        config: null,
        page: 0,
        total: 0,
        rows: [],
        selected: new Map(),
        decisionsPage: 0,
        detailId: null,
        batchTimer: null,
        statusTimer: null,
        searchTimer: null
    };

    // ------------------------------------------------------------- overview

    async function loadStatus() {
        try {
            const s = await api('GET', 'Status');
            state.status = s;
            renderStatus(s);
        } catch (e) {
            showFault('Could not load Smart Match status: ' + e.message);
        }
    }

    function showFault(message) {
        const box = $('#smFault');
        box.textContent = message || '';
        box.classList.toggle('sm-hidden', !message);
    }

    function renderStatus(s) {
        $('#smVersion').textContent = 'v' + s.PluginVersion + ' · Jellyfin ' + s.ServerVersion;
        const compat = $('#smCompat');
        compat.textContent = s.Compatible ? 'Compatible (ABI ' + s.TargetAbi + ')' : 'Unsupported server version';
        compat.className = 'sm-badge ' + (s.Compatible ? 'sm-ok' : 'sm-bad');
        const mode = $('#smMode');
        mode.textContent = s.Mode === 'AutoApplyHighConfidence' ? 'Auto-apply high confidence' : 'Report only';
        mode.className = 'sm-badge ' + (s.Mode === 'AutoApplyHighConfidence' ? 'sm-warn' : 'sm-ok');
        showFault(s.Fault);

        const warnings = $('#smWarnings');
        warnings.replaceChildren(...(s.Warnings || []).map((w) => el('div', { text: w })));
        warnings.classList.toggle('sm-hidden', !(s.Warnings && s.Warnings.length));

        const c = s.Coverage || {};
        const statusItems = [
            ['Monitoring new scans', s.MonitoringActive ? 'Active' : 'Off', s.MonitoringActive ? 'sm-ok' : 'sm-warn'],
            ['Existing-library audit coverage', (c.CheckedItems || 0) + ' of ' + (c.TotalItems || 0) + ' checked' + (c.InitialAuditCompleted ? ' · initial audit complete' : ' · initial audit pending'), c.InitialAuditCompleted ? 'sm-ok' : 'sm-warn'],
            ['Background work queue', (s.QueueDue || 0) + ' due, ' + (s.QueueDeferred || 0) + ' waiting to retry · ' + s.WorkerState, ''],
            ['Last reconciliation', fmtDate(s.LastReconciliationUtc), ''],
            ['Auto-apply', s.AutoApplyEnabled ? 'Enabled for ' + s.Libraries.filter((l) => l.AutoApply).map((l) => l.Name).join(', ') : 'Disabled', s.AutoApplyEnabled ? 'sm-warn' : 'sm-ok'],
            ['Active run', s.ActiveAudit ? s.ActiveAudit.Kind + ' since ' + fmtDate(s.ActiveAudit.StartedAtUtc) : 'None', '']
        ];
        $('#smStatus').replaceChildren(...statusItems.map(([label, value, cls]) => el('div', null, [
            el('div', { className: 'sm-small sm-muted', text: label }),
            el('div', { className: cls, text: value })
        ])));
        $('#smCancelAudit').classList.toggle('sm-hidden', !s.ActiveAudit);

        const counts = s.Counts || {};
        const tiles = [
            ['Not checked', (counts.Queued || 0) + (s.QueueDue || 0), 'Queued'],
            ['Proposed', counts.Proposed || 0, 'Proposed'],
            ['Need review', counts.NeedsReview || 0, 'NeedsReview'],
            ['Applied', counts.Applied || 0, 'Applied'],
            ['Ignored', (counts.Ignored || 0) + (counts.Rejected || 0), 'Ignored'],
            ['Errors', (counts.ProviderError || 0) + (counts.Conflict || 0) + (counts.Partial || 0), 'ProviderError']
        ];
        $('#smTiles').replaceChildren(...tiles.map(([label, n, filter]) => el('button', {
            type: 'button',
            className: 'sm-tile',
            'aria-label': label + ': ' + n,
            onclick: () => { $('#smFStatus').value = filter; state.page = 0; selectTab('findings'); loadFindings(); }
        }, [el('span', { className: 'sm-num', text: String(n) }), el('span', { text: label })])));

        $('#smApplyAll').querySelector('span').textContent = 'Apply all eligible (' + (s.Eligible || 0) + ')…';

        const libSelect = $('#smFLibrary');
        const current = libSelect.value;
        libSelect.replaceChildren(el('option', { value: '', text: 'All' }), ...s.Libraries.map((l) => el('option', { value: l.Id, text: l.Name })));
        libSelect.value = current;
        $('#smDataDir').textContent = 'Smart Match data directory (inside Jellyfin\'s configuration, never in a media folder): ' + s.DataDirectory;
    }

    // ------------------------------------------------------------- findings

    function filterParams() {
        const p = new URLSearchParams();
        const add = (k, v) => { if (v) p.set(k, v); };
        add('status', $('#smFStatus').value);
        add('libraryId', $('#smFLibrary').value);
        add('mediaType', $('#smFType').value);
        add('confidence', $('#smFConfidence').value);
        add('search', $('#smFSearch').value.trim());
        return p;
    }

    function currentFilterObject() {
        const f = {};
        const status = $('#smFStatus').value;
        if (status) f.Statuses = [status];
        if ($('#smFLibrary').value) f.LibraryId = $('#smFLibrary').value;
        if ($('#smFType').value) f.MediaType = $('#smFType').value;
        if ($('#smFConfidence').value) f.Confidence = $('#smFConfidence').value;
        if ($('#smFSearch').value.trim()) f.Search = $('#smFSearch').value.trim();
        return f;
    }

    async function loadFindings() {
        const p = filterParams();
        p.set('sort', $('#smFSort').value);
        p.set('skip', String(state.page * PAGE_SIZE));
        p.set('take', String(PAGE_SIZE));
        try {
            const page = await api('GET', 'Reviews?' + p.toString());
            state.rows = page.Items;
            state.total = page.Total;
            renderFindings();
        } catch (e) {
            showFault('Could not load findings: ' + e.message);
        }
    }

    function renderFindings() {
        const tbody = $('#smFindingsTable tbody');
        tbody.replaceChildren(...state.rows.map((r) => {
            const checkbox = el('input', {
                type: 'checkbox',
                'aria-label': 'Select ' + r.OriginalName,
                onclick: (ev) => ev.stopPropagation(),
                onchange: (ev) => {
                    if (ev.target.checked) state.selected.set(r.ReviewId, r); else state.selected.delete(r.ReviewId);
                    updateSelectionButton();
                }
            });
            checkbox.checked = state.selected.has(r.ReviewId);
            const candidate = r.CandidateName ? r.CandidateName + (r.CandidateYear ? ' (' + r.CandidateYear + ')' : '') : '—';
            const score = (r.Score !== null && r.Score !== undefined ? r.Score + '/100' : '—') + (r.AutoEligible ? ' · auto-eligible' : '');
            const statusClass = { Applied: 'sm-ok', Proposed: 'sm-ok', NeedsReview: 'sm-warn', Conflict: 'sm-bad', ProviderError: 'sm-bad', Partial: 'sm-bad' }[r.Status] || '';
            const tr = el('tr', {
                tabindex: '0',
                className: state.detailId === r.ReviewId ? 'sm-selected-row' : '',
                onclick: () => openDetail(r.ReviewId),
                onkeydown: (ev) => { if (ev.key === 'Enter') openDetail(r.ReviewId); }
            }, [
                el('td', null, [checkbox]),
                el('td', { text: r.OriginalName }),
                el('td', { text: r.SearchTitle + (r.TrustedYear ? ' (' + r.TrustedYear + ')' : '') }),
                el('td', { className: 'sm-hide-mobile', text: r.MediaType }),
                el('td', { className: 'sm-hide-mobile', text: r.LibraryName }),
                el('td', { text: candidate }),
                el('td', null, [el('div', { text: score }), el('div', { className: 'sm-small sm-muted', text: r.Reason || '' })]),
                el('td', { className: statusClass, text: (STATUS_LABELS[r.Status] || r.Status) + (r.SidecarRisk ? ' ⚠' : '') }),
                el('td', { className: 'sm-hide-mobile', text: r.Origin === 'NewScan' ? 'New scan' : (r.Origin === 'ExistingAudit' ? 'Existing audit' : r.Origin) }),
                el('td', { className: 'sm-hide-mobile', text: fmtDate(r.LastCheckedUtc) })
            ]);
            return tr;
        }));
        const from = state.total === 0 ? 0 : state.page * PAGE_SIZE + 1;
        const to = Math.min(state.total, (state.page + 1) * PAGE_SIZE);
        $('#smPageInfo').textContent = from + '–' + to + ' of ' + state.total;
        $('#smPrev').disabled = state.page === 0;
        $('#smNext').disabled = to >= state.total;
        $('#smSelectPage').checked = state.rows.length > 0 && state.rows.every((r) => state.selected.has(r.ReviewId));
        renderEmptyState();
        updateSelectionButton();
    }

    function renderEmptyState() {
        const empty = $('#smEmpty');
        const s = state.status;
        let message = null;
        if (state.total === 0) {
            if (s && s.Libraries.filter((l) => l.InScope).length === 0) {
                message = 'No libraries selected. Choose libraries under Settings.';
            } else if (s && s.Counts && s.Counts.ProviderError > 0 && !$('#smFStatus').value) {
                message = 'Providers unavailable: some searches failed and will be retried. Check network access to your metadata providers.';
            } else if (filterParams().toString()) {
                message = 'No findings match the current filter.';
            } else if (s && s.Coverage && !s.Coverage.InitialAuditCompleted) {
                message = 'The first audit has not finished yet. Findings appear here as items are checked.';
            } else {
                message = 'No unmatched media. Everything checked so far is already identified.';
            }
        }
        empty.textContent = message || '';
        empty.classList.toggle('sm-hidden', !message);
    }

    function updateSelectionButton() {
        const n = state.selected.size;
        const btn = $('#smApplySelected');
        btn.querySelector('span').textContent = 'Apply selected (' + n + ')';
        btn.disabled = n === 0;
    }

    async function openDetail(reviewId) {
        state.detailId = reviewId;
        const panel = $('#smDetail');
        panel.classList.remove('sm-hidden');
        panel.replaceChildren(el('div', { text: 'Loading…' }));
        try {
            const d = await api('GET', 'Reviews/' + reviewId);
            renderDetail(d);
            panel.focus();
        } catch (e) {
            panel.replaceChildren(el('div', { className: 'sm-error', text: 'Could not load details: ' + e.message }));
        }
        renderFindings();
    }

    function renderDetail(d) {
        const f = d.Finding;
        const panel = $('#smDetail');
        const pathBox = el('span', { className: 'sm-trace', text: f.SourceName || '' });
        const reveal = button('Reveal full path', () => {
            pathBox.textContent = d.Path || '(item no longer exists)';
            reveal.remove();
        });

        const actions = [];
        if (f.Status === 'Proposed' || f.Status === 'NeedsReview') {
            if (f.RecommendedCandidateId && f.Status === 'Proposed') {
                actions.push(button('Apply recommended', () => applyOne(f, f.RecommendedCandidateId), { raised: true }));
            }
            actions.push(button('Reject candidates', () => simpleAction(f, 'Reject')));
        }
        if (f.Status === 'Partial') {
            actions.push(button('Retry refresh', () => simpleAction(f, 'Retry'), { raised: true }));
        }
        if (f.Status === 'Applied' || f.Status === 'Partial') {
            actions.push(button('Roll back', () => rollback(f)));
        }
        if (f.Status !== 'Ignored' && f.Status !== 'Applied' && f.Status !== 'Applying') {
            actions.push(button('Ignore', () => simpleAction(f, 'Ignore')));
        }
        if (['NoMatch', 'ProviderError', 'Conflict', 'Rejected', 'Ignored', 'RolledBack', 'NeedsReview', 'Proposed', 'Resolved'].includes(f.Status)) {
            actions.push(button(f.Status === 'Ignored' ? 'Stop ignoring and re-check' : 'Retry analysis', () => simpleAction(f, 'Retry')));
        }
        actions.push(button('Close', () => { state.detailId = null; panel.classList.add('sm-hidden'); renderFindings(); }));

        const candidates = (f.Candidates || []).map((c) => el('div', { className: 'sm-candidate' }, [
            el('div', null, [
                el('strong', { text: c.Name + (c.Year ? ' (' + c.Year + ')' : '') }),
                ' ',
                el('span', { className: 'sm-badge', text: c.RankScore + '/100' }),
                c.AutoEligible ? el('span', { className: 'sm-badge sm-ok', text: 'auto-eligible' }) : null,
                c.CandidateId === f.RecommendedCandidateId ? el('span', { className: 'sm-badge', text: 'recommended' }) : null,
                c.YearConflict ? el('span', { className: 'sm-badge sm-bad', text: 'year conflict' }) : null
            ]),
            el('div', { className: 'sm-small sm-muted', text: 'Provider: ' + (c.Providers || []).join(', ') + ' · ids: ' + Object.entries(c.ProviderIds || {}).map(([k, v]) => k + '=' + v).join(', ') }),
            el('div', { className: 'sm-small', text: (c.Reasons || []).join(' · ') }),
            (c.Applicable && (f.Status === 'Proposed' || f.Status === 'NeedsReview'))
                ? button('Apply this candidate', () => applyOne(f, c.CandidateId), { raised: c.CandidateId === f.RecommendedCandidateId })
                : null
        ]));

        const decisions = (d.Decisions || []).map((x) => el('div', { className: 'sm-small' }, [
            fmtDate(x.TimeUtc) + ' · ' + x.Action + (x.Origin ? ' (' + x.Origin + ')' : '') + ' · ' + x.Outcome + (x.Message ? ' · ' + x.Message : '')
        ]));

        panel.replaceChildren(
            el('h3', { text: f.OriginalName }),
            el('div', { className: 'sm-grid2' }, [
                el('div', null, [
                    el('div', null, [el('strong', { text: 'Proposed title: ' }), f.SearchTitle + (f.TrustedYear ? ' (' + f.TrustedYear + ')' : '')]),
                    el('div', null, [el('strong', { text: 'Status: ' }), (STATUS_LABELS[f.Status] || f.Status) + (f.Reason ? ' — ' + f.Reason : '')]),
                    el('div', null, [el('strong', { text: 'Source: ' }), pathBox, ' ', reveal]),
                    f.EpisodeContext ? el('div', null, [el('strong', { text: 'Episode context: ' }), f.EpisodeContext]) : null,
                    el('div', null, [el('strong', { text: 'Current Jellyfin title: ' }), d.CurrentName || '—']),
                    el('div', null, [el('strong', { text: 'Current provider ids: ' }), Object.entries(d.CurrentProviderIds || {}).map(([k, v]) => k + '=' + v).join(', ') || 'none']),
                    f.SidecarRisk ? el('div', { className: 'sm-warn', text: 'This library saves metadata next to media: Jellyfin may write NFO/artwork files during the refresh.' }) : null,
                    (f.ProviderErrors && f.ProviderErrors.length) ? el('div', { className: 'sm-bad', text: 'Provider errors: ' + f.ProviderErrors.join('; ') }) : null
                ]),
                el('div', null, [
                    el('strong', { text: 'Normalization trace' }),
                    el('div', { className: 'sm-trace', text: (f.NormalizationTrace || []).join('\n') || '(no changes)' }),
                    (f.Warnings && f.Warnings.length) ? el('div', { className: 'sm-warn sm-small', text: 'Warnings: ' + f.Warnings.join('; ') }) : null,
                    (f.AutoBlockers && f.AutoBlockers.length) ? el('div', { className: 'sm-small sm-muted', text: 'Not automatic because: ' + f.AutoBlockers.join('; ') }) : null
                ])
            ]),
            el('h4', { text: 'Candidates' }),
            candidates.length ? el('div', null, candidates) : el('div', { className: 'sm-muted', text: 'No candidates.' }),
            el('div', { className: 'sm-row', style: 'margin-top: 1em' }, actions),
            el('h4', { text: 'History' }),
            decisions.length ? el('div', null, decisions) : el('div', { className: 'sm-muted sm-small', text: 'No actions yet.' })
        );
    }

    async function afterAction(result, reviewId) {
        if (result && result.Message) toast(result.Message);
        await Promise.all([loadStatus(), loadFindings()]);
        if (reviewId) openDetail(reviewId);
    }

    async function applyOne(f, candidateId) {
        const candidate = (f.Candidates || []).find((c) => c.CandidateId === candidateId);
        const ok = await confirmDialog('Apply this identification?', [
            el('p', { text: f.OriginalName + ' → ' + (candidate ? candidate.Name + (candidate.Year ? ' (' + candidate.Year + ')' : '') : candidateId) }),
            el('p', { className: 'sm-small', text: 'Smart Match writes the provider ids ' + Object.entries((candidate && candidate.ProviderIds) || {}).map(([k, v]) => k + '=' + v).join(', ') + ' and asks Jellyfin to refresh. The media path is not touched.' }),
            f.SidecarRisk ? el('p', { className: 'sm-warn', text: 'This library saves metadata next to media; Jellyfin may write NFO or artwork files during the refresh.' }) : null
        ], 'Apply');
        if (!ok) return;
        loading(true);
        try {
            const r = await api('POST', 'Reviews/' + f.ReviewId + '/Apply', { CandidateId: candidateId, Version: f.Version });
            await afterAction(r, f.ReviewId);
        } catch (e) {
            toast((e.payload && e.payload.Message) || e.message);
            await afterAction(null, f.ReviewId);
        } finally {
            loading(false);
        }
    }

    async function simpleAction(f, action) {
        let note;
        if (action === 'Ignore' || action === 'Reject') {
            note = window.prompt(action + ' "' + f.OriginalName + '"? Optional note:', '');
            if (note === null) return;
        }
        loading(true);
        try {
            const r = await api('POST', 'Reviews/' + f.ReviewId + '/' + action, { Version: f.Version, Note: note || null });
            await afterAction(r, f.ReviewId);
        } catch (e) {
            toast((e.payload && e.payload.Message) || e.message);
        } finally {
            loading(false);
        }
    }

    async function rollback(f) {
        const ok = await confirmDialog('Roll back this application?', [
            el('p', { text: 'Smart Match restores the provider ids and title it changed on "' + f.OriginalName + '", but only if nobody has edited them since. Descriptions and artwork Jellyfin downloaded stay until a Jellyfin refresh replaces them.' })
        ], 'Roll back');
        if (!ok) return;
        loading(true);
        try {
            const r = await api('POST', 'Reviews/' + f.ReviewId + '/Rollback', { Version: f.Version });
            await afterAction(r, f.ReviewId);
        } catch (e) {
            toast((e.payload && e.payload.Message) || e.message);
        } finally {
            loading(false);
        }
    }

    // ------------------------------------------------------------- bulk apply

    async function previewBatch(request) {
        loading(true);
        let plan;
        try {
            plan = await api('POST', 'ApplyBatch/Preview', request);
        } catch (e) {
            toast((e.payload && e.payload.Message) || e.message);
            return;
        } finally {
            loading(false);
        }

        const summary = Object.entries(plan.ExclusionSummary || {}).map(([reason, n]) => el('li', { text: n + ' × ' + reason }));
        const items = plan.Items.slice(0, 100).map((i) => el('tr', null, [
            el('td', { text: i.Title }),
            el('td', { text: i.CandidateName + (i.CandidateYear ? ' (' + i.CandidateYear + ')' : '') }),
            el('td', { text: Object.entries(i.ProviderIds || {}).map(([k, v]) => k + '=' + v).join(', ') + (i.WritesTitle ? ' + title' : '') }),
            el('td', { text: String(i.RankScore) }),
            el('td', { className: 'sm-warn sm-small', text: (i.Warnings || []).join(' ') })
        ]));
        const exclusions = (plan.Exclusions || []).slice(0, 50).map((x) => el('li', { className: 'sm-small', text: x.Title + ' — ' + x.Reason }));
        const content = [
            el('p', null, [el('strong', { text: 'Scope: ' }), plan.ScopeDescription]),
            el('p', null, [el('strong', { text: String(plan.Items.length) }), ' finding(s) will be applied, one at a time, each re-checked right before its write. ', el('strong', { text: String((plan.Exclusions || []).length) }), ' excluded.']),
            summary.length ? el('div', null, [el('strong', { text: 'Exclusions' }), el('ul', null, summary)]) : null,
            items.length ? el('div', { className: 'sm-table-wrap' }, [el('table', { className: 'sm-table' }, [
                el('thead', null, [el('tr', null, ['Item', 'Candidate', 'Writes', 'Score', 'Warnings'].map((h) => el('th', { text: h })))]),
                el('tbody', null, items)
            ])]) : null,
            plan.Items.length > 100 ? el('p', { className: 'sm-small sm-muted', text: '…and ' + (plan.Items.length - 100) + ' more.' }) : null,
            exclusions.length ? el('details', null, [el('summary', { text: 'Excluded items' }), el('ul', null, exclusions)]) : null,
            el('p', { className: 'sm-small sm-muted', text: 'Ambiguous, locked, already-identified and stale findings are never included. This preview expires at ' + fmtDate(plan.ExpiresUtc) + '.' })
        ];
        if (plan.Items.length === 0) {
            await confirmDialog('Nothing to apply', content, null);
            return;
        }
        const ok = await confirmDialog('Confirm bulk application', content, 'Apply ' + plan.Items.length);
        if (!ok) return;
        try {
            const run = await api('POST', 'ApplyBatch/' + plan.PlanId + '/Commit', { PlanHash: plan.PlanHash, IdempotencyKey: newKey() });
            state.selected.clear();
            trackBatch(run.RunId);
        } catch (e) {
            toast((e.payload && e.payload.Message) || e.message);
        }
    }

    function trackBatch(runId) {
        const box = $('#smBatchProgress');
        box.classList.remove('sm-hidden');
        clearInterval(state.batchTimer);
        const poll = async () => {
            let run;
            try {
                run = await api('GET', 'ApplyBatch/' + runId);
            } catch (e) {
                return;
            }
            const done = run.Items.filter((i) => i.Outcome).length;
            const counts = {};
            run.Items.forEach((i) => { if (i.Outcome) counts[i.Outcome] = (counts[i.Outcome] || 0) + 1; });
            const failures = run.Items.filter((i) => i.Outcome && i.Outcome !== 'Applied').slice(0, 30).map((i) => el('li', { className: 'sm-small', text: i.Title + ': ' + i.Outcome + (i.Message ? ' — ' + i.Message : '') }));
            const running = run.Status === 'Running';
            const current = running ? run.Items.find((i) => !i.Outcome && i.StartedUtc) : null;
            const seconds = current ? Math.max(0, Math.round((Date.now() - Date.parse(current.StartedUtc)) / 1000)) : 0;
            // replaceChildren turns null into the text "null", so absent parts are filtered out.
            box.replaceChildren(...[
                el('strong', { text: 'Bulk run ' + run.Status + ': ' + done + ' of ' + run.Items.length }),
                current ? el('div', { className: 'sm-small', text: 'Applying ' + current.Title + ' (' + seconds + ' s). Jellyfin refreshes each item\'s metadata and images before the next one starts; on small hardware this can take a minute per item.' }) : null,
                done ? el('div', { text: Object.entries(counts).map(([k, v]) => k + ' ' + v).join(' · ') }) : null,
                failures.length ? el('ul', null, failures) : null,
                running ? button('Cancel remaining', async () => { await api('POST', 'ApplyBatch/' + runId + '/Cancel'); poll(); }) : button('Dismiss', () => box.classList.add('sm-hidden'))
            ].filter(Boolean));
            if (!running) {
                clearInterval(state.batchTimer);
                loadStatus();
                loadFindings();
            }
        };
        poll();
        state.batchTimer = setInterval(poll, 2000);
    }

    function confirmDialog(title, content, confirmLabel) {
        return new Promise((resolve) => {
            const previous = document.activeElement;
            let overlay;
            const close = (value) => {
                overlay.remove();
                document.removeEventListener('keydown', onKey, true);
                if (previous && previous.focus) previous.focus();
                resolve(value);
            };
            const onKey = (ev) => { if (ev.key === 'Escape') { ev.stopPropagation(); close(false); } };
            const confirmBtn = confirmLabel ? button(confirmLabel, () => close(true), { raised: true }) : null;
            const cancelBtn = button(confirmLabel ? 'Cancel' : 'Close', () => close(false));
            overlay = el('div', { className: 'sm-dialog', role: 'dialog', 'aria-modal': 'true', 'aria-label': title }, [
                el('div', { className: 'sm-dialog-body' }, [
                    el('h3', { text: title }),
                    ...content.filter(Boolean),
                    el('div', { className: 'sm-row', style: 'margin-top: 1em' }, [confirmBtn, cancelBtn])
                ])
            ]);
            view.appendChild(overlay);
            document.addEventListener('keydown', onKey, true);
            (confirmBtn || cancelBtn).focus();
        });
    }

    // ------------------------------------------------------------- history

    async function loadHistory() {
        try {
            const [decisions, batches, runs] = await Promise.all([
                api('GET', 'Decisions?skip=' + (state.decisionsPage * PAGE_SIZE) + '&take=' + PAGE_SIZE),
                api('GET', 'ApplyBatch'),
                api('GET', 'Audits')
            ]);
            $('#smDecisions tbody').replaceChildren(...decisions.Items.map((d) => el('tr', null, [
                el('td', { text: fmtDate(d.TimeUtc) }),
                el('td', { text: d.Action }),
                el('td', { text: d.Origin || '' }),
                el('td', { text: d.ItemName }),
                el('td', { text: d.Outcome + (d.ErrorCode ? ' (' + d.ErrorCode + ')' : '') }),
                el('td', { className: 'sm-small', text: [Object.entries(d.WrittenProviderIds || {}).map(([k, v]) => k + '=' + v).join(', '), d.WrittenTitle ? 'title: ' + d.WrittenTitle : '', d.Message || ''].filter(Boolean).join(' · ') })
            ])));
            const from = decisions.Total === 0 ? 0 : decisions.Skip + 1;
            $('#smDecInfo').textContent = from + '–' + Math.min(decisions.Total, decisions.Skip + decisions.Take) + ' of ' + decisions.Total;
            $('#smDecPrev').disabled = state.decisionsPage === 0;
            $('#smDecNext').disabled = decisions.Skip + decisions.Take >= decisions.Total;
            $('#smBatches tbody').replaceChildren(...batches.map((b) => {
                const counts = {};
                b.Items.forEach((i) => { const k = i.Outcome || 'Pending'; counts[k] = (counts[k] || 0) + 1; });
                return el('tr', { onclick: () => { selectTab('findings'); trackBatch(b.RunId); } }, [
                    el('td', { text: fmtDate(b.StartedAtUtc) }),
                    el('td', { text: b.Scope }),
                    el('td', { text: b.Status }),
                    el('td', { text: Object.entries(counts).map(([k, v]) => k + ' ' + v).join(' · ') })
                ]);
            }));
            $('#smRuns tbody').replaceChildren(...runs.map((r) => el('tr', null, [
                el('td', { text: fmtDate(r.StartedAtUtc) }),
                el('td', { text: r.Kind + ' (' + r.StartedBy + ')' }),
                el('td', { text: r.Status + (r.EndedAtUtc ? ' · ' + fmtDate(r.EndedAtUtc) : '') }),
                el('td', { text: String(r.Enqueued) }),
                el('td', { text: String(r.Searches) }),
                el('td', { className: 'sm-small', text: Object.entries(r.Counts || {}).map(([k, v]) => k + ' ' + v).join(' · ') + (Object.keys(r.SkipReasons || {}).length ? ' · skipped: ' + Object.entries(r.SkipReasons).map(([k, v]) => k + ' ' + v).join(', ') : '') }),
                el('td', { className: 'sm-small', text: (r.ProviderErrors || []).slice(0, 3).join('; ') })
            ])));
        } catch (e) {
            showFault('Could not load history: ' + e.message);
        }
    }

    // ------------------------------------------------------------- settings

    async function loadConfig() {
        try {
            state.config = await api('GET', 'Config');
            if (!state.status) await loadStatus();
            renderConfig();
        } catch (e) {
            showFault('Could not load settings: ' + e.message);
        }
    }

    function renderConfig() {
        const c = state.config;
        view.querySelectorAll('input[name="smModeRadio"]').forEach((r) => { r.checked = r.value === c.Mode; });
        $('#smAllLibraries').checked = c.IncludeAllLibraries;
        const included = new Set(c.IncludedLibraryIds || []);
        const auto = new Set(c.AutoApplyLibraryIds || []);
        const ack = new Set(c.SidecarAcknowledgedLibraryIds || []);
        const libs = (state.status && state.status.Libraries) || [];
        $('#smLibraries tbody').replaceChildren(...libs.map((l) => {
            const scope = el('input', { type: 'checkbox', 'data-lib': l.Id, 'data-kind': 'scope', 'aria-label': 'Check ' + l.Name });
            scope.checked = c.IncludeAllLibraries || included.has(l.Id);
            scope.disabled = c.IncludeAllLibraries;
            const autoBox = el('input', { type: 'checkbox', 'data-lib': l.Id, 'data-kind': 'auto', 'aria-label': 'Auto-apply in ' + l.Name });
            autoBox.checked = auto.has(l.Id);
            const ackBox = l.SidecarRisk ? el('input', { type: 'checkbox', 'data-lib': l.Id, 'data-kind': 'ack', 'aria-label': 'Accept sidecar writes for automation in ' + l.Name }) : null;
            if (ackBox) ackBox.checked = ack.has(l.Id);
            return el('tr', null, [
                el('td', { text: l.Name }),
                el('td', { text: l.CollectionType || 'mixed' }),
                el('td', null, [scope]),
                el('td', null, [autoBox]),
                el('td', { className: 'sm-small' }, l.SidecarRisk
                    ? [el('div', { className: 'sm-warn', text: l.SidecarDetail }), el('label', null, [ackBox, ' allow auto-apply anyway'])]
                    : [el('span', { className: 'sm-ok', text: 'none' })])
            ]);
        }));
        $('#smMonitor').checked = c.MonitorNewScans;
        $('#smInitialAudit').checked = c.InitialAuditEnabled;
        $('#smDeferScan').checked = c.DeferWhileScanning;
        $('#smSettle').value = c.SettleDelaySeconds;
        $('#smStartup').value = c.StartupDelaySeconds;
        $('#smInterval').value = c.MinSearchIntervalMs;
        $('#smTimeout').value = c.ProviderTimeoutSeconds;
        $('#smAttempts').value = c.ProviderMaxAttempts;
        $('#smBudget').value = c.MaxSearchesPerRun;
        $('#smProfile').value = c.RefreshProfile;
        $('#smWriteTitle').checked = c.WriteCanonicalTitle;
        $('#smRefreshTimeout').value = c.RefreshTimeoutSeconds;
        $('#smAutoScore').value = c.AutoMinScore;
        $('#smAutoMargin').value = c.AutoMargin;
        $('#smYearTol').value = c.YearTolerance;
        $('#smRequireYear').checked = c.AutoRequireYear;
        $('#smExtraKeys').value = (c.ExtraProviderIdKeys || []).join(', ');
        $('#smRetDays').value = c.RetentionDays;
        $('#smRetMax').value = c.RetentionMaxDecisions;
        $('#smDebugPaths').checked = c.DebugLogPaths;
        $('#smBuiltInRules').textContent = 'Built-in: UIndex.org, well-known torrent index domains, "www.<site> - " and bracketed site domains at the start of a name.';
        renderRules(c.PrefixRules || []);
        $('#smSettingsErrors').classList.add('sm-hidden');
    }

    function renderRules(rules) {
        $('#smRules tbody').replaceChildren(...rules.map((r) => ruleRow(r)));
    }

    function ruleRow(r) {
        const input = (field, label) => {
            const i = el('input', { type: 'text', className: 'sm-input', 'data-field': field, 'aria-label': label, maxlength: field === 'Pattern' ? '200' : '512' });
            i.value = r[field] || '';
            return i;
        };
        const enabled = el('input', { type: 'checkbox', 'data-field': 'Enabled', 'aria-label': 'Enabled' });
        enabled.checked = !!r.Enabled;
        const result = el('div', { className: 'sm-small' });
        const row = el('tr', null, [
            el('td', null, [input('Id', 'Rule id')]),
            el('td', null, [input('Pattern', 'Pattern')]),
            el('td', null, [input('ExampleInput', 'Example input')]),
            el('td', null, [input('ExampleOutput', 'Expected output'), result]),
            el('td', null, [enabled]),
            el('td', null, [
                button('Test', async () => {
                    const pattern = row.querySelector('[data-field="Pattern"]').value;
                    const sample = row.querySelector('[data-field="ExampleInput"]').value;
                    try {
                        const out = await api('POST', 'Normalize/Preview', { Name: sample, MediaType: 'Series', RulePattern: pattern });
                        result.textContent = '→ ' + out.SearchTitle + '  (' + out.Steps.map((s) => s.Operation).join(', ') + ')';
                        result.className = 'sm-small sm-ok';
                    } catch (e) {
                        result.textContent = (e.payload && e.payload.Message) || e.message;
                        result.className = 'sm-small sm-bad';
                    }
                }),
                button('Remove', () => row.remove())
            ])
        ]);
        return row;
    }

    function readRules() {
        return Array.from(view.querySelectorAll('#smRules tbody tr')).map((tr) => ({
            Id: tr.querySelector('[data-field="Id"]').value.trim(),
            Pattern: tr.querySelector('[data-field="Pattern"]').value,
            Description: 'Custom rule',
            ExampleInput: tr.querySelector('[data-field="ExampleInput"]').value,
            ExampleOutput: tr.querySelector('[data-field="ExampleOutput"]').value.trim(),
            Enabled: tr.querySelector('[data-field="Enabled"]').checked
        }));
    }

    async function saveConfig(ev) {
        ev.preventDefault();
        const c = Object.assign({}, state.config);
        const checked = (kind) => Array.from(view.querySelectorAll('#smLibraries input[data-kind="' + kind + '"]')).filter((i) => i.checked).map((i) => i.getAttribute('data-lib'));
        const modeRadio = view.querySelector('input[name="smModeRadio"]:checked');
        c.Mode = modeRadio ? modeRadio.value : 'ReportOnly';
        c.IncludeAllLibraries = $('#smAllLibraries').checked;
        c.IncludedLibraryIds = c.IncludeAllLibraries ? (state.config.IncludedLibraryIds || []) : checked('scope');
        c.AutoApplyLibraryIds = checked('auto');
        c.SidecarAcknowledgedLibraryIds = checked('ack');
        c.MonitorNewScans = $('#smMonitor').checked;
        c.InitialAuditEnabled = $('#smInitialAudit').checked;
        c.DeferWhileScanning = $('#smDeferScan').checked;
        c.SettleDelaySeconds = parseInt($('#smSettle').value, 10);
        c.StartupDelaySeconds = parseInt($('#smStartup').value, 10);
        c.MinSearchIntervalMs = parseInt($('#smInterval').value, 10);
        c.ProviderTimeoutSeconds = parseInt($('#smTimeout').value, 10);
        c.ProviderMaxAttempts = parseInt($('#smAttempts').value, 10);
        c.MaxSearchesPerRun = parseInt($('#smBudget').value, 10);
        c.RefreshProfile = $('#smProfile').value;
        c.WriteCanonicalTitle = $('#smWriteTitle').checked;
        c.RefreshTimeoutSeconds = parseInt($('#smRefreshTimeout').value, 10);
        c.AutoMinScore = parseInt($('#smAutoScore').value, 10);
        c.AutoMargin = parseInt($('#smAutoMargin').value, 10);
        c.YearTolerance = parseInt($('#smYearTol').value, 10);
        c.AutoRequireYear = $('#smRequireYear').checked;
        c.ExtraProviderIdKeys = $('#smExtraKeys').value.split(',').map((s) => s.trim()).filter(Boolean);
        c.RetentionDays = parseInt($('#smRetDays').value, 10);
        c.RetentionMaxDecisions = parseInt($('#smRetMax').value, 10);
        c.DebugLogPaths = $('#smDebugPaths').checked;
        c.PrefixRules = readRules();

        if (c.Mode === 'AutoApplyHighConfidence' && c.AutoApplyLibraryIds.length > 0 && state.config.Mode !== 'AutoApplyHighConfidence') {
            const ok = await confirmDialog('Enable automatic application?', [
                el('p', { text: 'High-confidence, unique matches in the ticked libraries will be applied without asking during scans, reconciliations and audits. Ambiguous or weak matches stay in the report. You can switch back to Report only at any time; queued automatic work stops immediately.' })
            ], 'Enable auto-apply');
            if (!ok) return;
        }

        loading(true);
        try {
            state.config = await api('PUT', 'Config', c);
            renderConfig();
            await loadStatus();
            toast('Settings saved.');
        } catch (e) {
            const box = $('#smSettingsErrors');
            const errors = (e.payload && e.payload.Errors) || [e.message];
            box.replaceChildren(...errors.map((x) => el('div', { text: x })));
            box.classList.remove('sm-hidden');
        } finally {
            loading(false);
        }
    }

    async function previewName() {
        const out = $('#smPreviewOut');
        try {
            const r = await api('POST', 'Normalize/Preview', { Name: $('#smPreviewName').value, MediaType: $('#smPreviewType').value, IsFile: $('#smPreviewFile').checked });
            out.textContent = 'Search title: ' + (r.SearchTitle || '(none)') + '\nYear: ' + (r.TrustedYear || '—') + (r.EpisodeContext ? '\nEpisode: ' + r.EpisodeContext : '') + '\n' + r.Steps.map((s) => '• ' + s.Operation + (s.RuleId ? ' (' + s.RuleId + ')' : '') + ': ' + s.Detail).join('\n') + (r.Warnings.length ? '\nWarnings: ' + r.Warnings.join('; ') : '');
        } catch (e) {
            out.textContent = (e.payload && e.payload.Message) || e.message;
        }
    }

    // ------------------------------------------------------------- export

    async function exportReport(format) {
        const url = window.ApiClient.getUrl('SmartMatch/Export', { format: format, status: $('#smFStatus').value || undefined });
        const response = await fetch(url, { headers: { Authorization: 'MediaBrowser Token="' + window.ApiClient.accessToken() + '"' } });
        if (!response.ok) {
            toast('Export failed (' + response.status + ').');
            return;
        }
        const blob = await response.blob();
        const link = el('a', { href: URL.createObjectURL(blob), download: 'smartmatch-report.' + format });
        view.appendChild(link);
        link.click();
        setTimeout(() => { URL.revokeObjectURL(link.href); link.remove(); }, 1000);
    }

    // ------------------------------------------------------------- tabs & wiring

    function selectTab(name) {
        view.querySelectorAll('.sm-tab').forEach((t) => t.setAttribute('aria-selected', String(t.getAttribute('data-tab') === name)));
        $('#smTabFindings').classList.toggle('sm-hidden', name !== 'findings');
        $('#smTabHistory').classList.toggle('sm-hidden', name !== 'history');
        $('#smTabSettings').classList.toggle('sm-hidden', name !== 'settings');
        $('#smTabSafety').classList.toggle('sm-hidden', name !== 'safety');
        if (name === 'history') loadHistory();
        if (name === 'settings') loadConfig();
    }

    view.querySelectorAll('.sm-tab').forEach((t) => t.addEventListener('click', () => selectTab(t.getAttribute('data-tab'))));
    $('#smViewFindings').addEventListener('click', () => { selectTab('findings'); loadFindings(); });
    $('#smOpenSettings').addEventListener('click', () => selectTab('settings'));
    $('#smAudit').addEventListener('click', async () => {
        const ok = await confirmDialog('Audit existing library?', [el('p', { text: 'Checks every film and series in the selected libraries in the background, one provider search at a time. Unchanged items that were already checked are skipped. In Report only mode nothing in Jellyfin changes.' })], 'Start audit');
        if (!ok) return;
        try {
            await api('POST', 'Audits', { Force: false });
            await loadStatus();
        } catch (e) {
            toast((e.payload && e.payload.Message) || e.message);
        }
    });
    $('#smReconcile').addEventListener('click', async () => {
        try {
            const run = await api('POST', 'Reconcile');
            toast(run.Enqueued + ' new or changed item(s) queued.');
            await loadStatus();
        } catch (e) {
            toast((e.payload && e.payload.Message) || e.message);
        }
    });
    $('#smCancelAudit').addEventListener('click', async () => {
        const run = state.status && state.status.ActiveAudit;
        if (!run) return;
        await api('POST', 'Audits/' + run.RunId + '/Cancel');
        await loadStatus();
    });
    ['#smFStatus', '#smFLibrary', '#smFType', '#smFConfidence', '#smFSort'].forEach((id) => $(id).addEventListener('change', () => { state.page = 0; loadFindings(); }));
    $('#smFSearch').addEventListener('input', () => {
        clearTimeout(state.searchTimer);
        state.searchTimer = setTimeout(() => { state.page = 0; loadFindings(); }, 300);
    });
    $('#smPrev').addEventListener('click', () => { if (state.page > 0) { state.page--; loadFindings(); } });
    $('#smNext').addEventListener('click', () => { state.page++; loadFindings(); });
    $('#smDecPrev').addEventListener('click', () => { if (state.decisionsPage > 0) { state.decisionsPage--; loadHistory(); } });
    $('#smDecNext').addEventListener('click', () => { state.decisionsPage++; loadHistory(); });
    $('#smSelectPage').addEventListener('change', (ev) => {
        state.rows.forEach((r) => { if (ev.target.checked) state.selected.set(r.ReviewId, r); else state.selected.delete(r.ReviewId); });
        renderFindings();
    });
    $('#smApplySelected').addEventListener('click', () => previewBatch({ Scope: 'Selected', ReviewIds: Array.from(state.selected.keys()) }));
    $('#smApplyAll').addEventListener('click', () => {
        const scope = view.querySelector('input[name="smScope"]:checked').value;
        previewBatch({ Scope: 'AllEligible', Filter: scope === 'filter' ? currentFilterObject() : null });
    });
    $('#smExportCsv').addEventListener('click', () => exportReport('csv'));
    $('#smExportJson').addEventListener('click', () => exportReport('json'));
    $('#smSettingsForm').addEventListener('submit', saveConfig);
    $('#smAddRule').addEventListener('click', () => $('#smRules tbody').appendChild(ruleRow({ Id: 'rule' + (view.querySelectorAll('#smRules tbody tr').length + 1), Enabled: false })));
    $('#smAllLibraries').addEventListener('change', (ev) => view.querySelectorAll('#smLibraries input[data-kind="scope"]').forEach((i) => { i.disabled = ev.target.checked; if (ev.target.checked) i.checked = true; }));
    $('#smPreviewBtn').addEventListener('click', previewName);

    view.addEventListener('viewshow', () => {
        loading(true);
        Promise.all([loadStatus(), loadFindings()]).finally(() => loading(false));
        clearInterval(state.statusTimer);
        state.statusTimer = setInterval(() => { if (!document.hidden) loadStatus(); }, 10000);
    });
    view.addEventListener('viewhide', () => {
        clearInterval(state.statusTimer);
        clearInterval(state.batchTimer);
    });
}
