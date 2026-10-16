// Plays the PCM the page renders. Holds up to 90 s so playback rides out short pauses in the page.
class PcmPlayer extends AudioWorkletProcessor {
  constructor() {
    super();
    this.size = 48000 * 2 * 90;
    this.buf = new Float32Array(this.size);
    this.r = 0; this.w = 0; this.count = 0; this.started = false; this.gen = 0; this.played = 0;
    this.port.onmessage = (e) => {
      const m = e.data;
      // Chunks from a station we already left can still arrive after a flush; drop them by generation.
      if (m.type === 'pcm') { if (m.gen === this.gen) this.push(m.data); }
      else if (m.type === 'flush') { this.r = this.w = this.count = 0; this.started = false; this.gen = m.gen; }
    };
  }
  push(d) {
    for (let i = 0; i < d.length; i++) {
      if (this.count >= this.size - 1) { this.r = (this.r + 2) % this.size; this.count -= 2; }
      this.buf[this.w] = d[i]; this.w = (this.w + 1) % this.size; this.count++;
    }
  }
  process(inputs, outputs) {
    const out = outputs[0], L = out[0], R = out[1] || out[0];
    if (!this.started && this.count >= 48000 * 2 * 0.3) this.started = true;
    for (let i = 0; i < L.length; i++) {
      let l = 0, r = 0;
      if (this.started && this.count >= 2) {
        l = this.buf[this.r]; r = this.buf[this.r + 1];
        this.r = (this.r + 2) % this.size; this.count -= 2;
      } else if (this.started) this.started = false;
      L[i] = l; R[i] = r;
    }
    // Tell the page how much is buffered, about four times a second.
    if ((this.played += L.length) >= 12000) { this.played = 0; this.port.postMessage({ buffered: this.count / 2 }); }
    return true;
  }
}
registerProcessor('pcm-player', PcmPlayer);
