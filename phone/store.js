// Keeps the radio pack (made by GTARadio.exe) in this app's private storage on the phone, and reads
// byte ranges from it. Nothing is uploaded anywhere.
const Store = {
  file: null,
  index: null,

  async open() {
    try {
      const root = await navigator.storage.getDirectory();
      await root.getFileHandle('radio.ready');
      const h = await root.getFileHandle('radio.pack');
      this.file = await h.getFile();
      const head = new DataView(await this.file.slice(0, 24).arrayBuffer());
      if (new TextDecoder().decode(new Uint8Array(head.buffer, 0, 8)) !== 'GTARPK01') return false;
      const at = Number(head.getBigUint64(8, true)), len = Number(head.getBigUint64(16, true));
      this.index = JSON.parse(await this.file.slice(at, at + len).text());
      return true;
    } catch (e) { return false; }
  },

  async read(off, len) { return new Uint8Array(await this.file.slice(off, off + len).arrayBuffer()); },

  // Copies the chosen pack into private storage in a worker, reporting progress (0..1).
  async import(file, onProgress) {
    const head = new TextDecoder().decode(new Uint8Array(await file.slice(0, 8).arrayBuffer()));
    if (head !== 'GTARPK01') throw new Error('That isn\'t a GTARadio-phone.pack file.');
    try { await navigator.storage.persist(); } catch (e) {}
    try {
      const est = await navigator.storage.estimate();
      if (est.quota && est.quota - (est.usage || 0) < file.size * 1.02)
        throw new Error(`Not enough space: the radio needs ${(file.size / 1e9).toFixed(1)} GB and this app can use ${((est.quota - (est.usage || 0)) / 1e9).toFixed(1)} GB. Free up some space on your iPhone and try again.`);
    } catch (e) { if (e.message.startsWith('Not enough')) throw e; }
    const root = await navigator.storage.getDirectory();
    try { await root.removeEntry('radio.ready'); } catch (e) {}
    await new Promise((resolve, reject) => {
      const w = new Worker('import-worker.js');
      w.onmessage = e => {
        if (e.data.progress != null) onProgress(e.data.progress);
        if (e.data.done) { w.terminate(); resolve(); }
        if (e.data.error) { w.terminate(); reject(new Error(e.data.error)); }
      };
      w.onerror = e => { w.terminate(); reject(new Error(e.message || 'Copy failed')); };
      w.postMessage({ file });
    });
    if (!await this.open()) throw new Error('The copy didn\'t finish properly. Try again.');
  },

  async clear() {
    const root = await navigator.storage.getDirectory();
    for (const n of ['radio.ready', 'radio.pack']) try { await root.removeEntry(n); } catch (e) {}
    this.file = null; this.index = null;
  },
};
