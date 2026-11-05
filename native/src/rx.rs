//! The receiver: a worker thread that owns the DSP. IQ arrives from the app's TCI client,
//! goes through the down-converter into a DecDRM session, and status comes back as JSON.

use crate::ddc::Ddc;
use crossbeam_channel::{Receiver as Rx, Sender, TrySendError, bounded};
use decdrm_data::DataEvent;
use decdrm_data::journaline::{JournalineBrowser, NmlBody};
use decdrm_engine::audio_out::AudioOut;
use decdrm_engine::{InputFormat, ReceiverConfig, Session, SessionEvent, afs};
use serde_json::{Value, json};
use std::path::PathBuf;
use std::sync::atomic::{AtomicU64, Ordering};
use std::sync::{Arc, Mutex};
use std::thread::JoinHandle;
use std::time::{Duration, Instant};

const PUBLISH: Duration = Duration::from_millis(100);
/// About 2 s of IQ in TCI-sized packets; beyond that the UI thread has stalled badly.
const QUEUE: usize = 8192;
const SPECTRUM_BINS: usize = 384;
const LOG_LINES: usize = 200;

enum Msg {
    Iq { data: Vec<f32>, rate: u32, shift_hz: f64, mirror: bool },
    Cmd(Value),
    Stop,
}

#[derive(Default)]
struct Shared {
    status: Mutex<String>,
    events: Mutex<Vec<Value>>,
    dropped: AtomicU64,
}

pub struct Receiver {
    tx: Sender<Msg>,
    shared: Arc<Shared>,
    worker: Option<JoinHandle<()>>,
}

pub struct Config {
    pub play: bool,
    pub output_device: Option<String>,
    pub volume: f32,
    pub data_dir: Option<PathBuf>,
}

impl Receiver {
    pub fn new(cfg: Config) -> Receiver {
        let (tx, rx) = bounded(QUEUE);
        let shared = Arc::new(Shared::default());
        let s = shared.clone();
        let worker = std::thread::Builder::new().name("simpledream-rx".into()).spawn(move || Worker::new(cfg, s).run(&rx)).expect("thread");
        Receiver { tx, shared, worker: Some(worker) }
    }

    /// Queue IQ. If the worker has fallen far behind the packet is counted and dropped:
    /// the network thread must never block.
    pub fn push_iq(&self, data: &[f32], rate: u32, shift_hz: f64, mirror: bool) {
        match self.tx.try_send(Msg::Iq { data: data.to_vec(), rate, shift_hz, mirror }) {
            Ok(()) => {}
            Err(TrySendError::Full(_)) | Err(TrySendError::Disconnected(_)) => {
                self.shared.dropped.fetch_add(1, Ordering::Relaxed);
            }
        }
    }

    pub fn command(&self, cmd: Value) {
        let _ = self.tx.send(Msg::Cmd(cmd));
    }

    pub fn status(&self) -> String {
        self.shared.status.lock().map(|s| s.clone()).unwrap_or_default()
    }

    pub fn take_events(&self) -> Vec<Value> {
        self.shared.events.lock().map(|mut e| std::mem::take(&mut *e)).unwrap_or_default()
    }
}

impl Drop for Receiver {
    fn drop(&mut self) {
        let _ = self.tx.send(Msg::Stop);
        if let Some(h) = self.worker.take() {
            let _ = h.join();
        }
    }
}

struct Worker {
    shared: Arc<Shared>,
    session: Session,
    audio: Option<AudioOut>,
    play: bool,
    volume: f32,
    ddc: Option<Ddc>,
    pcm: Vec<f32>,
    text: Option<String>,
    journaline: JournalineBrowser,
    slides_dir: Option<PathBuf>,
    slide_no: u64,
    log: Vec<String>,
    rate_error: Option<u32>,
    last_seq: u64,
    level_dbfs: Option<f32>,
    /// Decoded audio rate and channels, for the status line.
    pcm_format: Option<(u32, u16)>,
}

fn receiver_config() -> ReceiverConfig {
    ReceiverConfig { input: InputFormat::Iq { swap: false }, channels: 2, ..ReceiverConfig::default() }
}

impl Worker {
    fn new(cfg: Config, shared: Arc<Shared>) -> Worker {
        let mut log = Vec::new();
        let audio = open_audio(cfg.play, cfg.output_device, cfg.volume, &mut log);
        let slides_dir = cfg.data_dir.map(|d| d.join("slides"));
        if let Some(d) = &slides_dir {
            // Slides are a cache for the running session.
            let _ = std::fs::remove_dir_all(d);
            let _ = std::fs::create_dir_all(d);
        }
        Worker {
            shared,
            session: Session::new(receiver_config()),
            audio,
            play: cfg.play,
            volume: cfg.volume,
            ddc: None,
            pcm: Vec::new(),
            text: None,
            journaline: JournalineBrowser::new(),
            slides_dir,
            slide_no: 0,
            log,
            rate_error: None,
            last_seq: 0,
            level_dbfs: None,
            pcm_format: None,
        }
    }

    fn run(mut self, rx: &Rx<Msg>) {
        let mut last = Instant::now() - PUBLISH;
        loop {
            match rx.recv_timeout(PUBLISH) {
                Ok(Msg::Iq { data, rate, shift_hz, mirror }) => {
                    self.on_iq(&data, rate, shift_hz, mirror);
                    // Work through a backlog in one go so the decoder gets larger blocks.
                    while self.pcm.len() < 9600 {
                        match rx.try_recv() {
                            Ok(Msg::Iq { data, rate, shift_hz, mirror }) => self.on_iq(&data, rate, shift_hz, mirror),
                            Ok(Msg::Cmd(c)) => self.on_command(&c),
                            Ok(Msg::Stop) => return self.finish(),
                            Err(_) => break,
                        }
                    }
                    self.decode();
                }
                Ok(Msg::Cmd(c)) => self.on_command(&c),
                Ok(Msg::Stop) => return self.finish(),
                Err(crossbeam_channel::RecvTimeoutError::Timeout) => {}
                Err(crossbeam_channel::RecvTimeoutError::Disconnected) => return self.finish(),
            }
            if last.elapsed() >= PUBLISH {
                self.publish();
                last = Instant::now();
            }
        }
    }

    fn finish(mut self) {
        self.decode();
        if let Some(a) = self.audio.as_mut() {
            let _ = a.stop_recording();
        }
    }

    fn on_iq(&mut self, data: &[f32], rate: u32, shift_hz: f64, mirror: bool) {
        if self.ddc.as_ref().is_none_or(|d| d.rate() != rate) {
            self.ddc = Ddc::new(rate);
            if self.ddc.is_none() {
                if self.rate_error != Some(rate) {
                    self.rate_error = Some(rate);
                    self.note(format!("IQ rate {rate} Hz is not a multiple of 48 kHz"));
                }
                return;
            }
            self.rate_error = None;
            self.note(format!("IQ at {rate} Hz"));
        }
        if let Some(d) = self.ddc.as_mut() {
            d.process(data, shift_hz, mirror, &mut self.pcm);
        }
    }

    fn decode(&mut self) {
        if self.pcm.is_empty() {
            return;
        }
        let frames = std::mem::take(&mut self.pcm);
        let rms = (frames.iter().map(|v| v * v).sum::<f32>() / frames.len() as f32).sqrt();
        self.level_dbfs = Some(20.0 * rms.max(1e-9).log10());
        for ev in self.session.push(&frames) {
            self.on_event(ev);
        }
    }

    fn on_event(&mut self, ev: SessionEvent) {
        match ev {
            SessionEvent::Log(l) => self.note(l),
            SessionEvent::Audio(pcm) => {
                self.pcm_format = Some((pcm.sample_rate, pcm.channels));
                if let Some(a) = self.audio.as_mut()
                    && let Err(e) = a.push(&pcm.samples, pcm.sample_rate, usize::from(pcm.channels))
                {
                    let line = format!("audio output error: {e:#}");
                    self.note(line);
                }
            }
            SessionEvent::Text(t) => self.text = t,
            SessionEvent::Data { short_id, event } => self.on_data(short_id, event),
            SessionEvent::ServicesChanged => {}
        }
    }

    fn on_data(&mut self, short_id: u8, event: DataEvent) {
        match event {
            DataEvent::SlideShowImage { name, mime, data, .. } => {
                let Some(dir) = &self.slides_dir else { return };
                let ext = if mime.contains("png") { "png" } else { "jpg" };
                self.slide_no += 1;
                let path = dir.join(format!("slide-{}.{ext}", self.slide_no));
                if std::fs::write(&path, data).is_ok() {
                    self.event(json!({"type": "slide", "service": short_id, "name": name, "path": path}));
                }
            }
            DataEvent::Journaline(update) => {
                self.journaline.apply(&update);
            }
            _ => {}
        }
    }

    fn event(&self, v: Value) {
        if let Ok(mut e) = self.shared.events.lock() {
            e.push(v);
        }
    }

    fn note(&mut self, line: String) {
        self.log.push(line);
        if self.log.len() > LOG_LINES {
            self.log.remove(0);
        }
    }

    fn on_command(&mut self, c: &Value) {
        let flag = |k: &str| c.get(k).and_then(Value::as_bool).unwrap_or(false);
        match c.get("cmd").and_then(Value::as_str).unwrap_or("") {
            "new_station" => {
                self.session.new_station();
                self.text = None;
                self.journaline = JournalineBrowser::new();
                self.pcm_format = None;
                self.event(json!({"type": "clear"}));
            }
            "restart" => self.session.restart(),
            "service" => {
                if let Some(id) = c.get("id").and_then(Value::as_u64) {
                    self.session.select_service(id as u8);
                    self.text = None;
                }
            }
            "volume" => {
                self.volume = c.get("gain").and_then(Value::as_f64).unwrap_or(1.0) as f32;
                if let Some(a) = self.audio.as_mut() {
                    a.set_volume(self.volume);
                }
            }
            "smooth_sbr" => self.session.set_smooth_sbr(flag("on")),
            "output_device" => {
                let name = c.get("name").and_then(Value::as_str).filter(|s| !s.is_empty()).map(String::from);
                if let Some(mut a) = self.audio.take() {
                    let _ = a.stop_recording();
                }
                let mut log = Vec::new();
                self.audio = open_audio(self.play, name, self.volume, &mut log);
                log.into_iter().for_each(|l| self.note(l));
            }
            "record_start" => {
                if let (Some(a), Some(p)) = (self.audio.as_mut(), c.get("path").and_then(Value::as_str)) {
                    let r = a.start_recording(PathBuf::from(p));
                    if let Err(e) = r {
                        self.note(format!("{e:#}"));
                    }
                }
            }
            "record_stop" => {
                if let Some(a) = self.audio.as_mut() {
                    let r = a.stop_recording();
                    if let Err(e) = r {
                        self.note(format!("{e:#}"));
                    }
                }
            }
            "jl_follow" => {
                if let Some(i) = c.get("index").and_then(Value::as_u64) {
                    self.journaline.follow(i as usize);
                }
            }
            "jl_back" => {
                self.journaline.back();
            }
            "jl_home" => self.journaline.home(),
            _ => {}
        }
    }

    fn publish(&mut self) {
        let dropped = self.shared.dropped.load(Ordering::Relaxed);
        let status = self.status_json(dropped).to_string();
        if let Ok(mut s) = self.shared.status.lock() {
            *s = status;
        }
    }

    fn status_json(&mut self, dropped: u64) -> Value {
        let visuals = self.session.visuals();
        let st = self.session.status().clone();
        let channel = self.session.ensemble().channel().copied();
        let stats = &self.session.audio_stats;
        let msc = self.session.msc_stats;
        let msc_mode = channel.map(|c| qam_label(&format!("{:?}", c.msc_mode)));
        let services: Vec<Value> = self
            .session
            .service_views()
            .iter()
            .map(|s| {
                json!({
                    "id": s.short_id, "label": s.label.trim(), "audio": s.is_audio, "description": s.description,
                    "language": s.language, "programme": s.programme_type, "country": s.country,
                    "decodable": s.decodable, "warning": s.warning, "encrypted": s.ca,
                    "bitrate": s.audio_bitrate,
                    "codec": s.audio.as_ref().map(codec_label),
                    "apps": s.apps.iter().map(|a| a.name.clone()).collect::<Vec<_>>(),
                })
            })
            .collect();
        let rows: Vec<Value> = {
            let new = visuals.spectrum_seq.saturating_sub(self.last_seq) as usize;
            self.last_seq = visuals.spectrum_seq;
            let have = visuals.waterfall_rows.len();
            visuals.waterfall_rows[have - new.min(have)..].iter().map(|r| json!(bins_f32(r))).collect()
        };
        let audio = self.audio.as_ref();
        let (buffer, drift) = audio.and_then(AudioOut::status).map_or((0.0, 0.0), |(b, p)| (b.as_secs_f64() * 1000.0, p));
        let recording = audio.and_then(AudioOut::recording).filter(|r| r.active).map(|r| r.describe());
        let journaline = self.journaline_json();
        json!({
            "state": format!("{:?}", st.state),
            "mode": st.mode.map(|m| format!("{m:?}")),
            "occupancy_khz": st.occupancy.map(|o| o.bandwidth_khz()),
            "inverted": st.inverted,
            "snr_db": st.snr_db, "mer_db": st.mer_db, "wmer_db": st.wmer_db,
            "needs_snr_db": msc_mode.as_deref().map(needed_snr_db),
            "doppler_hz": st.doppler_hz, "delay_ms": st.delay_ms, "sro_hz": st.sro_hz,
            "dc_hz": st.dc_frequency_hz,
            "fac_ok": st.fac_ok, "fac_bad": st.fac_bad, "sdc_ok": st.sdc_ok, "sdc_bad": st.sdc_bad,
            "msc_frames": msc.frames, "msc_ok": msc.ok, "msc_bad": msc.bad,
            "audio_ok": stats.frames_ok, "audio_bad": stats.frames_concealed, "codec": stats.codec,
            "msc_mode": msc_mode,
            "sdc_mode": channel.map(|c| qam_label(&format!("{:?}", c.sdc_mode))),
            "interleaving": channel.map(|c| format!("{:?}", c.interleaving)),
            "services": services,
            "selected": self.session.selected_service(),
            "text": self.text,
            "time_utc": self.session.ensemble().time().map(|t| {
                let (y, m, d) = t.date();
                format!("{y:04}-{m:02}-{d:02} {:02}:{:02} UTC", t.hour, t.minute)
            }),
            "afs": afs::describe(self.session.ensemble().alternative_frequencies(), self.session.ensemble().time()),
            "playing": audio.is_some_and(AudioOut::is_playing),
            "buffer_ms": buffer, "drift_ppm": drift, "recording": recording,
            "pcm_rate": self.pcm_format.map(|f| f.0), "pcm_channels": self.pcm_format.map(|f| f.1),
            "level_dbfs": self.level_dbfs,
            "time_s": self.session.time_s(),
            "dropped": dropped,
            "spectrum": {
                "db": bins_f64(&visuals.spectrum_db),
                "span_hz": visuals.spectrum_span_hz,
                "centre_hz": visuals.spectrum_centre_hz,
                // While searching this is only a guess, so nothing is marked.
                "band_hz": visuals.signal_band_hz.filter(|_| st.state != decdrm_core::rx::RxState::Acquisition).map(|b| [b.0, b.1]),
                "dc_hz": visuals.dc_hz,
            },
            "waterfall": rows,
            "journaline": journaline,
            "log": self.log,
        })
    }

    fn journaline_json(&self) -> Value {
        let Some(page) = self.journaline.current() else {
            return if self.journaline.root().is_some() { json!({"title": "Journaline", "text": "Page not received yet.", "entries": []}) } else { Value::Null };
        };
        let id = self.journaline.current_id();
        let entries: Vec<Value> = self
            .journaline
            .menu_entries(id)
            .iter()
            .map(|e| json!({"text": e.text, "available": e.available}))
            .collect();
        let text = match &page.body {
            NmlBody::PlainText(t) => t.clone(),
            NmlBody::List(items) => items.iter().map(|i| i.text.clone()).collect::<Vec<_>>().join("\n"),
            _ => String::new(),
        };
        json!({"title": page.title, "text": text, "entries": entries, "can_back": self.journaline.path().len() > 1})
    }
}

fn open_audio(play: bool, device: Option<String>, volume: f32, log: &mut Vec<String>) -> Option<AudioOut> {
    match AudioOut::new(play, device, false, None) {
        Ok(mut a) => {
            a.set_volume(volume);
            Some(a)
        }
        Err(e) => {
            log.push(format!("cannot open audio output: {e:#}"));
            None
        }
    }
}

fn qam_label(debug: &str) -> String {
    let d = debug.to_ascii_lowercase();
    if d.contains("64") {
        "64-QAM".into()
    } else if d.contains("16") {
        "16-QAM".into()
    } else if d.contains('4') {
        "4-QAM".into()
    } else {
        debug.to_string()
    }
}

/// Rough SNR a clean channel needs for audio with this modulation, dB. DecDRM measured
/// 14.8 dB for 64-QAM at code rate 0.6; fading channels need more, and the code rate moves
/// it either way, so the app words this as "about".
fn needed_snr_db(label: &str) -> f64 {
    if label.starts_with("64") {
        15.0
    } else if label.starts_with("16") {
        10.0
    } else {
        6.0
    }
}

fn codec_label(a: &decdrm_engine::AudioCodingView) -> String {
    let mut s = if a.codec == "AAC" {
        match (a.sbr, a.parametric_stereo) {
            (true, true) => "HE-AAC v2".to_string(),
            (true, false) => "HE-AAC".to_string(),
            _ => "AAC".to_string(),
        }
    } else {
        a.codec.clone()
    };
    s.push_str(if a.stereo || a.parametric_stereo { " stereo" } else { " mono" });
    s
}

/// Reduce to at most [`SPECTRUM_BINS`], to one decimal.
fn bins_f64(v: &[f64]) -> Vec<f32> {
    let f: Vec<f32> = v.iter().map(|&x| x as f32).collect();
    bins_f32(&f)
}

fn bins_f32(v: &[f32]) -> Vec<f32> {
    if v.is_empty() {
        return Vec::new();
    }
    let n = v.len().min(SPECTRUM_BINS);
    (0..n)
        .map(|k| {
            let (a, b) = (k * v.len() / n, ((k + 1) * v.len() / n).max(k * v.len() / n + 1));
            let m = v[a..b].iter().sum::<f32>() / (b - a) as f32;
            (m * 10.0).round() / 10.0
        })
        .collect()
}
