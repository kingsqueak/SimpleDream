//! Feed a raw TCI capture (interleaved f32 I/Q) through the bridge, silently, at full speed.
//! replay <file> <rate> <shift_hz> <mirror 0|1>
use simpledream_native::rx::{Config, Receiver};
use std::time::Duration;

fn main() {
    let a: Vec<String> = std::env::args().collect();
    let bytes = std::fs::read(&a[1]).unwrap();
    let (rate, shift, mirror) = (a[2].parse().unwrap(), a[3].parse().unwrap(), a[4] == "1");
    let iq: Vec<f32> = bytes.chunks_exact(4).map(|b| f32::from_le_bytes(b.try_into().unwrap())).collect();
    let r = Receiver::new(Config { play: false, output_device: None, volume: 1.0, data_dir: None });
    for c in iq.chunks(4096) {
        r.push_iq(c, rate, shift, mirror);
        std::thread::sleep(Duration::from_micros(150));
    }
    std::thread::sleep(Duration::from_secs(3));
    let s: serde_json::Value = serde_json::from_str(&r.status()).unwrap();
    let keys = ["state", "mode", "occupancy_khz", "snr_db", "needs_snr_db", "msc_mode", "fac_ok", "fac_bad", "msc_ok", "msc_bad", "audio_ok", "audio_bad", "codec", "dropped", "time_s", "inverted", "level_dbfs"];
    for k in keys {
        println!("{k}: {}", s[k]);
    }
    println!("services: {}", s["services"]);
    println!("spectrum bins: {}, span {}", s["spectrum"]["db"].as_array().map_or(0, Vec::len), s["spectrum"]["span_hz"]);
}
