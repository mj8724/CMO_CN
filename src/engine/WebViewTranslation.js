(function () {
    'use strict';
    // One translator per document. Repeated native discovery does not rescan it.
    if (window.__cmoWebTranslation || window !== window.top) return;
    var fileRoot = __CMO_WEB_FILE_ROOT__;
    var url = new URL(location.href);
    var trusted = /^about:(blank|srcdoc)(#.*)?$/.test(location.href) ||
        (url.protocol === 'file:' && fileRoot && location.href.toLowerCase().indexOf(fileRoot.toLowerCase()) === 0) ||
        (/^https?:$/.test(url.protocol) && (/^127\./.test(url.hostname) || url.hostname === 'localhost' || url.hostname === '[::1]'));
    if (!trusted) return;
    var labels = __CMO_WEB_LABELS__;
    var hostEnabled = __CMO_WEB_MESSAGES_ENABLED__;
    var fallback = !hostEnabled;
    var documentId = Date.now().toString(36) + '-' + Math.random().toString(36).slice(2);
    var states = new WeakMap();
    var pending = new Map();
    var bySource = new Map();
    var cache = new Map();
    var queued = [];
    var scheduled = false;
    var nextId = 0;
    var stats = { scans: 0, sent: 0, batches: 0, pending: 0, applied: 0 };
    var skipped = /^(SCRIPT|STYLE|NOSCRIPT|INPUT|TEXTAREA|SELECT|OPTION)$/;

    function release(request) {
        pending.delete(request.id);
        bySource.delete(request.source);
        stats.pending = pending.size;
    }

    function editable(node) {
        var parent = node.parentElement;
        return !parent || parent.isContentEditable || !!parent.closest('input,textarea,select,script,style,noscript');
    }

    function apply(node, source, target) {
        // A document nonce alone does not protect reused or user-edited text nodes.
        if (!node.isConnected || node.ownerDocument !== document || node.nodeValue !== source || editable(node)) return;
        states.set(node, target);
        if (target !== source) {
            node.nodeValue = target;
            stats.applied++;
        }
    }

    function nextBatch() {
        while (queued.length) {
            var lines = ['CMO_WEB_V1\t' + documentId];
            var ids = [];
            var requests = [];
            var size = lines[0].length;
            while (queued.length && ids.length < 64) {
                var request = queued[0];
                if (!pending.has(request.id)) { queued.shift(); continue; }
                var line;
                try { line = request.id + '\t' + encodeURIComponent(request.source); }
                catch (error) {
                    // A truncated surrogate is valid DOM text but not URI input.
                    // Keep it unchanged without blocking healthy text behind it.
                    queued.shift();
                    release(request);
                    request.nodes.clear();
                    continue;
                }
                if (ids.length && size + line.length + 1 > 262144) break;
                queued.shift();
                request.queued = false;
                lines.push(line);
                ids.push(request.id);
                requests.push(request);
                size += line.length + 1;
            }
            if (!ids.length) continue;
            return { message: lines.join('\n'), ids: ids, requests: requests };
        }
        return null;
    }

    function sent(batch) {
        stats.sent += batch.ids.length;
        stats.batches++;
        // Keep only IDs in the timer: successful replies release DOM references.
        (function (ids) {
            setTimeout(function () {
                ids.forEach(function (id) {
                    var unanswered = pending.get(id);
                    if (unanswered) release(unanswered);
                });
            }, 15000);
        }(batch.ids));
    }

    function flush() {
        scheduled = false;
        if (fallback || !hostEnabled) return;
        if (!window.chrome || !window.chrome.webview || !window.chrome.webview.postMessage) {
            hostEnabled = false;
            return;
        }
        var batch;
        while ((batch = nextBatch())) {
            try { window.chrome.webview.postMessage(batch.message); }
            catch (error) {
                hostEnabled = false;
                batch.requests.forEach(function (request) { request.queued = true; queued.push(request); });
                return;
            }
            sent(batch);
        }
    }

    function translate(node) {
        var source = node.nodeValue;
        if (!source || states.get(node) === source || editable(node)) return;
        states.set(node, source);
        var key = source.trim();
        if (!key || !/[A-Za-z]/.test(key)) return;
        if (Object.prototype.hasOwnProperty.call(labels, key)) {
            var offset = source.indexOf(key);
            apply(node, source, source.slice(0, offset) + labels[key] + source.slice(offset + key.length));
            return;
        }
        if (cache.has(source)) {
            apply(node, source, cache.get(source));
            return;
        }
        // Oversized text and overload remain unchanged rather than retaining an unbounded queue.
        if (source.length > 16384) return;
        var request = bySource.get(source);
        if (!request) {
            if (pending.size >= 2048) return;
            request = { id: ++nextId, source: source, nodes: new Set(), queued: true };
            bySource.set(source, request);
            pending.set(request.id, request);
            queued.push(request);
            stats.pending = pending.size;
        }
        request.nodes.add(node);
        if (!scheduled) {
            scheduled = true;
            queueMicrotask(flush);
        }
    }

    function scan(root) {
        stats.scans++;
        if (root.nodeType === Node.TEXT_NODE) { translate(root); return; }
        if (root.nodeType === Node.ELEMENT_NODE && (skipped.test(root.tagName) || root.isContentEditable)) return;
        var walker = document.createTreeWalker(root, NodeFilter.SHOW_ELEMENT | NodeFilter.SHOW_TEXT, {
            acceptNode: function (node) {
                if (node.nodeType === Node.ELEMENT_NODE) {
                    return skipped.test(node.tagName) || node.isContentEditable ? NodeFilter.FILTER_REJECT : NodeFilter.FILTER_SKIP;
                }
                return NodeFilter.FILTER_ACCEPT;
            }
        });
        var node;
        while ((node = walker.nextNode())) translate(node);
    }

    window.__cmoWebTranslation = {
        documentId: documentId,
        stats: stats,
        take: function () {
            if (!fallback) {
                fallback = true;
                hostEnabled = false;
                // Messaging may have been disabled after a successful post.
                // Recover outstanding IDs once; late duplicate replies remain harmless.
                pending.forEach(function (request) {
                    if (!request.queued) { request.queued = true; queued.push(request); }
                });
            }
            var batch = nextBatch();
            if (!batch) return null;
            sent(batch);
            return encodeURIComponent(batch.message);
        },
        receive: function (nonce, updates) {
            if (nonce !== documentId) return;
            for (var i = 0; i < updates.length; i++) {
                var update = updates[i];
                var request = pending.get(update[0]);
                if (!request || request.source !== update[1]) continue;
                release(request);
                cache.set(request.source, update[2]);
                if (cache.size > 2048) cache.delete(cache.keys().next().value);
                request.nodes.forEach(function (node) { apply(node, request.source, update[2]); });
            }
            stats.pending = pending.size;
        }
    };
    var observer = new MutationObserver(function (records) {
        // Nested parser mutations can name the same subtree more than once.
        var roots = new Set();
        var removed = false;
        for (var i = 0; i < records.length; i++) {
            var record = records[i];
            if (record.type === 'characterData') translate(record.target);
            else {
                removed = removed || record.removedNodes.length > 0;
                for (var j = 0; j < record.addedNodes.length; j++) roots.add(record.addedNodes[j]);
            }
        }
        if (removed) pending.forEach(function (request) {
            request.nodes.forEach(function (node) { if (!node.isConnected) request.nodes.delete(node); });
            if (!request.nodes.size) release(request);
        });
        roots.forEach(function (node) { if (node.isConnected) scan(node); });
    });
    observer.observe(document, { childList: true, characterData: true, subtree: true });
    scan(document);
}());
