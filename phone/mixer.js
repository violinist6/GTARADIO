// Decodes the Opus clips from the pack and mixes a station to stereo PCM: music, DJ speech over it
// with the music ducked, and breaks. A port of the PC program's Mixer.cs.
const FRAME = 960;          // 20 ms Opus frames
const CHUNK = 250;          // frames read from storage at a time (5 s)
const PREROLL = 4;          // frames decoded and thrown away after a seek

// One compressed clip: header, packet offsets, and packet data read on demand.
class Clip {
  constructor(id) { this.id = id; this.chunks = new Map(); }
  async open() {
    const [off] = Store.index.blobs[this.id];
    const h = new DataView((await Store.read(off, 12)).buffer);
    this.frames = h.getUint32(0, true); this.ch = h.getUint16(4, true); this.la = h.getUint16(6, true); this.samples = h.getUint32(8, true);
    const sizes = await Store.read(off + 12, this.frames * 2);
    const sv = new DataView(sizes.buffer, sizes.byteOffset, sizes.byteLength);
    this.offs = new Float64Array(this.frames + 1);
    for (let i = 0; i < this.frames; i++) this.offs[i + 1] = this.offs[i] + sv.getUint16(i * 2, true);
    this.data = off + 12 + this.frames * 2;
  }
  async load(f0, f1) {
    for (let k = Math.floor(f0 / CHUNK); k <= Math.floor(f1 / CHUNK); k++) {
      if (this.chunks.has(k)) continue;
      const a = k * CHUNK, b = Math.min(this.frames, a + CHUNK);
      this.chunks.set(k, await Store.read(this.data + this.offs[a], this.offs[b] - this.offs[a]));
      if (this.chunks.size > 6) this.chunks.delete(this.chunks.keys().next().value);
    }
  }
  packet(f) {
    const k = Math.floor(f / CHUNK), c = this.chunks.get(k), base = this.offs[k * CHUNK];
    return c.subarray(this.offs[f] - base, this.offs[f + 1] - base);
  }
}

// Reads a clip at any sample position, keeping a decoder warm for continuous playback.
class Source {
  constructor(clip) { this.clip = clip; this.frames = []; this.next = -1; }
  async init() {
    const ch = this.clip.ch;
    this.dec = new window['opus-decoder'].OpusDecoder({ channels: ch, streamCount: 1, coupledStreamCount: ch === 2 ? 1 : 0, channelMappingTable: ch === 2 ? [0, 1] : [0] });
    await this.dec.ready;
  }
  // Returns [left, right] (the same array twice for mono) for samples [pos, pos+n) of the clip.
  async read(pos, n) {
    const c = this.clip, L = new Float32Array(n), R = c.ch === 2 ? new Float32Array(n) : L;
    const s = Math.max(0, pos) + c.la, e = pos + n + c.la;
    if (pos + n <= 0 || pos >= c.samples || e <= s) return [L, R];
    const fs = Math.floor(s / FRAME), fe = Math.min(c.frames - 1, Math.floor((e - 1) / FRAME));
    // Restart the decoder when this read doesn't follow on from the last one.
    const have = this.frames.length ? this.frames[0].f : -1;
    if (this.next < 0 || fs < have || fs > this.next) {
      await this.dec.reset();
      this.frames = [];
      this.next = Math.max(0, fs - PREROLL);
    }
    if (this.next <= fe) await c.load(this.next, fe);
    while (this.next <= fe) {
      const out = this.dec.decodeFrame(c.packet(this.next));
      this.frames.push({ f: this.next, d: out.channelData });
      this.next++;
    }
    while (this.frames.length && this.frames[0].f < fs) this.frames.shift();
    for (const fr of this.frames) {
      const base = fr.f * FRAME;
      for (let i = Math.max(s, base); i < Math.min(e, base + FRAME); i++) {
        const o = i - c.la - pos;
        L[o] = fr.d[0][i - base];
        if (c.ch === 2) R[o] = fr.d[1][i - base];
      }
    }
    return [L, R];
  }
  free() { try { this.dec.free(); } catch (e) {} }
}

class Mixer {
  constructor() { this.sources = new Map(); this.tick = 0; }
  static DUCK = 0.45;              // music level under DJ speech
  static RAMP = RATE / 4;

  // Sources are keyed by use as well as clip, so a DJ line reusing the song's clip can't fight over a decoder.
  async get(id, role) {
    const key = role + ':' + id;
    this.tick++;
    let e = this.sources.get(key);
    if (!e) {
      const clip = new Clip(id); await clip.open();
      const src = new Source(clip); await src.init();
      e = { src, last: 0 };
      this.sources.set(key, e);
    }
    e.last = this.tick;
    if (this.sources.size > 8)
      for (const [k, v] of [...this.sources]) if (this.tick - v.last > 50) { v.src.free(); this.sources.delete(k); }
    return e.src;
  }

  // Renders n frames of the station starting at clock time t, as interleaved stereo.
  async render(runner, t, n) {
    const out = new Float32Array(n * 2);
    for (const s of runner.range(t, t + n)) {
      const a = Math.max(t, s.start), b = Math.min(t + n, s.start + s.len);
      if (b <= a) continue;
      const cn = b - a, o = a - t;
      const [l, r] = await (await this.get(s.track.b, 'main')).read(a - s.start, cn);
      for (let i = 0; i < cn; i++) {
        const rel = a - s.start + i;
        let env = 0;
        for (const ov of s.overlays) {
          const R = Mixer.RAMP;
          const e = rel < ov.at - R || rel >= ov.at + ov.len + R ? 0
            : rel < ov.at ? (rel - (ov.at - R)) / R
            : rel < ov.at + ov.len ? 1
            : 1 - (rel - (ov.at + ov.len)) / R;
          if (e > env) env = e;
        }
        const g = 1 - (1 - Mixer.DUCK) * env;
        out[(o + i) * 2] += l[i] * g;
        out[(o + i) * 2 + 1] += r[i] * g;
      }
      for (const ov of s.overlays) {
        const os = s.start + ov.at, oe = os + ov.len;
        const ca = Math.max(a, os), cb = Math.min(b, oe);
        if (cb <= ca) continue;
        const [sl, sr] = await (await this.get(ov.line.b, 'dj')).read(ca - os, cb - ca);
        const co = ca - t;
        for (let i = 0; i < cb - ca; i++) { out[(co + i) * 2] += sl[i]; out[(co + i) * 2 + 1] += sr[i]; }
      }
    }
    return out;
  }

  clear() { for (const v of this.sources.values()) v.src.free(); this.sources.clear(); }
}
