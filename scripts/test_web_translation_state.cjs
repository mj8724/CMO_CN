#!/usr/bin/env node
// Focused real-Edge tests of the injected state machine. No game, profile or network.
// NODE_PATH may point at the installed puppeteer-core package directory.
'use strict';
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const { execFileSync } = require('node:child_process');
const puppeteer = require('puppeteer-core');

const root = path.resolve(__dirname, '..');
const sourcePath = path.join(root, 'src/engine/WebViewTranslation.js');
const source = process.argv.includes('--baseline') ?
    execFileSync('git', ['show', 'HEAD:src/engine/WebViewTranslation.js'], { cwd: root, encoding: 'utf8' }) :
    fs.readFileSync(sourcePath, 'utf8');
const edge = process.env.CMO_TEST_EDGE || 'C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe';
const makeScript = messages => source.replace('__CMO_WEB_LABELS__', JSON.stringify({
    SITUATION: '战场态势', MISSION: '任务'
})).replace('__CMO_WEB_FILE_ROOT__', '"file:///g:/game/"')
    .replace('__CMO_WEB_MESSAGES_ENABLED__', messages ? 'true' : 'false');
const bootstrap = makeScript(false);
const mode = process.argv.find(arg => arg.startsWith('--case='));
const selected = mode ? mode.slice(7) : null;
let browser, page;
const errors = [];
const passed = [];

function parse(encoded, translate = s => '中文 ' + s) {
    const message = decodeURIComponent(encoded);
    assert.ok(message.length <= 262144, 'encoded batch size stays bounded');
    const lines = message.split('\n');
    assert.ok(lines.length >= 2 && lines.length <= 65, '1..64 requests per batch');
    const nonce = lines.shift().split('\t')[1];
    const updates = lines.map(line => {
        const tab = line.indexOf('\t');
        const source = decodeURIComponent(line.slice(tab + 1));
        assert.ok(source.length <= 16384, 'node size limit is preserved');
        return [Number(line.slice(0, tab)), source, translate(source)];
    });
    return { nonce, updates };
}
const take = () => page.evaluate(() => window.__cmoWebTranslation.take());
const receive = reply => page.evaluate(({ nonce, updates }) => window.__cmoWebTranslation.receive(nonce, updates), reply);
const stats = () => page.evaluate(() => ({ ...window.__cmoWebTranslation.stats }));
async function fresh() {
    await page.goto('about:blank');
    await page.evaluate(bootstrap);
    assert.equal(await page.evaluate(() => typeof window.__cmoWebTranslation), 'object');
}
async function drain(expectedNodes) {
    let requests = 0, batches = 0;
    const deadline = Date.now() + 10000;
    while (true) {
        const encoded = await take();
        if (encoded) {
            const reply = parse(encoded);
            requests += reply.updates.length;
            batches++;
            await receive(reply);
        } else {
            const ready = await page.evaluate(expected => window.__cmoWebTranslation.stats.pending === 0 &&
                document.querySelectorAll('#overflow p').length === expected &&
                Array.from(document.querySelectorAll('#overflow p')).every(p => p.textContent.startsWith('中文 ')), expectedNodes);
            if (ready) break;
            if (Date.now() > deadline) throw new Error('deferred translation did not resume after capacity release: ' + JSON.stringify(await stats()));
            // Yield to the actual 0ms cursor continuation, not an assumed completion delay.
            await page.evaluate(() => new Promise(resolve => setTimeout(resolve, 0)));
        }
    }
    return { requests, batches, stats: await stats() };
}
async function test(name, body) {
    if (selected && selected !== name) return;
    await fresh();
    await body();
    passed.push(name);
    console.log('PASS ' + name);
}
async function main() {
    assert.ok(fs.existsSync(edge), 'isolated Edge executable exists');
    browser = await puppeteer.launch({ executablePath: edge, headless: true });
    page = await browser.newPage();
    page.on('pageerror', error => errors.push(error.message));
    try {
        await test('capacity-resume', async () => {
            const pending = await page.evaluate(async () => {
                const section = document.createElement('section'); section.id = 'overflow';
                for (let i = 0; i < 2500; i++) {
                    const p = document.createElement('p'); p.textContent = 'Overflow description ' + i; section.append(p);
                }
                document.body.append(section);
                await Promise.resolve();
                return window.__cmoWebTranslation.stats.pending;
            });
            assert.equal(pending, 2048, 'initial request capacity is not lowered');
            const result = await drain(2500);
            assert.equal(result.requests, 2500, 'every overflow node is eventually served exactly once');
            assert.ok(result.stats.continuationSlices > 0 && result.stats.continuationSlices <= 12, 'bounded cursor yields, no per-root retry list');
            console.log('CAPACITY ' + JSON.stringify(result));
            assert.equal(await take(), null, 'completed page is not rescanned repeatedly');
        });
        await test('capacity-detached-cursor', async () => {
            await page.evaluate(async () => {
                const full = document.createElement('section'); full.id = 'full';
                for (let i = 0; i < 2048; i++) {
                    const p = document.createElement('p'); p.textContent = 'Full description ' + i; full.append(p);
                }
                const rejected = document.createElement('section'); rejected.id = 'rejected';
                rejected.innerHTML = '<p>Rejected description before removal</p>';
                const survivors = document.createElement('section'); survivors.id = 'overflow';
                for (let i = 0; i < 20; i++) {
                    const p = document.createElement('p'); p.textContent = 'Surviving description ' + i; survivors.append(p);
                }
                document.body.append(full, rejected, survivors);
                await Promise.resolve();
                full.remove(); rejected.remove();
                await Promise.resolve();
            });
            const result = await drain(20);
            assert.equal(result.requests, 20, 'detached cursor recovers surviving later subtrees');
            assert.equal(result.stats.pending, 0);
        });
        await test('same-node-thousand-values', async () => {
            await page.evaluate(() => {
                const p = document.createElement('p'); p.id = 'changing'; p.textContent = 'Changing description 0'; document.body.append(p);
            });
            const obsolete = parse(await take());
            const maxPending = await page.evaluate(async () => {
                const node = document.querySelector('#changing').firstChild;
                let max = 0;
                for (let i = 1; i <= 1000; i++) {
                    node.nodeValue = 'Changing description ' + i;
                    await Promise.resolve();
                    max = Math.max(max, window.__cmoWebTranslation.stats.pending);
                }
                return max;
            });
            assert.equal(maxPending, 1, 'one node keeps only its current live request');
            const current = parse(await take());
            assert.equal(current.updates.length, 1, 'unsent obsolete sources do not accumulate in the queue');
            assert.equal(current.updates[0][1], 'Changing description 1000');
            assert.equal(await take(), null);
            await receive(obsolete);
            assert.equal(await page.$eval('#changing', p => p.textContent), 'Changing description 1000');
            await receive(current);
            assert.equal(await page.$eval('#changing', p => p.textContent), '中文 Changing description 1000');
            assert.equal((await stats()).pending, 0);
            console.log('SAME_NODE maxPending=' + maxPending + ' remainingRequests=' + current.updates.length);
        });
        await test('shared-request-peer', async () => {
            await page.evaluate(() => { document.body.innerHTML = '<p id="one">Shared description.</p><p id="two">Shared description.</p>'; });
            const shared = parse(await take());
            assert.equal(shared.updates.length, 1);
            await page.evaluate(() => { document.querySelector('#one').firstChild.nodeValue = 'Fresh description.'; });
            const freshReply = parse(await take());
            assert.equal((await stats()).pending, 2, 'unchanged peer retains the shared request');
            await receive(shared);
            assert.equal(await page.$eval('#two', p => p.textContent), '中文 Shared description.');
            assert.equal(await page.$eval('#one', p => p.textContent), 'Fresh description.');
            await receive(freshReply);
            assert.equal(await page.$eval('#one', p => p.textContent), '中文 Fresh description.');
            assert.equal((await stats()).pending, 0);
        });
        await test('nonce-editing-and-reinsertion', async () => {
            await page.evaluate(() => {
                const p = document.createElement('p'); p.id = 'guard'; p.textContent = 'Guarded description.';
                document.body.append(p);
            });
            const current = parse(await take());
            await receive({ nonce: 'wrong-document', updates: current.updates });
            assert.equal((await stats()).pending, 1);
            await receive({ nonce: current.nonce, updates: [[current.updates[0][0], 'wrong-source', '错误覆盖']] });
            assert.equal((await stats()).pending, 1, 'source mismatch cannot consume a live request');
            await page.evaluate(() => { document.querySelector('#guard').contentEditable = 'true'; });
            await receive(current);
            assert.equal(await page.$eval('#guard', p => p.textContent), 'Guarded description.');
            assert.equal((await stats()).pending, 0);
            await page.evaluate(() => {
                document.body.innerHTML = '<p id="moved">Moved description.</p>';
                window.moved = document.querySelector('#moved');
            });
            const old = parse(await take());
            await page.evaluate(async () => { moved.remove(); await Promise.resolve(); });
            assert.equal((await stats()).pending, 0);
            await page.evaluate(() => document.body.append(moved));
            const freshReply = parse(await take());
            assert.notEqual(freshReply.updates[0][0], old.updates[0][0]);
            await receive(old);
            assert.equal(await page.$eval('#moved', p => p.textContent), 'Moved description.');
            await receive(freshReply);
            assert.equal(await page.$eval('#moved', p => p.textContent), '中文 Moved description.');
        });
        await test('timeout-with-new-state', async () => {
            await page.evaluate(() => {
                const nativeTimeout = setTimeout;
                window.expirations = [];
                window.setTimeout = (fn, delay) => delay === 15000 ? (expirations.push(fn), 0) : nativeTimeout(fn, delay);
                document.body.innerHTML = '<p id="changing">Old timeout description.</p>';
            });
            const old = parse(await take());
            await page.evaluate(() => { document.querySelector('#changing').firstChild.nodeValue = 'Fresh timeout description.'; });
            const freshReply = parse(await take(), () => '中文 Retained state');
            await receive(freshReply);
            await page.evaluate(async () => {
                expirations.shift()();
                const p = document.querySelector('#changing'); p.remove(); document.body.append(p);
                await Promise.resolve();
            });
            await receive(old);
            assert.equal(await page.$eval('#changing', p => p.textContent), '中文 Retained state');
            assert.equal(await take(), null, 'old timeout cannot erase a newer successful translation state');
        });
        await test('encoding-and-size', async () => {
            await page.evaluate(() => {
                const bad = document.createElement('p'); bad.id = 'malformed';
                bad.textContent = 'Malformed ' + String.fromCharCode(0xd800) + ' paragraph';
                const huge = document.createElement('p'); huge.id = 'oversized'; huge.textContent = 'Oversized ' + 'x'.repeat(16385);
                const good = document.createElement('p'); good.id = 'healthy'; good.textContent = 'Healthy description.';
                document.body.append(bad, huge, good);
                const input = document.createElement('textarea'); input.value = 'MISSION'; document.body.append(input);
                const editable = document.createElement('div'); editable.contentEditable = 'true'; editable.innerHTML = '<span>MISSION</span>'; document.body.append(editable);
            });
            const reply = parse(await take());
            assert.equal(reply.updates.length, 1, 'bad UTF-16 and oversized nodes never block a healthy node');
            await receive(reply);
            assert.equal((await stats()).pending, 0);
            assert.equal(await page.$eval('#healthy', p => p.textContent), '中文 Healthy description.');
            assert.equal(await page.$eval('textarea', p => p.value), 'MISSION');
            assert.equal(await page.$eval('[contenteditable]', p => p.textContent), 'MISSION');
            await page.evaluate(() => {
                for (let i = 0; i < 64; i++) {
                    const p = document.createElement('p'); p.textContent = 'Mixed batch ' + i + '中文 English '.repeat(900); document.body.append(p);
                }
            });
            let batches = 0, requests = 0;
            for (let encoded; (encoded = await take());) {
                const batch = parse(encoded); requests += batch.updates.length; await receive(batch); batches++;
            }
            assert.equal(requests, 64);
            assert.ok(batches > 1);
            assert.equal((await stats()).pending, 0);
        });
        await test('messaging-overflow', async () => {
            await page.goto('about:blank');
            await page.evaluate(() => {
                window.transport = [];
                window.chrome = window.chrome || {};
                window.chrome.webview = { postMessage: message => transport.push(message) };
            });
            await page.evaluate(makeScript(true));
            await page.evaluate(() => {
                const section = document.createElement('section'); section.id = 'overflow';
                for (let i = 0; i < 2050; i++) {
                    const p = document.createElement('p'); p.textContent = 'Messaging description ' + i; section.append(p);
                }
                document.body.append(section);
            });
            const deadline = Date.now() + 10000;
            let requests = 0;
            while (true) {
                const messages = await page.evaluate(() => transport.splice(0));
                for (const message of messages) {
                    const reply = parse(encodeURIComponent(message)); requests += reply.updates.length; await receive(reply);
                }
                if (requests === 2050 && (await stats()).pending === 0) break;
                if (Date.now() > deadline) throw Error('message-mode overflow did not recover');
                await page.evaluate(() => new Promise(resolve => setTimeout(resolve, 0)));
            }
            assert.equal(await page.$$eval('#overflow p', ps => ps.filter(p => !p.textContent.startsWith('中文 ')).length), 0);
            console.log('MESSAGING requests=' + requests);
        });
        await test('origin-and-frame-isolation', async () => {
            const results = await page.evaluate(script => {
                const results = [];
                for (const href of ['https://outside.invalid/', 'https://127.example.invalid/',
                    'https://127.0.0.1.example.invalid/', 'file:///g:/outside/info.html', 'file:///g:/game-other/info.html']) {
                    let posts = 0;
                    const win = { chrome: { webview: { postMessage: () => posts++ } } }; win.top = win;
                    new Function('window', 'location', script)(win, { href });
                    results.push({ href, installed: !!win.__cmoWebTranslation, posts });
                }
                const frame = {}; frame.top = {};
                new Function('window', script)(frame);
                results.push({ href: 'child-frame', installed: !!frame.__cmoWebTranslation, posts: 0 });
                return results;
            }, makeScript(true));
            assert.ok(results.every(result => !result.installed && result.posts === 0));
            console.log('ORIGIN ' + JSON.stringify(results));
        });
        assert.ok(passed.length > 0, 'case selection must execute a test');
        assert.deepEqual(errors, [], 'zero page errors');
        console.log(passed.length + '/' + passed.length + ' focused state checks passed; zero page errors');
    } finally {
        await browser.close();
    }
}
main().catch(error => { console.error(error); process.exitCode = 1; });
