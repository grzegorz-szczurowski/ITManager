// File: wwwroot/js/fileDownload.js
// Description: Minimal helper do pobierania plików z Blazor (base64 -> Blob -> download).
// Version: 1.00
// Created: 2026-01-22

export function downloadFileFromBase64(fileName, contentType, base64Data) {
    if (!base64Data) return;

    const bytes = base64ToUint8Array(base64Data);
    const blob = new Blob([bytes], { type: contentType || "application/octet-stream" });

    const url = URL.createObjectURL(blob);
    try {
        const a = document.createElement("a");
        a.href = url;
        a.download = fileName || "export.bin";
        a.style.display = "none";
        document.body.appendChild(a);
        a.click();
        document.body.removeChild(a);
    } finally {
        URL.revokeObjectURL(url);
    }
}

function base64ToUint8Array(base64) {
    const binaryString = atob(base64);
    const len = binaryString.length;
    const bytes = new Uint8Array(len);

    for (let i = 0; i < len; i++) {
        bytes[i] = binaryString.charCodeAt(i);
    }

    return bytes;
}
