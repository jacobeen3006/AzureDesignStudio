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
        document.addEventListener('dragstart', function (e) {
            const tile = e.target && e.target.closest ? e.target.closest('[data-stencil-key]') : null;
            self.dragKey = tile ? tile.getAttribute('data-stencil-key') : '';
        });
        zone.addEventListener('dragover', function (e) { e.preventDefault(); });
        // Dropping over a node icon (<img> is natively draggable) otherwise
        // makes Chrome abandon the external drag session: no further dragover
        // reaches the zone and no drop fires. Keep the session alive.
        zone.addEventListener('dragenter', function (e) { e.preventDefault(); });
        zone.addEventListener('drop', function (e) {
            e.preventDefault();
            if (zone.__adsRef) {
                zone.__adsRef.invokeMethodAsync('HandleDropAtAsync', e.clientX, e.clientY, self.dragKey || '');
            }
        });
    }
};