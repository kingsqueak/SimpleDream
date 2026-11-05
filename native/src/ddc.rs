//! Digital down-converter: takes TCI IQ at any integer multiple of 48 kHz, mirrors it if
//! needed, moves the wanted channel to 0 Hz and decimates to the decoder's 48 kHz.

/// Where the sharp filter is 6 dB down, Hz. DRM's widest channel is 20 kHz.
const FINE_CUTOFF_HZ: f64 = 18_200.0;

/// One decimating FIR over complex samples.
struct Fir {
    decim: usize,
    taps: Vec<f32>,
    /// Double-length delay lines, so the filter always reads a contiguous slice.
    re: Vec<f32>,
    im: Vec<f32>,
    pos: usize,
    count: usize,
}

impl Fir {
    fn new(decim: usize, taps: Vec<f32>) -> Fir {
        let n = taps.len();
        Fir { decim, taps, re: vec![0.0; 2 * n], im: vec![0.0; 2 * n], pos: 0, count: 0 }
    }

    #[inline]
    fn push(&mut self, i: f32, q: f32) -> Option<(f32, f32)> {
        let n = self.taps.len();
        self.re[self.pos] = i;
        self.re[self.pos + n] = i;
        self.im[self.pos] = q;
        self.im[self.pos + n] = q;
        self.pos = (self.pos + 1) % n;
        self.count += 1;
        if self.count < self.decim {
            return None;
        }
        self.count = 0;
        let (r, m) = (&self.re[self.pos..self.pos + n], &self.im[self.pos..self.pos + n]);
        Some((dot(&self.taps, r), dot(&self.taps, m)))
    }
}

pub struct Ddc {
    rate: u32,
    /// A short filter down to 96 kHz, then the sharp one down to 48 kHz: at 384 kHz this
    /// is about a third of the arithmetic of one filter doing it all. Rates of 96 kHz
    /// and below have only the second.
    coarse: Option<Fir>,
    fine: Fir,
    osc: (f64, f64),
    step: (f64, f64),
    shift_hz: f64,
    renorm: u32,
}

impl Ddc {
    /// `None` unless `rate` is a whole multiple of 48 kHz.
    pub fn new(rate: u32) -> Option<Ddc> {
        if rate == 0 || !rate.is_multiple_of(48_000) {
            return None;
        }
        let decim = (rate / 48_000) as usize;
        let (coarse, fine) = if decim >= 4 && decim.is_multiple_of(2) {
            let first = decim / 2;
            // Everything that would fold into the channel lies above 72 kHz.
            (Some(Fir::new(first, design(rate, 12 * first + 1, 48_000.0))), Fir::new(2, design(96_000, 129, FINE_CUTOFF_HZ)))
        } else {
            let len = if decim == 1 { 1 } else { 64 * decim + 1 };
            (None, Fir::new(decim, design(rate, len, FINE_CUTOFF_HZ)))
        };
        Some(Ddc { rate, coarse, fine, osc: (1.0, 0.0), step: (1.0, 0.0), shift_hz: 0.0, renorm: 0 })
    }

    pub fn rate(&self) -> u32 {
        self.rate
    }

    /// Mix `iq` (interleaved) down by `shift_hz`, after conjugating if `mirror`, and
    /// append 48 kHz interleaved I/Q to `out`.
    pub fn process(&mut self, iq: &[f32], shift_hz: f64, mirror: bool, out: &mut Vec<f32>) {
        if shift_hz != self.shift_hz || self.renorm == 0 {
            self.shift_hz = shift_hz;
            let w = -2.0 * std::f64::consts::PI * shift_hz / f64::from(self.rate);
            self.step = (w.cos(), w.sin());
        }
        let sign = if mirror { -1.0f64 } else { 1.0 };
        for s in iq.chunks_exact(2) {
            let (i, q) = (f64::from(s[0]), sign * f64::from(s[1]));
            let (c, d) = self.osc;
            let (a, b) = (i * c - q * d, i * d + q * c);
            self.osc = (c * self.step.0 - d * self.step.1, c * self.step.1 + d * self.step.0);
            self.renorm = self.renorm.wrapping_add(1);
            if self.renorm & 0xFFFF == 0 {
                let m = (self.osc.0 * self.osc.0 + self.osc.1 * self.osc.1).sqrt();
                self.osc = (self.osc.0 / m, self.osc.1 / m);
            }
            let sample = match self.coarse.as_mut() {
                Some(f) => f.push(a as f32, b as f32),
                None => Some((a as f32, b as f32)),
            };
            if let Some((x, y)) = sample
                && let Some((p, r)) = self.fine.push(x, y)
            {
                out.push(p);
                out.push(r);
            }
        }
    }
}

fn dot(a: &[f32], b: &[f32]) -> f32 {
    a.iter().zip(b).map(|(x, y)| x * y).sum()
}

/// Kaiser-windowed low-pass with unity DC gain.
fn design(rate: u32, len: usize, cutoff_hz: f64) -> Vec<f32> {
    if len == 1 {
        return vec![1.0];
    }
    let fc = cutoff_hz / f64::from(rate);
    let beta = 9.0;
    let mid = (len - 1) as f64 / 2.0;
    let mut h: Vec<f64> = (0..len)
        .map(|k| {
            let t = k as f64 - mid;
            let sinc = if t == 0.0 { 2.0 * fc } else { (2.0 * std::f64::consts::PI * fc * t).sin() / (std::f64::consts::PI * t) };
            let r = t / mid;
            sinc * i0(beta * (1.0 - r * r).max(0.0).sqrt()) / i0(beta)
        })
        .collect();
    let sum: f64 = h.iter().sum();
    h.iter_mut().for_each(|v| *v /= sum);
    h.iter().map(|&v| v as f32).collect()
}

fn i0(x: f64) -> f64 {
    let (mut sum, mut term) = (1.0, 1.0);
    for k in 1..40 {
        term *= (x / (2.0 * f64::from(k))).powi(2);
        sum += term;
    }
    sum
}

#[cfg(test)]
mod tests {
    use super::*;

    fn tone(rate: u32, hz: f64, n: usize) -> Vec<f32> {
        let w = 2.0 * std::f64::consts::PI * hz / f64::from(rate);
        (0..n).flat_map(|k| [(w * k as f64).cos() as f32, (w * k as f64).sin() as f32]).collect()
    }

    fn rms(v: &[f32]) -> f32 {
        (v.iter().map(|x| x * x).sum::<f32>() / v.len() as f32).sqrt()
    }

    #[test]
    fn rejects_rates_that_are_not_multiples_of_48k() {
        assert!(Ddc::new(44_100).is_none());
        assert!(Ddc::new(0).is_none());
    }

    #[test]
    fn moves_a_tone_to_dc_at_unity_gain() {
        let mut d = Ddc::new(384_000).unwrap();
        let mut out = Vec::new();
        d.process(&tone(384_000, -15_000.0, 96_000), -15_000.0, false, &mut out);
        let tail = &out[out.len() / 2..];
        assert_eq!(out.len(), 24_000);
        let (i, q): (f32, f32) = (tail.iter().step_by(2).sum(), tail.iter().skip(1).step_by(2).sum());
        let n = tail.len() as f32 / 2.0;
        assert!(((i / n) - 1.0).abs() < 0.01, "I = {}", i / n);
        assert!((q / n).abs() < 0.01, "Q = {}", q / n);
    }

    #[test]
    fn mirror_conjugates_before_mixing() {
        let mut d = Ddc::new(96_000).unwrap();
        let mut out = Vec::new();
        d.process(&tone(96_000, 5_000.0, 48_000), -5_000.0, true, &mut out);
        let tail = &out[out.len() / 2..];
        let i: f32 = tail.iter().step_by(2).sum::<f32>() / (tail.len() as f32 / 2.0);
        assert!((i - 1.0).abs() < 0.01, "I = {i}");
    }

    #[test]
    fn rejects_a_signal_outside_the_channel() {
        let mut d = Ddc::new(384_000).unwrap();
        let mut out = Vec::new();
        d.process(&tone(384_000, 60_000.0, 96_000), 0.0, false, &mut out);
        assert!(rms(&out[out.len() / 2..]) < 1e-3, "rms {}", rms(&out[out.len() / 2..]));
    }

    #[test]
    fn passes_the_edge_of_a_20_khz_channel() {
        let mut d = Ddc::new(384_000).unwrap();
        let mut out = Vec::new();
        d.process(&tone(384_000, 10_000.0, 96_000), 0.0, false, &mut out);
        let got = rms(&out[out.len() / 2..]) * std::f32::consts::SQRT_2;
        assert!((got - 1.0).abs() < 0.03, "gain {got}");
    }

    #[test]
    fn rejects_what_would_fold_into_the_channel() {
        for (rate, hz) in [(384_000, 90_000.0), (192_000, 85_000.0), (96_000, 40_000.0), (768_000, 91_000.0)] {
            let mut d = Ddc::new(rate).unwrap();
            let mut out = Vec::new();
            d.process(&tone(rate, hz, rate as usize / 2), 0.0, false, &mut out);
            let level = rms(&out[out.len() / 2..]);
            assert!(level < 1e-3, "{rate} Hz, tone at {hz}: rms {level}");
        }
    }

    #[test]
    fn splits_into_any_block_sizes() {
        let x = tone(192_000, 3_000.0, 40_000);
        let (mut a, mut b) = (Ddc::new(192_000).unwrap(), Ddc::new(192_000).unwrap());
        let (mut oa, mut ob) = (Vec::new(), Vec::new());
        a.process(&x, 1_000.0, false, &mut oa);
        for c in x.chunks(476) {
            b.process(c, 1_000.0, false, &mut ob);
        }
        assert_eq!(oa, ob);
    }
}
