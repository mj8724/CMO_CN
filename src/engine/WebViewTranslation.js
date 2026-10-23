(function () {
    'use strict';
    // One translator per document. Repeated native discovery does not rescan it.
    if (window.__cmoWebTranslation || window !== window.top) return;
    var fileRoot = __CMO_WEB_FILE_ROOT__;
    var url = new URL(location.href);
    // URL canonicalizes shortened/numeric IPv4 and IPv6 spellings first.
    // A DNS name merely beginning with "127." is not a loopback address.
    var host = url.hostname.toLowerCase();
    var ipv4 = host.split('.');
    var loopback = host === 'localhost' || host === 'loopback' || host === '[::1]' ||
        host === '[::ffff:7f00:1]' || host === '[::7f00:1]' ||
        (ipv4.length === 4 && ipv4[0] === '127' && ipv4.every(function (part) {
            return /^\d{1,3}$/.test(part) && Number(part) <= 255;
        }));
    var trusted = (url.protocol === 'about:' && (url.pathname === 'blank' || url.pathname === 'srcdoc')) ||
        (url.protocol === 'file:' && fileRoot && location.href.toLowerCase().indexOf(fileRoot.toLowerCase()) === 0) ||
        (/^https?:$/.test(url.protocol) && loopback);
    if (!trusted) return;
    var labels = __CMO_WEB_LABELS__;
    var hostEnabled = __CMO_WEB_MESSAGES_ENABLED__;
    var fallback = !hostEnabled;
    var documentId = Date.now().toString(36) + '-' + Math.random().toString(36).slice(2);
    var states = new WeakMap();
    var nodeRequests = new WeakMap();
    var pending = new Map();
    var bySource = new Map();
    var cache = new Map();
    var queued = new Map();
    var scheduled = false;
    // One document cursor, not a second queue of rejected nodes/subtrees.
    var continuation = null;
    var continuationNode = null;
    var continuationAgain = false;
    var continuationScheduled = false;
    var nextId = 0;
    var stats = { scans: 0, sent: 0, batches: 0, pending: 0, applied: 0, continuationSlices: 0 };
    var skipped = /^(SCRIPT|STYLE|NOSCRIPT|INPUT|TEXTAREA|SELECT|OPTION)$/;

    function release(request) {
        if (pending.get(request.id) !== request) return;
        pending.delete(request.id);
        if (bySource.get(request.source) === request) bySource.delete(request.source);
        queued.delete(request.id);
        request.nodes.forEach(function (node) {
            if (nodeRequests.get(node) === request) nodeRequests.delete(node);
        });
        stats.pending = pending.size;
        scheduleContinuation();
    }

    function forget(request, node) {
        // Cancellation must not erase a newer value already processed on this node.
        if (states.get(node) === request.source) states.delete(node);
        if (nodeRequests.get(node) === request) nodeRequests.delete(node);
        request.nodes.delete(node);
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
        while (queued.size) {
            var lines = ['CMO_WEB_V1\t' + documentId];
            var ids = [];
            var requests = [];
            var size = lines[0].length;
            while (queued.size && ids.length < 64) {
                var request = queued.values().next().value;
                if (!pending.has(request.id)) { queued.delete(request.id); continue; }
                var line;
                try { line = request.id + '\t' + encodeURIComponent(request.source); }
                catch (error) {
                    // A truncated surrogate is valid DOM text but not URI input.
                    // Keep it unchanged without blocking healthy text behind it.
                    release(request);
                    request.nodes.clear();
                    continue;
                }
                if (ids.length && size + line.length + 1 > 262144) break;
                queued.delete(request.id);
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
                    if (unanswered) {
                        unanswered.nodes.forEach(function (node) { forget(unanswered, node); });
                        release(unanswered);
                    }
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
                batch.requests.forEach(function (request) {
                    if (pending.has(request.id)) { request.queued = true; queued.set(request.id, request); }
                });
                return;
            }
            sent(batch);
        }
    }

    function translate(node) {
        // One live request per node. Updating into a label/cache/editable value also
        // releases its obsolete membership; a shared peer keeps its request alive.
        var previous = nodeRequests.get(node);
        var source = node.nodeValue;
        if (previous && (previous.source !== source || !node.isConnected ||
            node.ownerDocument !== document || editable(node))) {
            forget(previous, node);
            if (!previous.nodes.size) release(previous);
        }
        // Removal records can still deliver edits made after a subtree left this document.
        if (!node.isConnected || node.ownerDocument !== document) return true;
        if (!source || states.get(node) === source || editable(node)) return true;
        var key = source.trim();
        if (!key || !/[A-Za-z]/.test(key) || source.length > 16384) {
            states.set(node, source);
            return true;
        }
        if (Object.prototype.hasOwnProperty.call(labels, key)) {
            var offset = source.indexOf(key);
            apply(node, source, source.slice(0, offset) + labels[key] + source.slice(offset + key.length));
            return true;
        }
        if (cache.has(source)) {
            apply(node, source, cache.get(source));
            return true;
        }
        var request = bySource.get(source);
        if (!request) {
            // A refused node is NOT processed. The bounded cursor revisits it when
            // a reply/cancellation frees capacity, even without another mutation.
            if (pending.size >= 2048) return false;
            request = { id: ++nextId, source: source, nodes: new Set(), queued: true };
            bySource.set(source, request);
            pending.set(request.id, request);
            queued.set(request.id, request);
            stats.pending = pending.size;
        }
        states.set(node, source);
        request.nodes.add(node);
        nodeRequests.set(node, request);
        if (!scheduled) {
            scheduled = true;
            queueMicrotask(flush);
        }
        return true;
    }

    function walker(root) {
        return document.createTreeWalker(root, NodeFilter.SHOW_ELEMENT | NodeFilter.SHOW_TEXT, {
            acceptNode: function (node) {
                if (node.nodeType === Node.ELEMENT_NODE) {
                    return skipped.test(node.tagName) || node.isContentEditable ? NodeFilter.FILTER_REJECT : NodeFilter.FILTER_SKIP;
                }
                return NodeFilter.FILTER_ACCEPT;
            }
        });
    }

    function defer(node) {
        if (!continuation) {
            continuation = walker(document);
            continuation.currentNode = node;
            continuationNode = node;
        } else {
            // A newly changed subtree can be behind the cursor. At most one
            // further pass is requested; no list of overflow roots is retained.
            continuationAgain = true;
        }
        scheduleContinuation();
    }

    function scheduleContinuation() {
        if (!continuation || continuationScheduled || pending.size >= 2048) return;
        continuationScheduled = true;
        setTimeout(continueDeferred, 0);
    }

    function continueDeferred() {
        continuationScheduled = false;
        if (!continuation || pending.size >= 2048) return;
        stats.continuationSlices++;
        var visited = 0;
        while (visited++ < 256) {
            var node = continuationNode || continuation.nextNode();
            continuationNode = null;
            if (!node) {
                if (continuationAgain) {
                    continuation = walker(document);
                    continuationAgain = false;
                } else {
                    continuation = null;
                    return;
                }
            } else if (!translate(node)) {
                continuationNode = node;
                return; // Resumed only when live work releases capacity.
            }
        }
        scheduleContinuation();
    }

    function scan(root) {
        stats.scans++;
        if (root.nodeType === Node.TEXT_NODE) {
            if (!translate(root)) defer(root);
            return;
        }
        if (root.nodeType === Node.ELEMENT_NODE && (skipped.test(root.tagName) || root.isContentEditable)) return;
        var walk = walker(root);
        var node;
        while ((node = walk.nextNode())) {
            if (!translate(node)) { defer(node); return; }
        }
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
                    if (!request.queued) { request.queued = true; queued.set(request.id, request); }
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
            if (record.type === 'characterData') {
                if (!translate(record.target)) defer(record.target);
            }
            else {
                removed = removed || record.removedNodes.length > 0;
                for (var j = 0; j < record.addedNodes.length; j++) roots.add(record.addedNodes[j]);
            }
        }
        if (removed) {
            // A cursor in a detached subtree cannot reach its following siblings.
            if (continuation) continuationAgain = true;
            pending.forEach(function (request) {
                request.nodes.forEach(function (node) {
                    if (!node.isConnected || node.ownerDocument !== document ||
                        node.nodeValue !== request.source || editable(node)) forget(request, node);
                });
                if (!request.nodes.size) release(request);
            });
            scheduleContinuation();
        }
        roots.forEach(function (node) { if (node.isConnected) scan(node); });
    });
    observer.observe(document, { childList: true, characterData: true, subtree: true });
    scan(document);
}());
