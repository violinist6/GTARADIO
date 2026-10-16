// Plays the live PCM stream from the local server, with a burst of tuning static between stations.
class PcmPlayer extends AudioWorkletProcessor {
  constructor() {
    super();
    this.size = 48000 * 2 * 16;
    this.buf = new Float32Array(this.size);
    this.r = 0; this.w = 0; this.count = 0;
    this.started = false;
    this.staticLeft = 0; this.n1 = 0; this.n2 = 0;
    this.port.onmessage = (e) => {
      const m = e.data;
      // Chunks from a station we already left can still arrive after a flush; drop them by generation.
      if (m.type === 'pcm') { if (m.gen === this.gen) this.push(m.data); }
      else if (m.type === 'flush') { this.r = this.w = this.count = 0; this.started = false; this.gen = m.gen; }
      else if (m.type === 'static') this.staticLeft = Math.floor(48000 * m.ms / 1000);
    };
  }
  push(d) {
    for (let i = 0; i < d.length; i++) {
      // On overflow drop a whole stereo frame so left and right never swap.
      if (this.count >= this.size - 1) { this.r = (this.r + 2) % this.size; this.count -= 2; }
      this.buf[this.w] = d[i]; this.w = (this.w + 1) % this.size; this.count++;
    }
  }
  process(inputs, outputs) {
    const out = outputs[0], L = out[0], R = out[1] || out[0];
    if (!this.started && this.count >= 48000 * 2 * 0.35) this.started = true;
    for (let i = 0; i < L.length; i++) {
      let l = 0, r = 0;
      if (this.started && this.count >= 2) {
        l = this.buf[this.r]; r = this.buf[(this.r + 1) % this.size];
        this.r = (this.r + 2) % this.size; this.count -= 2;
      } else if (this.started) { this.started = false; }
      if (this.staticLeft > 0) {
        // Band-limited noise with a little crackle, fading out.
        const fade = Math.min(1, this.staticLeft / 4800);
        const white = Math.random() * 2 - 1;
        this.n1 = 0.75 * this.n1 + 0.25 * white;
        this.n2 = 0.9 * this.n2 + 0.1 * this.n1;
        let s = (this.n1 - this.n2) * 0.9 + (Math.random() < 0.002 ? (Math.random() - 0.5) : 0);
        s *= 0.35 * fade;
        l = l * (1 - fade) + s; r = r * (1 - fade) + s;
        this.staticLeft--;
      }
      L[i] = l; R[i] = r;
    }
    return true;
  }
}
registerProcessor('pcm-player', PcmPlayer);
