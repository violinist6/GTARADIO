// Copies the radio pack into the app's private storage in 8 MB pieces.
onmessage = async (e) => {
  try {
    const file = e.data.file;
    const root = await navigator.storage.getDirectory();
    const fh = await root.getFileHandle('radio.pack', { create: true });
    const h = await fh.createSyncAccessHandle();
    try {
      h.truncate(0);
      const CH = 8 << 20;
      let at = 0;
      while (at < file.size) {
        const buf = new Uint8Array(await file.slice(at, Math.min(file.size, at + CH)).arrayBuffer());
        let off = 0;
        while (off < buf.length) off += h.write(off === 0 ? buf : buf.subarray(off), { at: at + off });
        at += buf.length;
        postMessage({ progress: at / file.size });
      }
      h.flush();
    } finally { h.close(); }
    const ready = await root.getFileHandle('radio.ready', { create: true });
    const rh = await ready.createSyncAccessHandle(); rh.write(new Uint8Array([49]), { at: 0 }); rh.flush(); rh.close();
    postMessage({ done: true });
  } catch (err) {
    postMessage({ error: (err && err.name === 'QuotaExceededError') ? 'Your iPhone ran out of space while copying the radio.' : String(err && err.message || err) });
  }
};
