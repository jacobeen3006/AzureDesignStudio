// Offline precache filter of the published service worker (wwwroot/service-worker.published.js) - tests.
// Runs the real worker in a Node vm against minimal stand-ins for self, caches and Request; no browser, no packages.
// Run: node --test src/AzureDesignStudio/JsTests/*.test.cjs
// Kept outside wwwroot on purpose: everything under wwwroot is published with the app.
//
// D-10: appsettings*.json must never be precached, because the worker serves cached files before the
// network and a role or scope related configuration change would otherwise stay stale in the browser.
// Urls in the generated assets manifest are relative with no leading slash.
const fs = require('node:fs');
const vm = require('node:vm');
const assert = require('node:assert/strict');
const { test } = require('node:test');
const path = require('node:path');

const wwwroot = path.join(__dirname, '..', 'wwwroot');

/** Loads the real worker with a manifest of the given urls and returns what its own onInstall step caches. */
async function precached(urls) {
    const requested = [];
    const self_ = {
        importScripts: () => {},
        addEventListener: () => {},
        skipWaiting: () => {},
        assetsManifest: { version: 'test', assets: urls.map(url => ({ url, hash: 'sha256-test' })) },
    };
    class FakeRequest {
        constructor(url, init) { this.url = url; this.init = init; }
    }
    const context = vm.createContext({
        self: self_,
        Request: FakeRequest,
        console: { info() {} },
        caches: { open: async () => ({ addAll: async (requests) => { requested.push(...requests.map(r => r.url)); } }) },
    });
    const source = fs.readFileSync(path.join(wwwroot, 'service-worker.published.js'), 'utf8');
    vm.runInContext(source, context);
    await vm.runInContext('onInstall()', context);
    return requested;
}

const manifestUrls = [
    'appsettings.json',
    'appsettings.Development.json',
    'appsettings.Production.json',
    'service-worker.js',
    'ads-stencils.json',
    'index.html',
    '_framework/blazor.boot.json',
    '_framework/AzureDesignStudio.Core.dll',
    'css/app.css',
];

test('appsettings files are not precached', async () => {
    const cached = await precached(manifestUrls);
    for (const url of ['appsettings.json', 'appsettings.Development.json', 'appsettings.Production.json'])
        assert.ok(!cached.includes(url), `${url} must not be precached`);
});

test('the worker script itself is still excluded', async () => {
    const cached = await precached(manifestUrls);
    assert.ok(!cached.includes('service-worker.js'));
});

test('other json, html, dll and css assets are still precached', async () => {
    const cached = await precached(manifestUrls);
    for (const url of ['ads-stencils.json', 'index.html', '_framework/blazor.boot.json',
        '_framework/AzureDesignStudio.Core.dll', 'css/app.css'])
        assert.ok(cached.includes(url), `${url} must still be precached`);
});

test('the exclusion is not wider than appsettings files', async () => {
    const lookalikes = ['myappsettings.json', 'appsettings.txt', 'data/appsettings.json.bak.json', 'settings.json'];
    const cached = await precached(lookalikes);
    // '.txt' is not an included extension at all; the three .json lookalikes must be cached.
    for (const url of ['myappsettings.json', 'data/appsettings.json.bak.json', 'settings.json'])
        assert.ok(cached.includes(url), `${url} is not an appsettings file and must still be precached`);
});

test('the exclusion changes caching, not publishing: wwwroot/appsettings.json still exists', () => {
    assert.ok(fs.existsSync(path.join(wwwroot, 'appsettings.json')));
});
