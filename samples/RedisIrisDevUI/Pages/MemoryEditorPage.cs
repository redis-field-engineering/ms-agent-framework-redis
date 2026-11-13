// Copyright (c) Redis, Inc.

using System.Net;

namespace RedisIrisDevUI;

internal static class MemoryEditorPage
{
    public static string Render(string token, bool enabled) => $$"""
        <!doctype html><html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
        <meta name="csrf-token" content="{{WebUtility.HtmlEncode(token)}}"><title>Long-term memories · Redis Iris</title>
        <style>
        :root{font-family:system-ui,sans-serif;color:#172b38;background:#f3f6f8}body{max-width:1120px;margin:auto;padding:24px}
        a{color:#005d8b}nav{display:flex;gap:20px}h1{font-size:2rem}h2{font-size:1.2rem}main{display:grid;grid-template-columns:1fr 1fr;gap:24px}
        section,article{background:white;border:1px solid #cbd5dd;border-radius:10px;padding:20px;margin-bottom:16px}article{padding:14px}
        label{display:block;margin:14px 0 5px;font-weight:600}textarea,input{box-sizing:border-box;width:100%;padding:10px;border:1px solid #8495a1;border-radius:5px;font:inherit}
        textarea{min-height:130px;resize:vertical}button{padding:10px 16px;margin:10px 8px 0 0;border:1px solid #005d8b;border-radius:5px;background:#005d8b;color:white;font:inherit;cursor:pointer}
        button.secondary{background:white;color:#005d8b}button:disabled{opacity:.5;cursor:default}.meta{color:#4a6070;font-size:.85rem;overflow-wrap:anywhere}
        pre,.memory-text{white-space:pre-wrap;overflow-wrap:anywhere}#status{padding:14px;background:#e4eef4;border-radius:6px;min-height:24px}#status.error{background:#ffe7e3}
        [hidden]{display:none!important}summary{cursor:pointer}#comparison{border-left:4px solid #b56b00;padding:12px;background:#fff4db}input[type=checkbox]{width:auto}
        @media(max-width:760px){main{grid-template-columns:1fr}body{padding:16px} }
        </style><script src="/memory-editor.js" defer></script></head>
        <body data-enabled="{{enabled.ToString().ToLowerInvariant()}}"><nav><a href="/">Dashboard</a><a href="/devui">Chat</a></nav>
        <h1>Long-term memories</h1><p>Save a durable fact or correct a memory for your signed-in identity. Agent proposals are saved only when you select Save.</p>
        <p id="status" role="status" aria-live="polite">{{(enabled ? "Loading memories…" : "Set REDIS_IRIS_API_MODE=Iris to enable direct memory editing.")}}</p>
        <main><div><section><h2>Your memories</h2><form id="search"><label for="query">Search</label><input id="query" placeholder="Seat preference, food, travel…"><button>Search</button><button id="new" type="button" class="secondary">New memory</button></form>
        <div id="records"></div><button id="more" class="secondary" hidden>Load more</button></section>
        <section><h2>Agent proposals</h2><p>Review the exact text and tags before saving.</p><button id="refresh-proposals" class="secondary">Refresh proposals</button><div id="proposals"></div></section></div>
        <section><h2 id="editor-title">New memory</h2><form id="editor"><label for="text">Memory text</label><textarea id="text" required maxlength="50000"></textarea>
        <label for="topics">Topics (comma separated)</label><input id="topics" placeholder="travel, preferences">
        <label for="type">Memory type</label><input id="type" value="semantic" maxlength="64">
        <details><summary>Custom attributes</summary><p>For a registered custom type, enter a JSON object. On edit this replaces attributes while retaining other fields. Leave blank to keep them unchanged.</p><label for="attributes">Attributes JSON</label><textarea id="attributes" spellcheck="false" placeholder='{"seat":"window"}'></textarea></details>
        <p id="record-meta" class="meta"></p><div id="comparison" hidden><strong>Current stored memory</strong><pre id="current-record"></pre><label><input id="reviewed" type="checkbox"> I reviewed the current record and want to apply my draft to this revision.</label></div>
        <button id="save" type="submit">Save memory</button><button id="reload" type="button" class="secondary" hidden>Compare with latest</button></form></section></main></body></html>
        """;
}
