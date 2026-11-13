// Stencil drag state machine (AdsStencilDrag in wwwroot/js/ads.js) - regression tests.
// Runs the real ads.js in a Node vm against a minimal DOM stand-in; no browser, no packages.
// Run: node --test src/AzureDesignStudio/JsTests/*.test.cjs
// Kept outside wwwroot on purpose: everything under wwwroot is published with the app.
const fs = require('node:fs');
const vm = require('node:vm');
const assert = require('node:assert/strict');
const { test } = require('node:test');
const path = require('node:path');

// --- fixture factory -------------------------------------------------------
function fixture() {
    const listeners = {};
    const ghosts = new Set();
    const drops = [];
    const classes = () => {
        const values = new Set();
        return {
            add: (x) => values.add(x),
            remove: (x) => values.delete(x),
            contains: (x) => values.has(x),
            toggle: (x, on) => on ? values.add(x) : values.delete(x),
        };
    };
    const body = { classList: classes(), appendChild: (g) => ghosts.add(g) };
    const zone = {
        classList: classes(),
        getBoundingClientRect: () => ({ left: 100, top: 0, right: 500, bottom: 500 }),
        __adsRef: { invokeMethodAsync: (...args) => { drops.push(args); return Promise.resolve(); } },
    };
    const document = {
        body,
        querySelector: () => zone,
        createElement: () => {
            const g = { style: {}, remove: () => ghosts.delete(g) };
            return g;
        },
        addEventListener: (name, fn) => (listeners[name] ||= []).push(fn),
        documentElement: { setAttribute() {} },
        getElementById: () => null,
    };
    const window_ = {
        addEventListener: (name, fn) => (listeners[`window:${name}`] ||= []).push(fn),
        matchMedia: () => ({ matches: false, addEventListener() {} }),
    };
    const source = fs.readFileSync(path.join(__dirname, '..', 'wwwroot', 'js', 'ads.js'), 'utf8');
    vm.runInNewContext(source, { document, window: window_, localStorage: { getItem: () => 'light' } });
    window_.AdsStencilDrag.init('.zone');

    // Stencil tile used by start().
    const tile = { getAttribute: () => 'firewall', closest: () => null, textContent: 'Firewall' };
    const tileTarget = { closest: () => tile };
    const canvas = { closest: () => null };

    // Event emitter helper — default target is canvas unless overridden.
    const emit = (name, overrides = {}) => {
        const event = {
            pointerId: 1,
            pointerType: 'mouse',
            button: 0,
            buttons: 1,
            clientX: 150,
            clientY: 50,
            target: canvas,
            preventDefault() {},
            ...overrides,
        };
        for (const fn of listeners[name] || []) fn(event);
    };

    // Start a drag from the stencil tile.
    const start = () => {
        emit('pointerdown', { target: tileTarget, clientX: 10 });
        emit('pointermove');
    };

    const emitWindow = (name) => {
        for (const fn of listeners[`window:${name}`] || []) fn({});
    };

    const setRef = (ref) => { zone.__adsRef = ref; };

    return { emit, emitWindow, setRef, start, ghosts, drops, body, tileTarget };
}

// --- tests ------------------------------------------------------------------

test('normal drag delivers exactly one drop and removes feedback', () => {
    const f = fixture();
    f.start();
    f.emit('pointerup', { buttons: 0 });
    assert.equal(f.drops.length, 1);
    assert.equal(f.ghosts.size, 0);
});

test('released outside viewport cannot replay on a later canvas click', () => {
    // Sequence: start → pointermove(buttons=0) cancels active → pointerdown has no effect
    const f = fixture();
    f.start();
    f.emit('pointermove', { buttons: 0 });
    f.emit('pointerdown');
    f.emit('pointerup', { buttons: 0 });
    assert.equal(f.drops.length, 0);
    assert.equal(f.ghosts.size, 0);
});

test('new canvas pointerdown cancels stale drag even without a move', () => {
    // Sequence: start → pointerdown (same pointer) clears active → pointerup sees nothing
    const f = fixture();
    f.start();
    f.emit('pointerdown');
    f.emit('pointerup', { buttons: 0 });
    assert.equal(f.drops.length, 0);
    assert.equal(f.ghosts.size, 0);
});

test('replacement drag never leaves an orphaned ghost', () => {
    // Start a drag, immediately replace it with another pointerdown+move+up.
    // The replacement should end cleanly: zero orphaned ghosts, exactly one drop.
    const f = fixture();
    f.start();
    f.emit('pointerdown', { target: f.tileTarget, clientX: 10 });
    f.emit('pointermove');
    f.emit('pointerup', { buttons: 0 });
    assert.equal(f.ghosts.size, 0);
    assert.equal(f.drops.length, 1);
});

test('cancellation never drops a resource', () => {
    const f = fixture();
    f.start();
    f.emit('pointercancel', { buttons: 0 });
    assert.equal(f.drops.length, 0);
    assert.equal(f.ghosts.size, 0);
});

test('a second pointer landing on a tile mid-drag does not orphan the first ghost', () => {
    // Finger 1 is dragging; finger 2 (a new pointer id) presses a tile. The first drag's
    // ghost must be removed - `active` used to be overwritten and the ghost stayed on screen.
    const f = fixture();
    f.start();
    assert.equal(f.ghosts.size, 1);
    f.emit('pointerdown', { pointerId: 2, pointerType: 'touch', target: f.tileTarget, clientX: 10 });
    assert.equal(f.ghosts.size, 0);
    f.emit('pointermove', { pointerId: 2, pointerType: 'touch' });
    f.emit('pointerup', { pointerId: 2, pointerType: 'touch', buttons: 0 });
    assert.equal(f.ghosts.size, 0);
    assert.equal(f.drops.length, 1);
});

test('a stale touch drag is cleared by the next touch, which has a new pointer id', () => {
    const f = fixture();
    f.emit('pointerdown', { pointerId: 7, pointerType: 'touch', target: f.tileTarget, clientX: 10 });
    f.emit('pointermove', { pointerId: 7, pointerType: 'touch' });
    assert.equal(f.ghosts.size, 1);
    // The release was lost. A later tap on the canvas arrives as pointer 8.
    f.emit('pointerdown', { pointerId: 8, pointerType: 'touch' });
    f.emit('pointerup', { pointerId: 8, pointerType: 'touch', buttons: 0 });
    assert.equal(f.ghosts.size, 0);
    assert.equal(f.drops.length, 0);
    assert.equal(f.body.classList.contains('ads-dragging'), false);
});

test('releasing the primary button while another is held cancels the drag', () => {
    // buttons = 2: secondary still down, primary released (outside the window, say).
    const f = fixture();
    f.start();
    f.emit('pointermove', { buttons: 2 });
    f.emit('pointerup', { buttons: 0 });
    assert.equal(f.drops.length, 0);
    assert.equal(f.ghosts.size, 0);
});

test('window blur cancels the drag without a drop', () => {
    const f = fixture();
    f.start();
    f.emitWindow('blur');
    f.emit('pointerup', { buttons: 0 });
    assert.equal(f.drops.length, 0);
    assert.equal(f.ghosts.size, 0);
});

test('a click on a tile without movement is not a drag', () => {
    const f = fixture();
    f.emit('pointerdown', { target: f.tileTarget, clientX: 150 });
    f.emit('pointermove', { clientX: 151 }); // under the 4px threshold
    f.emit('pointerup', { buttons: 0 });
    assert.equal(f.drops.length, 0);
    assert.equal(f.ghosts.size, 0);
});

test('pointer id 0 is treated as a real id', () => {
    // A drag owned by pointer 0 must still ignore the release of another pointer.
    const f = fixture();
    f.emit('pointerdown', { pointerId: 0, target: f.tileTarget, clientX: 10 });
    f.emit('pointermove', { pointerId: 0 });
    f.emit('pointerup', { pointerId: 3, buttons: 0 });
    assert.equal(f.drops.length, 0);
    assert.equal(f.ghosts.size, 1); // still dragging
    f.emit('pointerup', { pointerId: 0, buttons: 0 });
    assert.equal(f.drops.length, 1);
    assert.equal(f.ghosts.size, 0);
});

test('a drop on a disposed component reference does not raise an unhandled rejection', async () => {
    const f = fixture();
    let unhandled = 0;
    const onUnhandled = () => { unhandled++; };
    process.on('unhandledRejection', onUnhandled);
    try {
        f.start();
        // Swap in a reference whose call fails the way a disposed DotNetObjectReference does.
        const zoneRef = { invokeMethodAsync: () => Promise.reject(new Error('disposed')) };
        f.setRef(zoneRef);
        f.emit('pointerup', { buttons: 0 });
        await new Promise((resolve) => setImmediate(resolve));
        await new Promise((resolve) => setImmediate(resolve));
        assert.equal(unhandled, 0);
    } finally {
        process.off('unhandledRejection', onUnhandled);
    }
});