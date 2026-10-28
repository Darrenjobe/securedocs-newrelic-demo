// Fault Control panel: reads/writes /fault/state and fires one-shot triggers.
(() => {
    const panel = document.getElementById('faultPanel');
    if (!panel) return;
    const inputs = [...panel.querySelectorAll('[data-fault]')];
    const log = document.getElementById('faultLog');
    const dot = document.getElementById('faultDot');
    let state = null;
    let saveTimer = null;

    const get = (obj, path) => path.split('.').reduce((o, k) => o?.[k], obj);
    const set = (obj, path, value) => {
        const keys = path.split('.');
        keys.slice(0, -1).reduce((o, k) => (o[k] ??= {}), obj)[keys.at(-1)] = value;
    };

    function render() {
        if (!state) return;
        for (const el of inputs) {
            const v = get(state, el.dataset.fault);
            if (el.type === 'checkbox') el.checked = !!v; else el.value = v ?? 0;
        }
        document.getElementById('errorRateLabel').textContent = `${state.api.errorRatePercent}%`;
        document.getElementById('slowTraceLabel').textContent = `${state.api.slowTraceMs} ms`;
        document.getElementById('slowQueryLabel').textContent = `${state.api.slowQueryMs} ms`;
        const pending = state.portal.pendingTrafficRequests;
        document.getElementById('pendingTraffic').textContent = pending > 0 ? `${pending} queued requests remaining` : '';
        const warn = document.getElementById('faultApiError');
        warn.classList.toggle('d-none', !state.apiError);
        warn.textContent = state.apiError ? `API unreachable: ${state.apiError}` : '';
        const a = state.api;
        const active = a.errorRatePercent > 0 || a.databaseErrors || a.slowTraces || a.slowQueries ||
            a.nPlusOneQueries || a.qaMismatches || a.productionStalled || state.portal.portalErrors;
        dot.className = 'fab-dot' + (active ? ' on' : '') + (state.portal.autoTraffic ? ' traffic' : '');
    }

    function addLog(text, kind = 'info') {
        const row = document.createElement('div');
        row.className = `log-row log-${kind}`;
        row.textContent = `${new Date().toLocaleTimeString()} — ${text}`;
        log.prepend(row);
        while (log.children.length > 8) log.lastChild.remove();
    }

    async function load() {
        try {
            state = await (await fetch('/fault/state')).json();
            render();
        } catch (e) { addLog(`Could not load fault state: ${e}`, 'bad'); }
    }

    async function save() {
        try {
            const res = await fetch('/fault/state', {
                method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(state)
            });
            state = await res.json();
            render();
            addLog('Fault settings saved', 'ok');
        } catch (e) { addLog(`Save failed: ${e}`, 'bad'); }
    }

    for (const el of inputs) {
        el.addEventListener(el.type === 'checkbox' ? 'change' : 'input', () => {
            if (!state) return;
            set(state, el.dataset.fault, el.type === 'checkbox' ? el.checked : Number(el.value));
            render();
            clearTimeout(saveTimer);
            saveTimer = setTimeout(save, el.type === 'checkbox' ? 0 : 350);
        });
    }

    for (const btn of panel.querySelectorAll('[data-trigger]')) {
        btn.addEventListener('click', async () => {
            const kind = btn.dataset.trigger;
            btn.disabled = true;
            const started = performance.now();
            addLog(`Triggering ${kind}…`);
            try {
                const res = await fetch(`/fault/trigger/${kind}`, { method: 'POST' });
                const ms = Math.round(performance.now() - started);
                let detail = '';
                try { const body = await res.json(); detail = body.error || body.message || ''; } catch { }
                addLog(`${kind}: HTTP ${res.status} in ${ms} ms ${detail}`.trim(), res.ok ? 'ok' : 'bad');
            } catch (e) { addLog(`${kind} failed: ${e}`, 'bad'); }
            btn.disabled = false;
            load();
        });
    }

    document.getElementById('faultReset').addEventListener('click', async () => {
        await fetch('/fault/reset', { method: 'POST' });
        addLog('All faults cleared', 'ok');
        load();
    });

    load();
    panel.addEventListener('shown.bs.offcanvas', load);
    setInterval(() => { if (panel.classList.contains('show')) load(); }, 5000);
})();
