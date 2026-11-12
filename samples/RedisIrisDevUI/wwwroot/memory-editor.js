'use strict';
(() => {
  const el = id => document.getElementById(id);
  const api = '/api/ram/memory-editor';
  const csrf = document.querySelector('meta[name="csrf-token"]').content;
  let selected = null, pending = null, cursor = null, conflict = false, latest = null, saving = false;
  function status(message, error = false) { el('status').textContent = message; el('status').className = error ? 'error' : ''; }
  async function request(path, body) {
    const response = await fetch(api + path, body === undefined ? { cache: 'no-store' } : {
      method: 'POST', headers: { 'Content-Type': 'application/json', 'X-CSRF-TOKEN': csrf }, body: JSON.stringify(body)
    });
    const data = await response.json();
    if (!response.ok) { const error = new Error(data.error || 'Memory operation failed.'); error.status = response.status; throw error; }
    return data;
  }
  function textNode(tag, text, className) { const node = document.createElement(tag); node.textContent = text; if (className) node.className = className; return node; }
  function button(text, action) { const node = textNode('button', text); node.type = 'button'; node.addEventListener('click', action); return node; }
  function report(error) { status(error.message, true); }
  async function list(append = false) {
    const data = await request('/memories?query=' + encodeURIComponent(el('query').value) + (append && cursor ? '&cursor=' + encodeURIComponent(cursor) : ''));
    if (!append) el('records').replaceChildren();
    for (const memory of data.items) {
      const card = document.createElement('article');
      card.append(textNode('p', memory.text, 'memory-text'), textNode('p', `${memory.memoryType || 'semantic'} · ${(memory.topics || []).join(', ')} · ${new Date(memory.updatedAt).toLocaleString()}`, 'meta'));
      card.append(button(memory.documentManaged ? 'View (read-only)' : 'Edit', () => open(memory.id).catch(report)));
      el('records').append(card);
    }
    if (!append && data.items.length === 0) el('records').append(textNode('p', 'No memories found.'));
    cursor = data.nextPageToken; el('more').hidden = !cursor;
  }
  function reset() {
    if (saving) return;
    selected = pending = latest = null; conflict = false;
    el('editor').reset(); el('type').value = 'semantic'; el('type').disabled = false;
    el('editor-title').textContent = 'New memory'; el('record-meta').textContent = '';
    el('comparison').hidden = el('reload').hidden = true; el('save').disabled = false;
  }
  async function open(id) {
    if (saving) return;
    const record = await request('/memories/' + encodeURIComponent(id));
    reset(); selected = record;
    el('editor-title').textContent = 'Edit memory'; el('text').value = record.text;
    el('topics').value = (record.topics || []).join(', '); el('type').value = record.memoryType || 'semantic'; el('type').disabled = true;
    el('attributes').value = record.attributes ? JSON.stringify(record.attributes, null, 2) : '';
    el('record-meta').textContent = `ID: ${record.id} · Revision: ${record.revision || 'unavailable'}`;
    el('reload').hidden = false; el('save').disabled = record.documentManaged || !record.revision;
    status(record.documentManaged ? 'This document-managed memory is read-only.' : !record.revision ? 'This server did not supply a revision; safe editing is unavailable.' : 'Edit the fields, then save.');
  }
  async function proposals() {
    const items = await request('/proposals'); el('proposals').replaceChildren();
    for (const proposal of items) {
      const card = document.createElement('article');
      card.append(textNode('strong', proposal.kind + ' memory'), textNode('p', proposal.text, 'memory-text'), textNode('p', proposal.topics.join(', '), 'meta'));
      if (proposal.attributes) card.append(textNode('pre', JSON.stringify(proposal.attributes, null, 2)));
      const save = button('Save proposal', async () => {
        save.disabled = true;
        try { await persist(proposal.id); await proposals(); } catch (error) { report(error); } finally { save.disabled = false; }
      });
      card.append(save); el('proposals').append(card);
    }
    if (!items.length) el('proposals').append(textNode('p', 'No pending proposals.'));
  }
  async function persist(id) {
    const result = await request('/proposals/' + encodeURIComponent(id) + '/save', {});
    status(result.refreshPending ? 'Saved. Refresh to load the new record.' : 'Memory saved.');
    try { await list(); } catch { status('Saved. The list could not refresh; try Search again.'); }
    return result;
  }
  el('editor').addEventListener('input', () => { pending = null; });
  el('editor').addEventListener('submit', async event => {
    event.preventDefault(); if (saving) return; saving = true; el('editor').inert = true; el('new').disabled = true; el('save').disabled = true;
    try {
      if (conflict) {
        if (!latest || !el('reviewed').checked) throw new Error('Compare with the latest memory and review it before saving.');
        selected = latest; pending = null;
      }
      if (!pending) {
        const raw = el('attributes').value.trim(); const attributes = raw ? JSON.parse(raw) : undefined;
        if (attributes !== undefined && (!attributes || typeof attributes !== 'object' || Array.isArray(attributes))) throw new Error('Attributes must be a JSON object.');
        // Avoid an unnecessary PUT when custom attributes have not changed.
        const unchanged = selected && JSON.stringify(attributes) === JSON.stringify(selected.attributes);
        pending = await request('/proposals', { text: el('text').value, topics: el('topics').value.split(',').map(t => t.trim()).filter(Boolean),
          memoryId: selected?.id, expectedRevision: selected?.revision, memoryType: el('type').value,
          attributes: unchanged ? undefined : attributes });
      }
      const result = await persist(pending.id); pending = null; conflict = false; el('comparison').hidden = true;
      if (result.memory) { selected = result.memory; el('editor-title').textContent = 'Edit memory'; el('type').disabled = true; el('reload').hidden = false; el('record-meta').textContent = `ID: ${selected.id} · Revision: ${selected.revision || 'unavailable'}`; }
      else { saving = false; reset(); }
      await proposals().catch(() => {});
    } catch (error) {
      if (error.status === 412) { conflict = true; latest = null; pending = null; el('reviewed').checked = false; el('reload').hidden = false; }
      report(error);
    } finally { saving = false; el('editor').inert = false; el('new').disabled = false; el('save').disabled = !!selected?.documentManaged || (!!selected && !selected.revision); }
  });
  el('reload').addEventListener('click', async () => {
    if (!selected) return;
    try {
      latest = await request('/memories/' + encodeURIComponent(selected.id)); conflict = true; pending = null;
      el('current-record').textContent = JSON.stringify({ text: latest.text, topics: latest.topics, attributes: latest.attributes, revision: latest.revision }, null, 2);
      el('comparison').hidden = false; el('reviewed').checked = false;
      el('save').disabled = !!latest.documentManaged || !latest.revision;
      status('Your draft is preserved. Review the current record before applying it.');
    } catch (error) { report(error); }
  });
  el('search').addEventListener('submit', event => { event.preventDefault(); list().catch(report); });
  el('new').addEventListener('click', reset);
  el('more').addEventListener('click', () => list(true).catch(report));
  el('refresh-proposals').addEventListener('click', () => proposals().catch(report));
  if (document.body.dataset.enabled === 'true') Promise.all([list(), proposals()]).then(() => status('Ready.')).catch(report);
  else document.querySelectorAll('button').forEach(node => { node.disabled = true; });
})();
