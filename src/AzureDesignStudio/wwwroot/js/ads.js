async function diagram2PicAsync(type, width, height) {
    var diagram = document.querySelector(".diagram-canvas");
    var dataUrl = "";
    if (type === "png") {
        dataUrl = await domtoimage.toPng(diagram, {
            bgcolor: '#ffffff',
            width: width,
            height: height,
        });
    } else if (type === "jpeg") {
        dataUrl = domtoimage.toJpeg(diagram, {
            bgcolor: '#ffffff',
            width: width,
            height: height,
        });
    }

    return dataUrl;
}

async function downloadFileFromStream(fileName, contentType, contentStreamReference) {
    const arrayBuffer = await contentStreamReference.arrayBuffer();
    const blob = new Blob([arrayBuffer], { type: contentType });

    const url = URL.createObjectURL(blob);

    triggerFileDownload(fileName, url);

    URL.revokeObjectURL(url);
}

function triggerFileDownload(fileName, url) {
    const anchorElement = document.createElement('a');
    anchorElement.href = url;

    if (fileName) {
        anchorElement.download = fileName;
    }

    anchorElement.click();
    anchorElement.remove();
}

function getWindowSize() {
    return {
        width: window.innerWidth,
        height: window.innerHeight,
    }
}

// HTML5 drop seam: MAUI BlazorWebView loses DragEventArgs.dataTransfer in
// blazor.webview.js serialization (dotnet/maui#2205), which aborts Blazor's
// event-args deserialization before any @ondrop handler runs. The stencil key
// travels on the tile's data-stencil-key attribute (native dragstart capture),
// and the drop listener forwards key + viewport coordinates to the component.
window.AdsDropZone = {
    dragKey: '',
    init: function (dotNetRef, selector) {
        const zone = document.querySelector(selector);
        if (!zone) {
            return;
        }
        // Always adopt the newest component reference: a remount disposes the
        // previous DotNetObjectReference, but listeners live on the zone element.
        zone.__adsRef = dotNetRef;
        if (zone.__adsDrop) {
            return;
        }
        zone.__adsDrop = true;
        const self = this;
        const clearDropState = function () {
            zone.classList.remove('ads-drop-active');
            zone.classList.remove('ads-drop-hover');
            document.body.classList.remove('ads-dragging');
        };

        document.addEventListener('dragstart', function (e) {
            const tile = e.target && e.target.closest ? e.target.closest('[data-stencil-key]') : null;
            self.dragKey = tile ? tile.getAttribute('data-stencil-key') : '';
            if (self.dragKey) {
                zone.classList.add('ads-drop-active');
                document.body.classList.add('ads-dragging');
            }
        });
        document.addEventListener('dragend', clearDropState);

        // Capture phase: the diagram canvas and its nodes/groups may stop
        // propagation on drop, which would otherwise swallow a drop that lands
        // on an existing resource or frame.
        zone.addEventListener('dragover', function (e) {
            e.preventDefault();
            if (e.dataTransfer) {
                e.dataTransfer.dropEffect = 'copy';
            }
            zone.classList.add('ads-drop-hover');
        }, true);
        // Without a dragenter cancel, an external drag that enters node/group
        // content is abandoned by Chrome (observed: dragenter on the icon IMG,
        // then no further dragover dispatch and no drop). Cancelling dragenter
        // keeps the session alive so drop reaches the zone.
        zone.addEventListener('dragenter', function (e) { e.preventDefault(); }, true);
        zone.addEventListener('dragleave', function (e) {
            if (!e.relatedTarget || !zone.contains(e.relatedTarget)) {
                zone.classList.remove('ads-drop-hover');
            }
        }, true);
        zone.addEventListener('drop', function (e) {
            e.preventDefault();
            e.stopPropagation();
            clearDropState();
            if (zone.__adsRef) {
                zone.__adsRef.invokeMethodAsync('HandleDropAtAsync', e.clientX, e.clientY, self.dragKey || '');
            }
        }, true);
    }
};

/*
 * Pointer-based stencil drag. Native HTML5 drag is not reliable in every
 * WebView2 input path (touch/pen, some host policies). Pointer events use one
 * code path for mouse, pen, and touch and still feed the same Blazor drop
 * handler as the native-drop seam above.
 */
window.AdsStencilDrag = {
    init: function (selector) {
        const zone = document.querySelector(selector);
        if (!zone) {
            return;
        }
        // The wrapper is Blazor-owned: a remount replaces the element and
        // disposes the previous DotNetObjectReference with it. Guard on the
        // element, and mark it before the document-level early return so a
        // later wrapper still binds while the listeners stay attached once.
        if (zone.__adsPointerInit) {
            return;
        }
        zone.__adsPointerInit = true;
        if (window.__adsPointerDocInit) {
            return;
        }
        window.__adsPointerDocInit = true;

        // Listeners live on document because a drag starts in the stencil
        // rail, outside the zone, so every read resolves the live wrapper at
        // event time instead of measuring a captured (possibly detached) node.
        const liveZone = function () {
            return document.querySelector(selector);
        };

        let active = null;

        const inZone = function (x, y) {
            const z = liveZone();
            if (!z) {
                return false;
            }
            const r = z.getBoundingClientRect();
            return x >= r.left && x <= r.right && y >= r.top && y <= r.bottom;
        };

        const clear = function (drag) {
            document.body.classList.remove('ads-dragging');
            const z = liveZone();
            if (z) {
                z.classList.remove('ads-drop-active');
                z.classList.remove('ads-drop-hover');
            }
            if (drag && drag.ghost) {
                drag.ghost.remove();
            }
        };

        document.addEventListener('pointerdown', function (e) {
            if (e.button !== 0) {
                return;
            }
            const tile = e.target && e.target.closest ? e.target.closest('[data-stencil-key]') : null;
            if (!tile) {
                return;
            }
            const key = tile.getAttribute('data-stencil-key');
            if (!key) {
                return;
            }
            active = {
                key: key,
                label: (tile.closest('[title]')?.getAttribute('title') || tile.textContent.trim() || key),
                pointerId: e.pointerId,
                startX: e.clientX,
                startY: e.clientY,
                dragging: false,
                ghost: null
            };
        }, true);

        document.addEventListener('pointermove', function (e) {
            if (!active || e.pointerId !== active.pointerId) {
                return;
            }

            const dx = e.clientX - active.startX;
            const dy = e.clientY - active.startY;
            if (!active.dragging && (Math.abs(dx) < 4 && Math.abs(dy) < 4)) {
                return;
            }

            if (!active.dragging) {
                active.dragging = true;
                document.body.classList.add('ads-dragging');
                const dragZone = liveZone();
                if (dragZone) {
                    dragZone.classList.add('ads-drop-active');
                }

                const ghost = document.createElement('div');
                ghost.className = 'ads-drag-ghost';
                ghost.textContent = active.label || active.key;
                document.body.appendChild(ghost);
                active.ghost = ghost;
            }

            e.preventDefault();
            if (active.ghost) {
                active.ghost.style.left = e.clientX + 'px';
                active.ghost.style.top = e.clientY + 'px';
            }
            const hoverZone = liveZone();
            if (hoverZone) {
                hoverZone.classList.toggle('ads-drop-hover', inZone(e.clientX, e.clientY));
            }
        }, { passive: false, capture: true });

        const finish = function (e, cancelled) {
            if (!active || (e.pointerId && e.pointerId !== active.pointerId)) {
                return;
            }

            const drag = active;
            active = null;
            clear(drag);

            const dropZone = liveZone();
            if (drag.dragging && !cancelled && inZone(e.clientX, e.clientY) && dropZone && dropZone.__adsRef) {
                dropZone.__adsRef.invokeMethodAsync('HandleDropAtAsync', e.clientX, e.clientY, drag.key);
            }
        };

        document.addEventListener('pointerup', function (e) { finish(e, false); }, true);
        document.addEventListener('pointercancel', function (e) { finish(e, true); }, true);
        window.addEventListener('blur', function () {
            if (active) {
                const drag = active;
                active = null;
                clear(drag);
            }
        });
    }
};

/* ---------- shell: theme + keyboard ---------- */

window.AdsTheme = {
    modes: ['light', 'dark', 'system'],
    normalize: function (mode) {
        return this.modes.indexOf(mode) >= 0 ? mode : 'system';
    },
    get: function () {
        try { return this.normalize(localStorage.getItem('ads.theme') || 'system'); }
        catch (e) { return 'system'; }
    },
    resolvedDark: function (mode) {
        return mode === 'dark' || (mode === 'system' && window.matchMedia('(prefers-color-scheme: dark)').matches);
    },
    apply: function (mode) {
        mode = this.normalize(mode);
        var dark = this.resolvedDark(mode);
        document.documentElement.setAttribute('data-ads-theme', dark ? 'dark' : 'light');

        var link = document.getElementById('antd-theme');
        if (link) {
            var href = '_content/AntDesign/css/ant-design-blazor' + (dark ? '.dark' : '') + '.css';
            if (link.getAttribute('href') !== href) {
                link.setAttribute('href', href);
            }
        }
        return mode;
    },
    set: function (mode) {
        mode = this.normalize(mode);
        try { localStorage.setItem('ads.theme', mode); } catch (e) { }
        return this.apply(mode);
    },
    cycle: function () {
        var current = this.get();
        var next = this.modes[(this.modes.indexOf(current) + 1) % this.modes.length];
        return this.set(next);
    },
    init: function () {
        var self = this;
        this.apply(this.get());
        if (window.matchMedia) {
            var query = window.matchMedia('(prefers-color-scheme: dark)');
            var listener = function () { if (self.get() === 'system') self.apply('system'); };
            if (query.addEventListener) query.addEventListener('change', listener);
            else if (query.addListener) query.addListener(listener);
        }
    }
};

window.AdsTheme.init();

window.AdsShortcuts = {
    init: function (ref) {
        window.__adsShortcutRef = ref;
        if (window.__adsShortcutsInit) return;
        window.__adsShortcutsInit = true;
        document.addEventListener('keydown', function (e) {
            if ((e.ctrlKey || e.metaKey) && (e.key === 'k' || e.key === 'K')) {
                e.preventDefault();
                if (window.__adsShortcutRef) {
                    window.__adsShortcutRef.invokeMethodAsync('OpenFromShortcut');
                }
            }
        });
    }
};