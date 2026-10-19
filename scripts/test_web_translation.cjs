#!/usr/bin/env node
// Real Edge DOM tests; only the native WebView2 message transport is substituted.
// Requires playwright (NODE_PATH may point at the installed package directory).
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const http = require('node:http');
const { chromium } = require('playwright');

const root = path.resolve(__dirname, '..');
const sourcePath = path.join(root, 'src/engine/WebViewTranslation.js');
const source = fs.existsSync(sourcePath) ? fs.readFileSync(sourcePath, 'utf8') : '';
const bootstrap = source.replace('__CMO_WEB_LABELS__', JSON.stringify({
    SITUATION: '战场态势', MISSION: '任务', Category: '装备大类'
})).replace('__CMO_WEB_FILE_ROOT__', '""').replace('__CMO_WEB_MESSAGES_ENABLED__', 'true');
const fallbackBootstrap = bootstrap.replace('var hostEnabled = true;', 'var hostEnabled = false;');
const translations = new Map([
    ['A detailed description.', '一段详细说明。'],
    ['Another detailed description.', '另一段详细说明。'],
    ['Delayed old description.', '迟到的旧说明。'],
    ['Navigation delayed description.', '导航前的旧说明。'],
    ['A navigation-only description.', '只用于导航测试的说明。']
]);
const requestLog = [];
let responseDelay = 0;

async function main() {
    const server = http.createServer((req, res) => {
        res.writeHead(200, { 'Content-Type': 'text/html; charset=utf-8' });
        res.end('<!doctype html><html><head></head><body>' +
            '<h1 id="heading">SITUATION</h1><p id="description">A detailed description.</p>' +
            '<input id="input" value="MISSION"><textarea id="textarea">MISSION</textarea>' +
            '<div id="edit" contenteditable="true"><span>MISSION</span></div>' +
            '<script>window.firstFrame = new Promise(resolve => requestAnimationFrame(() => ' +
            'resolve(document.getElementById("heading").textContent)));</script></body></html>');
    });
    await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
    const url = `http://127.0.0.1:${server.address().port}/`;
    const browser = await chromium.launch({ channel: 'msedge', headless: true });
    const context = await browser.newContext();
    const errors = [];
    await context.exposeBinding('__cmoHostMessage', async ({ page }, message) => {
        // Same accepted message size as the real reflection adapter.
        if (message.length > 524288) return;
        const lines = message.split('\n');
        const header = lines.shift().split('\t');
        if (header[0] !== 'CMO_WEB_V1') return;
        const nonce = header[1];
        const delay = responseDelay;
        const updates = lines.filter(Boolean).map(line => {
            const tab = line.indexOf('\t');
            const id = Number(line.slice(0, tab));
            const original = decodeURIComponent(line.slice(tab + 1));
            requestLog.push(original);
            return [id, original, translations.get(original.trim()) ||
                (original.startsWith('Unique description ') ? '独特说明 ' + original.slice(19) : original)];
        });
        if (delay) await new Promise(resolve => setTimeout(resolve, delay));
        try {
            await page.evaluate(({ nonce, updates }) => {
                if (window.__cmoWebTranslation) window.__cmoWebTranslation.receive(nonce, updates);
            }, { nonce, updates });
        } catch (error) {
            if (!/Execution context was destroyed|Target page.*closed/.test(error.message)) throw error;
        }
    });
    await context.addInitScript(() => {
        window.chrome = window.chrome || {};
        window.chrome.webview = { postMessage: message => window.__cmoHostMessage(message) };
    });
    await context.addInitScript({ content: bootstrap });
    const page = await context.newPage();
    page.setDefaultTimeout(10000);
    page.on('pageerror', error => errors.push(error.message));
    const passed = [];
    async function test(name, body) {
        await body();
        passed.push(name);
        console.log('PASS ' + name);
    }
    try {
        await page.goto(url);
        await test('known label is Chinese at first animation frame', async () => {
            assert.equal(await page.evaluate(() => window.firstFrame), '战场态势');
        });
        await test('complex text uses the host translation', async () => {
            await page.waitForFunction(() => document.querySelector('#description').textContent === '一段详细说明。');
        });
        await test('editable text and user input remain unchanged', async () => {
            assert.deepEqual(await page.evaluate(() => [document.querySelector('#input').value,
                document.querySelector('#textarea').value, document.querySelector('#edit').textContent]),
            ['MISSION', 'MISSION', 'MISSION']);
        });
        await test('reused text node is translated after the game writes new English', async () => {
            await page.evaluate(() => { document.querySelector('#description').firstChild.nodeValue = 'Another detailed description.'; });
            await page.waitForFunction(() => document.querySelector('#description').textContent === '另一段详细说明。');
        });
        await test('in-flight response does not overwrite newer text or detached nodes', async () => {
            responseDelay = 120;
            await page.evaluate(() => { document.querySelector('#description').firstChild.nodeValue = 'Navigation delayed description.'; });
            await page.waitForFunction(() => window.__cmoWebTranslation.stats.sent > 2);
            await page.evaluate(() => {
                const node = document.querySelector('#description').firstChild;
                node.nodeValue = 'A detailed description.';
                const removed = document.createElement('p');
                removed.textContent = 'Delayed old description.';
                document.body.append(removed);
                removed.remove();
            });
            await page.waitForTimeout(220);
            assert.equal(await page.locator('#description').textContent(), '一段详细说明。');
            responseDelay = 0;
        });
        await test('a node made editable while a request is in flight is preserved', async () => {
            responseDelay = 100;
            await page.evaluate(() => {
                const p = document.createElement('p'); p.id = 'new-editable';
                p.textContent = 'Delayed old description.'; document.body.append(p);
            });
            await page.waitForFunction(() => window.__cmoWebTranslation.stats.pending > 0);
            await page.evaluate(() => { document.querySelector('#new-editable').contentEditable = 'true'; });
            await page.waitForTimeout(160);
            assert.equal(await page.locator('#new-editable').textContent(), 'Delayed old description.');
            responseDelay = 0;
        });
        await test('late response from previous document cannot change the next navigation', async () => {
            responseDelay = 140;
            const oldCount = requestLog.length;
            await page.evaluate(() => { document.querySelector('#description').firstChild.nodeValue = 'A navigation-only description.'; });
            await page.waitForFunction(() => window.__cmoWebTranslation.stats.pending > 0);
            await page.goto(url + 'next');
            await page.waitForFunction(() => document.querySelector('#description').textContent === '一段详细说明。');
            await page.waitForTimeout(180);
            assert.equal(await page.locator('#description').textContent(), '一段详细说明。');
            assert.ok(requestLog.length > oldCount);
            responseDelay = 0;
        });
        await test('unchanged unknown text and translated text are not repeatedly sent', async () => {
            await page.evaluate(() => {
                const p = document.createElement('p'); p.id = 'unknown'; p.textContent = 'Unmapped proper noun'; document.body.append(p);
            });
            await page.waitForTimeout(80);
            const before = requestLog.length;
            await page.evaluate(bootstrap);
            await page.waitForTimeout(80);
            assert.equal(requestLog.length, before);
            assert.equal(await page.locator('#unknown').textContent(), 'Unmapped proper noun');
        });
        await test('long paragraphs are split into bounded transport batches', async () => {
            const batches = await page.evaluate(() => window.__cmoWebTranslation.stats.batches);
            await page.evaluate(() => {
                for (let i = 0; i < 64; i++) {
                    const p = document.createElement('p'); p.className = 'long';
                    p.textContent = 'Unique description long-' + i + ' ' + 'x'.repeat(10000); document.body.append(p);
                }
            });
            await page.waitForFunction(() => Array.from(document.querySelectorAll('.long')).every(p => p.textContent.startsWith('独特说明 ')));
            assert.ok(await page.evaluate(before => window.__cmoWebTranslation.stats.batches > before + 1, batches));
        });
        await test('oversized single nodes stay unchanged without retaining a pending request', async () => {
            const sent = requestLog.length;
            await page.evaluate(() => {
                const p = document.createElement('p'); p.id = 'oversized';
                p.textContent = 'Unique description ' + 'x'.repeat(524289); document.body.append(p);
            });
            await page.waitForTimeout(40);
            assert.equal(requestLog.length, sent);
            assert.equal(await page.evaluate(() => window.__cmoWebTranslation.stats.pending), 0);
            assert.ok((await page.locator('#oversized').textContent()).startsWith('Unique description '));
        });
        await test('mixed Chinese and English paragraphs obey encoded batch size limits', async () => {
            await page.evaluate(() => {
                for (let i = 0; i < 64; i++) {
                    const p = document.createElement('p'); p.className = 'mixed';
                    p.textContent = 'Unique description mixed-' + i + '中文 English '.repeat(900); document.body.append(p);
                }
            });
            await page.waitForFunction(() => Array.from(document.querySelectorAll('.mixed')).every(p => p.textContent.startsWith('独特说明 ')));
        });
        await test('1000 mutations are batched without rescanning the unchanged page', async () => {
            const before = await page.evaluate(() => ({ ...window.__cmoWebTranslation.stats }));
            const result = await page.evaluate(async () => {
                const start = performance.now();
                for (let i = 0; i < 1000; i++) {
                    const p = document.createElement('p'); p.className = 'bulk';
                    p.textContent = 'Unique description ' + i; document.body.append(p);
                }
                while (Array.from(document.querySelectorAll('.bulk')).some(p => !p.textContent.startsWith('独特说明 '))) {
                    if (performance.now() - start > 10000) throw Error('bulk translation timed out');
                    await new Promise(resolve => setTimeout(resolve, 5));
                }
                return { milliseconds: performance.now() - start, stats: { ...window.__cmoWebTranslation.stats } };
            });
            assert.equal(result.stats.sent - before.sent, 1000);
            assert.ok(result.stats.batches - before.batches <= 20);
            assert.ok(result.stats.scans - before.scans <= 1005);
            console.log('BENCHMARK ' + JSON.stringify({ nodes: 1000, milliseconds: result.milliseconds,
                batches: result.stats.batches - before.batches, scannedRoots: result.stats.scans - before.scans,
                bootstrapBytes: Buffer.byteLength(bootstrap) }));
        });
        await test('outstanding work is bounded and detached nodes release their pending requests', async () => {
            responseDelay = 200;
            const pending = await page.evaluate(async () => {
                const group = document.createElement('section'); group.id = 'pending-group';
                for (let i = 0; i < 2500; i++) {
                    const p = document.createElement('p'); p.textContent = 'Unique description pending-' + i; group.append(p);
                }
                document.body.append(group);
                await Promise.resolve();
                return window.__cmoWebTranslation.stats.pending;
            });
            assert.ok(pending > 0 && pending <= 2048);
            await page.evaluate(() => document.querySelector('#pending-group').remove());
            await page.waitForFunction(() => window.__cmoWebTranslation.stats.pending === 0);
            responseDelay = 0;
            await page.waitForTimeout(240);
        });
        await test('external pages do not install a translator or access the native bridge', async () => {
            await context.route('https://outside.invalid/**', route => route.fulfill({
                contentType: 'text/html', body: '<h1>SITUATION</h1><p>A detailed description.</p>'
            }));
            const external = await context.newPage();
            const count = requestLog.length;
            await external.goto('https://outside.invalid/');
            assert.equal(await external.locator('h1').textContent(), 'SITUATION');
            assert.equal(await external.evaluate(() => typeof window.__cmoWebTranslation), 'undefined');
            assert.equal(requestLog.length, count);
            await external.close();
        });
        await test('malformed UTF-16 cannot poison the message queue', async () => {
            await page.evaluate(() => {
                const bad = document.createElement('p'); bad.id = 'bad-utf16';
                bad.textContent = 'Malformed ' + String.fromCharCode(0xd800) + ' paragraph';
                const good = document.createElement('p'); good.id = 'after-bad-utf16';
                good.textContent = 'Unique description after-malformed';
                document.body.append(bad, good);
            });
            await page.waitForFunction(() => document.querySelector('#after-bad-utf16').textContent.startsWith('独特说明 '));
            assert.equal(await page.evaluate(() => window.__cmoWebTranslation.stats.pending), 0);
            assert.ok((await page.locator('#bad-utf16').textContent()).startsWith('Malformed '));
        });
        const fallbackContext = await browser.newContext();
        await fallbackContext.addInitScript({ content: fallbackBootstrap });
        const fallbackPage = await fallbackContext.newPage();
        fallbackPage.on('pageerror', error => errors.push(error.message));
        const take = () => fallbackPage.evaluate(() => window.__cmoWebTranslation.take());
        const parse = encoded => {
            const lines = decodeURIComponent(encoded).split('\n');
            const nonce = lines.shift().split('\t')[1];
            const updates = lines.map(line => {
                const tab = line.indexOf('\t');
                const original = decodeURIComponent(line.slice(tab + 1));
                return [Number(line.slice(0, tab)), original, translations.get(original.trim()) || original];
            });
            return { nonce, updates };
        };
        const receive = reply => fallbackPage.evaluate(({ nonce, updates }) => {
            window.__cmoWebTranslation.receive(nonce, updates);
        }, reply);
        try {
            await fallbackPage.goto(url);
            await test('fallback extracts complex text without a web message bridge', async () => {
                assert.equal(await fallbackPage.evaluate(() => typeof window.__cmoWebTranslation.take), 'function');
                await receive(parse(await take()));
                await fallbackPage.waitForFunction(() => document.querySelector('#description').textContent === '一段详细说明。');
                assert.equal(await fallbackPage.evaluate(() => window.__cmoWebTranslation.stats.pending), 0);
            });
            await test('fallback is incremental and protects reused nodes and input', async () => {
                const scans = await fallbackPage.evaluate(() => window.__cmoWebTranslation.stats.scans);
                assert.equal(await take(), null);
                assert.equal(await take(), null);
                assert.equal(await fallbackPage.evaluate(() => window.__cmoWebTranslation.stats.scans), scans);
                await fallbackPage.evaluate(() => { document.querySelector('#description').firstChild.nodeValue = 'Delayed old description.'; });
                const old = parse(await take());
                await fallbackPage.evaluate(() => { document.querySelector('#description').firstChild.nodeValue = 'Another detailed description.'; });
                await receive(old);
                assert.equal(await fallbackPage.locator('#description').textContent(), 'Another detailed description.');
                await receive(parse(await take()));
                assert.equal(await fallbackPage.locator('#description').textContent(), '另一段详细说明。');
                assert.deepEqual(await fallbackPage.evaluate(() => [document.querySelector('#input').value,
                    document.querySelector('#textarea').value, document.querySelector('#edit').textContent]),
                ['MISSION', 'MISSION', 'MISSION']);
            });
            await test('fallback rejects replies from the prior document and does not enter iframes', async () => {
                await fallbackPage.evaluate(() => { document.querySelector('#description').firstChild.nodeValue = 'Delayed old description.'; });
                const old = parse(await take());
                await fallbackPage.goto(url + 'fallback-next');
                await receive(old);
                assert.equal(await fallbackPage.locator('#description').textContent(), 'A detailed description.');
                await receive(parse(await take()));
                await fallbackPage.evaluate(() => {
                    const frame = document.createElement('iframe');
                    frame.srcdoc = '<h1>SITUATION</h1><p>A detailed description.</p>';
                    document.body.append(frame);
                });
                await fallbackPage.waitForFunction(() => document.querySelector('iframe').contentDocument.querySelector('p'));
                assert.equal(await fallbackPage.evaluate(() => document.querySelector('iframe').contentDocument.querySelector('h1').textContent), 'SITUATION');
                assert.equal(await fallbackPage.evaluate(() => typeof document.querySelector('iframe').contentWindow.__cmoWebTranslation), 'undefined');
            });
            await test('fallback batches stay bounded and external pages remain untouched', async () => {
                await fallbackPage.evaluate(() => {
                    for (let i = 0; i < 64; i++) {
                        const p = document.createElement('p'); p.textContent = 'Long fallback ' + i + '中文 English '.repeat(900);
                        document.body.append(p);
                    }
                });
                let batches = 0;
                for (let encoded; (encoded = await take());) {
                    const message = decodeURIComponent(encoded);
                    assert.ok(message.length <= 262144);
                    assert.ok(message.split('\n').length <= 65);
                    await receive(parse(encoded)); batches++;
                }
                assert.ok(batches > 1);
                assert.equal(await fallbackPage.evaluate(() => window.__cmoWebTranslation.stats.pending), 0);
                await fallbackPage.evaluate(() => {
                    const bad = document.createElement('p'); bad.textContent = 'Malformed ' + String.fromCharCode(0xdc00) + ' paragraph';
                    const good = document.createElement('p'); good.id = 'fallback-after-bad';
                    good.textContent = 'A navigation-only description.';
                    document.body.append(bad, good);
                });
                await receive(parse(await take()));
                assert.equal(await fallbackPage.locator('#fallback-after-bad').textContent(), '只用于导航测试的说明。');
                assert.equal(await fallbackPage.evaluate(() => window.__cmoWebTranslation.stats.pending), 0);
                await fallbackContext.route('https://outside.invalid/**', route => route.fulfill({
                    contentType: 'text/html', body: '<h1>SITUATION</h1><p>A detailed description.</p>'
                }));
                await fallbackPage.goto('https://outside.invalid/');
                assert.equal(await fallbackPage.evaluate(() => typeof window.__cmoWebTranslation), 'undefined');
                assert.equal(await fallbackPage.locator('h1').textContent(), 'SITUATION');
            });
        } finally { await fallbackContext.close(); }
        assert.deepEqual(errors, []);
        console.log(`${passed.length}/${passed.length} browser checks passed; zero page errors`);
        if (process.argv.includes('--benchmark')) {
            // Browser-only comparison isolates the discovery delay. The native transport
            // and translation cost are substituted equally; these are not game timings.
            const baseline = await browser.newPage();
            const comparison = await baseline.evaluate(async () => {
                const results = [];
                for (const interval of [600, 1500]) {
                    const start = performance.now();
                    document.body.innerHTML = '<p id="sample">A detailed description.</p>';
                    await new Promise(resolve => setTimeout(resolve, interval));
                    // The previous pipeline walks the body only after its watcher tick.
                    const walker = document.createTreeWalker(document.body, NodeFilter.SHOW_TEXT);
                    let node;
                    while ((node = walker.nextNode())) {
                        if (node.nodeValue === 'A detailed description.') node.nodeValue = '一段详细说明。';
                    }
                    results.push({ pollingIntervalMs: interval, visibleEnglishMs: performance.now() - start });
                }
                return results;
            });
            await baseline.close();
            const latencies = [];
            for (let i = 0; i < 20; i++) {
                latencies.push(await page.evaluate(async i => {
                    const p = document.createElement('p'); p.textContent = 'Unique description sample-' + i;
                    const start = performance.now(); document.body.append(p);
                    await new Promise(resolve => {
                        const observer = new MutationObserver(() => {
                            if (p.textContent.startsWith('独特说明 ')) { observer.disconnect(); resolve(); }
                        });
                        observer.observe(p, { childList: true, characterData: true, subtree: true });
                    });
                    p.remove(); return performance.now() - start;
                }, i));
            }
            latencies.sort((a, b) => a - b);
            console.log('LATENCY_BENCHMARK ' + JSON.stringify({ environment: 'headless Edge; substituted native transport',
                baseline: comparison, incrementalSamples: latencies.length,
                incrementalMedianMs: latencies[Math.floor(latencies.length / 2)], incrementalP95Ms: latencies[18] }));
        }
    } finally {
        await context.close();
        await browser.close();
        await new Promise(resolve => server.close(resolve));
    }
}
main().catch(error => { console.error(error); process.exitCode = 1; });
