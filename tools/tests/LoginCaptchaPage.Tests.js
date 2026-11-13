'use strict';
// Executes the actual generated page in a small DOM fixture. Provider callbacks
// are replaced; no SDK network request, SMS, account submission or human
// verification is attempted by this harness.
const fs = require('fs');
const vm = require('vm');
const html = fs.readFileSync(process.argv[2], 'utf8');
const source = /<script>([\s\S]*?)<\/script>/.exec(html)[1];
let checks = 0;
function check(condition, name) { ++checks; if (!condition) throw new Error(name); }
function fixture(width, height) {
    let now = 1000, configuration, ready, success, close, error, verified = 0, proof = null, observerStopped = false;
    const events = {}, messages = [], elements = [], status = { style: {}, textContent: '' };
    let script;
    // Match the actual selector subset used by the production adapter rather
    // than returning every fixture node. In particular, a point challenge's
    // entire image must never be treated as an answer tile or clicked by A.
    function matches(el, selector) {
        return selector.split(',').some(part => {
            part = part.trim();
            const excluded = /:not\(\.([\w-]+)\)/.exec(part);
            if (excluded && el.className.split(/\s+/).includes(excluded[1])) return false;
            part = part.replace(/:not\(\.[\w-]+\)/g, '');
            const className = /^\.([\w-]+)$/.exec(part);
            if (className) return el.className.split(/\s+/).includes(className[1]);
            const attribute = /^\[([\w-]+)=['"]?([\w-]+)['"]?\]$/.exec(part);
            if (attribute) return el.attributes[attribute[1]] === attribute[2];
            if (/^[a-z]+$/i.test(part)) return el.tagName === part.toLowerCase();
            throw new Error('Fixture does not implement production selector: ' + part);
        });
    }
    function query(selector, scope) {
        return elements.filter(el => (!scope || el.scope === scope) && matches(el, selector));
    }
    const document = {
        activeElement: null,
        body: {},
        documentElement: { clientWidth: width, clientHeight: height },
        head: { appendChild(s) { script = s; } },
        createElement() { return {}; },
        querySelectorAll(selector) { return query(selector); },
        getElementById(id) { return id === 'status' ? status : area; },
        addEventListener(name, handler) { events[name] = handler; }
    };
    const area = { style: {}, querySelectorAll(selector) { return query(selector, 'area'); } };
    function mutationObserver(callback) { this.observe = () => { this.callback = callback; }; this.disconnect = () => { observerStopped = true; }; }
    const window = {
        innerWidth: width, innerHeight: height,
        external: { notify(json) { messages.push(JSON.parse(json)); } },
        getComputedStyle(el) { return { display: el.hidden ? 'none' : 'block', visibility: 'visible' }; },
        requestAnimationFrame(callback) { callback(); },
        addEventListener(name, handler) { events[name] = handler; },
        MutationObserver: mutationObserver
    };
    const context = vm.createContext({
        window, document, MutationObserver: mutationObserver, Date: { now: () => now },
        initGeetest(config, callback) {
            configuration = config;
            callback({
                onReady(fn) { ready = fn; }, onSuccess(fn) { success = fn; }, onClose(fn) { close = fn; }, onError(fn) { error = fn; },
                verify() { ++verified; }, getValidate() { return proof; }
            });
        }
    });
    vm.runInContext(source, context);
    function element(name, x, y, w = 90, h = 90, options = {}) {
        const el = {
            name, clicks: 0, attributes: options.attributes || {}, scrolls: 0,
            className: options.className === undefined ? 'geetest_item' : options.className,
            tagName: options.tagName || 'div', scope: options.scope || 'area',
            getBoundingClientRect: () => ({ left: x, top: y, width: w, height: h }),
            setAttribute(key, value) { this.attributes[key] = value; },
            focus() { document.activeElement = this; },
            scrollIntoView() { ++this.scrolls; },
            click() { ++this.clicks; }
        };
        elements.push(el); return el;
    }
    function key(code, repeat = false, name = '') {
        let prevented = false;
        events.keydown({ keyCode: code, repeat, key: name, preventDefault() { prevented = true; }, stopPropagation() {} });
        return prevented;
    }
    return {
        area, status, messages, document, elements, element, key,
        load() { script.onload(); }, ready() { ready(); }, success(value) { proof = value; success(); }, close() { close(); }, error() { error(); },
        release() { events.keyup(); }, advance(ms) { now += ms; }, pagehide() { events.pagehide(); },
        get config() { return configuration; }, get verified() { return verified; }, get observerStopped() { return observerStopped; }
    };
}
check(html.includes("body{overflow:auto;}"), 'Small verification viewports preserve scrolling');
check(!html.includes('transform:') && !html.includes('zoom:'), 'No CSS scaling changes provider click coordinates');
const page = fixture(800, 296);
page.load();
check(page.config.product === 'bind' && page.config.area === '#challenge-area', 'Complete provider challenge bound to dedicated area');
check(page.config.next_width === '210px', 'Challenge sizing includes available height rather than full wide column');
check(page.area.style.height === '480px', 'Small viewport retains a scrollable complete provider surface');
const tiles = [];
for (let row = 0; row < 3; ++row) for (let col = 0; col < 3; ++col) tiles.push(page.element('tile-' + row + col, col * 100, row * 100));
const confirm = page.element('confirm', 0, 310, 290, 40, { className: 'geetest_commit' });
const hidden = page.element('hidden', 0, -200); hidden.hidden = true;
const unrelated = page.element('unrelated document content', 0, -300, 90, 90, { className: '' });
const disabled = page.element('disabled provider button', 0, -400, 90, 90, { className: 'geetest_refresh' }); disabled.disabled = true;
page.ready();
check(page.verified === 1, 'SDK verify is opened once after ready');
check(page.status.style.display === 'none', 'Loading text leaves full viewport for provider instructions');
check(page.document.activeElement === tiles[0], 'First visible provider tile receives focus');
check(!unrelated.attributes['data-tv-captcha'] && !disabled.attributes['data-tv-captcha'], 'Only matching visible enabled provider controls become keyboard targets');
check(tiles.every(tile => tile.clicks === 0) && confirm.clicks === 0, 'Opening never selects answers or submits confirmation');
check(page.messages.length === 1 && page.messages[0].kind === 'ready', 'Ready reports no successful proof');
check(page.key(39) && page.document.activeElement === tiles[1], 'Right moves to next provider tile');
page.key(39); check(page.document.activeElement === tiles[1], 'Duplicate immediate direction is ignored');
page.advance(449); page.key(39); check(page.document.activeElement === tiles[1], 'Held direction waits for initial delay');
page.advance(1); page.key(39); check(page.document.activeElement === tiles[2], 'Held direction moves after initial delay');
page.release(); page.key(40); check(page.document.activeElement === tiles[5], 'Down preserves tile column');
page.release(); page.key(40); check(page.document.activeElement === tiles[8], 'Down reaches last tile row');
page.release(); page.key(40); check(page.document.activeElement === confirm && confirm.scrolls > 0, 'Down reveals and focuses real provider confirm');
page.release(); page.key(13); check(confirm.clicks === 1, 'A/Enter activates the human focused provider control');
page.advance(1000); page.key(13); check(confirm.clicks === 1, 'Held confirm cannot submit multiple times even without repeat flag');
page.release(); page.key(13, true); check(confirm.clicks === 1, 'Native repeated Enter cannot resubmit');
page.key(196); check(page.messages[1].kind === 'cancel' && page.messages[1].nonce, 'Gamepad B cancels through nonce-bound bridge');
page.success({ geetest_challenge: 'x', geetest_validate: 'y', geetest_seccode: 'z' });
check(page.messages.length === 2, 'Late provider success after cancel is discarded');
page.pagehide(); check(page.observerStopped, 'Page teardown removes provider observer');
for (const cancelCode of [27, 143, 166]) {
    const cancelPage = fixture(800, 500); cancelPage.load(); cancelPage.ready(); cancelPage.key(cancelCode);
    check(cancelPage.messages[1].kind === 'cancel', 'Cancellation key ' + cancelCode + ' reaches native owner');
}
const large = fixture(1600, 1000); large.load(); check(large.config.next_width === '420px', 'Large viewport keeps challenge legible without oversizing');
large.ready();
large.success(null); check(large.messages.length === 1, 'Empty provider proof cannot complete verification');
large.success({ geetest_challenge: 'actual-c', geetest_validate: 'actual-v', geetest_seccode: 'actual-s' });
check(large.messages[1].challenge === 'actual-c' && large.messages[1].validate === 'actual-v' && large.messages[1].seccode === 'actual-s', 'Success transmits exact provider proof');
large.close(); check(large.messages.length === 2, 'Completed verification ignores delayed close');
const failed = fixture(700, 400); failed.load(); failed.error(); check(failed.messages[0].kind === 'error', 'Provider error cancels proof completion');

// The user video shows a single image with arbitrary selected word positions.
// The provider can append that popup directly to document.body in bind mode.
// Preserve its manual point selection; only make its real action bar reachable.
const point = fixture(800, 296);
point.load();
const image = point.element('word point image', 0, 0, 290, 290, { className: 'geetest_item geetest_big_item', scope: 'body' });
const numberedPoint = point.element('already selected numbered point', 120, 40, 20, 20, { className: 'geetest_point', scope: 'body' });
const pointConfirm = point.element('provider confirm below image', 0, 310, 220, 40, { className: 'geetest_commit', scope: 'body' });
const pointRefresh = point.element('provider refresh', 240, 310, 40, 40, { className: 'geetest_refresh', scope: 'body' });
point.ready();
check(point.document.activeElement === null && pointConfirm.scrolls === 0, 'Opening point challenge preserves instructions instead of jumping to its bottom actions');
check(!image.attributes['data-tv-captcha'] && !numberedPoint.attributes['data-tv-captcha'], 'Point image and selected coordinate markers remain outside tile focus adaptation');
check(image.clicks === 0 && numberedPoint.clicks === 0 && pointConfirm.clicks === 0, 'Opening point challenge never selects image coordinates or confirms answers');
point.key(39);
check(point.document.activeElement === pointConfirm && pointConfirm.scrolls === 1, 'Explicit keyboard navigation finds and reveals actual confirm outside designated area');
point.release(); point.key(39);
check(point.document.activeElement === pointRefresh, 'Right reaches actual point challenge refresh action');
point.release(); point.key(37);
check(point.document.activeElement === pointConfirm, 'Left returns to actual confirm without inventing tile targets');
point.release(); point.key(13);
check(pointConfirm.clicks === 1 && image.clicks === 0, 'Human A activates only real focused confirm and never synthesizes answer coordinates');
point.key(196);
check(point.messages[1].kind === 'cancel', 'Point verification retains cancellation after keyboard action');
console.log('PASS: ' + checks + ' generated verification page behavior checks. No network or SMS requests sent.');
