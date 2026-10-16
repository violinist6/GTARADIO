// Decides what each station plays. Every station keeps running on the shared clock whether or not
// you're listening; tuning in just reads the timeline at "now". A port of the PC program's Engine.cs.
const RATE = 48000;
const CAT_NEWS = 3, CAT_SOLO = 5;

const Clock = {
  t0: performance.now(),
  get now() { return Math.floor((performance.now() - this.t0) / 1000 * RATE); },
};

function mulberry32(a) {
  return () => { a |= 0; a = a + 0x6D2B79F5 | 0; let t = Math.imul(a ^ a >>> 15, 1 | a); t = t + Math.imul(t ^ t >>> 7, 61 | t) ^ t; return ((t ^ t >>> 14) >>> 0) / 4294967296; };
}

// Time-of-day DJ lines follow Melbourne time.
const melbHour = () => +new Intl.DateTimeFormat('en-AU', { timeZone: 'Australia/Melbourne', hour: 'numeric', hourCycle: 'h23' }).format(new Date());

class Runner {
  constructor(st, radio, cfg, seed) {
    this.st = st; this.radio = radio; this.cfg = cfg; this.rng = mulberry32(seed);
    this.timeline = []; this.pending = []; this.history = {}; this.last = {};
    this.songsSinceBreak = 0; this.nextBreakAfter = this.int(2, 5);
  }
  int(a, b) { return a + Math.floor(this.rng() * (b - a)); }
  minGap(cat) { const ms = this.radio.minGap && this.radio.minGap[cat]; return ms ? ms * RATE / 1000 : 0; }

  ensure(upTo) {
    const tl = this.timeline;
    if (tl.length === 0) {
      // Like the game, a station starts somewhere in the middle of something.
      const first = this.next(0);
      first.start = -Math.floor(this.rng() * first.len * 0.9);
      tl.push(first);
    }
    while (end(tl[tl.length - 1]) < upTo) {
      const e = end(tl[tl.length - 1]);
      const s = this.next(e); s.start = e; tl.push(s);
    }
    while (tl.length > 8 && end(tl[1]) < upTo - 30 * 60 * RATE) tl.shift();
  }
  at(t) {
    this.ensure(t + 5 * 60 * RATE);
    for (const s of this.timeline) if (t >= s.start && t < end(s)) return s;
    return this.timeline[this.timeline.length - 1];
  }
  range(from, to) {
    this.ensure(to + 60 * RATE);
    return this.timeline.filter(s => end(s) > from && s.start < to);
  }

  pick(key, list, keep = 10) {
    if (!list || list.length === 0) return null;
    const h = this.history[key] || (this.history[key] = []);
    keep = Math.min(keep, list.length - 1);
    let choices = list.filter(t => !h.includes(t.n));
    if (choices.length === 0) choices = list;
    const p = choices[Math.floor(this.rng() * choices.length)];
    h.push(p.n);
    while (h.length > keep) h.shift();
    return p;
  }
  since(what, now) { return what in this.last ? now - this.last[what] : Number.MAX_SAFE_INTEGER / 4; }

  next(now) {
    if (this.pending.length) return this.pending.shift();
    const st = this.st;
    if (st.kind === 'mix') return seg(this.pick('music', st.music, st.music.length), 'music');
    if (st.kind === 'talk') return this.nextTalk(now);
    return this.nextMusic(now);
  }

  nextTalk(now) {
    const show = seg(this.pick('music', this.st.music), 'music');
    this.queueBreak(now + show.len, true);
    return show;
  }

  queueBreak(at, newsAllowed) {
    const st = this.st, news = this.radio.news;
    if (newsAllowed && this.cfg.news && news.length && this.since('news', at) >= this.minGap(CAT_NEWS) && this.rng() < 0.4) {
      this.pending.push(seg(this.pick('news', news, 20), 'news')); this.last.news = at;
    } else {
      const n = st.ads.length === 0 ? 0 : this.int(2, 4);
      for (let i = 0; i < n; i++) this.pending.push(seg(this.pick('ad', st.ads, 20), 'ad'));
      this.last.ad = at;
    }
    const id = this.pick('ident', st.idents);
    if (id) this.pending.push(seg(id, 'ident'));
  }

  nextMusic(now) {
    const st = this.st, news = this.radio.news;
    const s = seg(this.pick('music', st.music), 'music');
    this.tryIntro(s);
    this.songsSinceBreak++;
    const e = now + s.len;
    let follow = 'none';
    if (this.songsSinceBreak >= this.nextBreakAfter && st.ads.length > 0)
      follow = this.cfg.news && news.length && this.since('news', e) >= this.minGap(CAT_NEWS) && this.rng() < 0.4 ? 'news' : 'ad';
    else if (st.solos.length > 0 && this.since('solo', e) >= this.minGap(CAT_SOLO) && this.rng() < 0.12) follow = 'solo';
    else if (st.idents.length > 0 && this.rng() < 0.4) follow = 'ident';

    if (follow === 'ad' || follow === 'news') {
      this.songsSinceBreak = 0; this.nextBreakAfter = this.int(2, 5);
      // The DJ throws to the break over the song's outro, or straight after it.
      const key = 'to_' + follow;
      const lead = this.pick(key, st.speech[key], 4);
      if (lead && !this.tryOutro(s, lead)) this.pending.push(seg(lead, 'speech'));
      if (follow === 'news') { this.pending.push(seg(this.pick('news', news, 20), 'news')); this.last.news = e; }
      else {
        const n = this.int(2, 4);
        for (let i = 0; i < n; i++) this.pending.push(seg(this.pick('ad', st.ads, 20), 'ad'));
        this.last.ad = e;
      }
      const id = this.pick('ident', st.idents);
      if (id) this.pending.push(seg(id, 'ident'));
    } else if (follow === 'solo') {
      this.pending.push(seg(this.pick('solo', st.solos), 'solo')); this.last.solo = e;
    } else if (follow === 'ident') {
      this.pending.push(seg(this.pick('ident', st.idents), 'ident'));
    } else if (this.rng() < 0.25) {
      const g = this.pick('general', st.speech.general, 8);
      if (g) this.tryOutro(s, g);
    }
    return s;
  }

  tryIntro(s) {
    const t = s.track;
    if (t.ie <= 0 || this.rng() > 0.6) return;
    const win = t.ie - Math.max(0, t.is);
    const h = melbHour();
    const tod = h >= 5 && h < 12 ? 'morning' : h >= 12 && h < 17 ? 'afternoon' : h >= 17 && h < 22 ? 'evening' : 'night';
    const usable = [['intro:' + t.n, 3], [tod, 1.5], ['general', 1]]
      .map(([key, w]) => ({ key, w, lines: (this.st.speech[key] || []).filter(l => l.s / RATE <= win + 0.3) }))
      .filter(o => o.lines.length > 0);
    if (!usable.length) return;
    let r = this.rng() * usable.reduce((a, o) => a + o.w, 0);
    let box = usable[0];
    for (const o of usable) { if ((r -= o.w) <= 0) { box = o; break; } }
    const line = this.pick(box.key, box.lines, 6);
    // Speech is timed to finish as the vocals come in.
    const at = Math.max(Math.floor(t.ie * RATE) - line.s, Math.floor(Math.max(0, t.is) * RATE));
    s.overlays.push({ at, line, len: line.s });
  }

  tryOutro(s, line) {
    const t = s.track;
    if (t.os <= 0) return false;
    const at = Math.floor(t.os * RATE);
    if (at + line.s > s.len) return false;
    s.overlays.push({ at, line, len: line.s });
    return true;
  }
}

function seg(track, kind) { return { track, kind, len: track.s, start: 0, overlays: [] }; }
function end(s) { return s.start + s.len; }

// What's on now, for the display.
function nowPlaying(r, t) {
  const s = r.at(t), pos = (t - s.start) / RATE;
  let title = null, artist = null;
  const pretty = n => n.replace('mono_', '').replace(/_/g, ' ').trim().replace(/\b\w/g, c => c.toUpperCase());
  switch (s.kind) {
    case 'music': {
      const cues = s.track.c || [];
      let cue = null;
      for (const c of cues) if (c[0] <= pos) cue = c;
      cue = cue || cues[0];
      if (cue) { title = cue[1]; artist = cue[2]; }
      if (!title && r.st.kind !== 'mix') title = pretty(s.track.n);
      if (r.st.kind === 'talk') { artist = null; title = title || pretty(s.track.n); }
      break;
    }
    case 'ad': title = 'Advert'; break;
    case 'news': title = 'Weazel News'; break;
    case 'ident': title = r.st.name; break;
    default: title = 'DJ';
  }
  const rel = t - s.start;
  const dj = s.overlays.some(o => rel >= o.at && rel < o.at + o.len);
  return { kind: s.kind, title, artist, dj };
}
