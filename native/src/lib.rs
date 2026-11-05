//! C API between the SimpleDream app and DecDRM. Strings are UTF-8 JSON. Returned strings
//! belong to the caller and are released with `sd_string_free`.

#![recursion_limit = "512"]

pub mod ddc;
pub mod rx;
pub mod schedule;

use rx::{Config, Receiver};
use serde_json::{Value, json};
use std::ffi::{CStr, CString, c_char, c_void};
use std::path::PathBuf;

fn text(p: *const c_char) -> String {
    if p.is_null() {
        return String::new();
    }
    // SAFETY: the caller passes a NUL-terminated string that lives for the call.
    unsafe { CStr::from_ptr(p) }.to_string_lossy().into_owned()
}

fn give(s: String) -> *mut c_char {
    CString::new(s.replace('\0', "")).map_or(std::ptr::null_mut(), CString::into_raw)
}

/// Release a string returned by this library.
///
/// # Safety
/// `p` must come from this library, or be null, and is not used again.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn sd_string_free(p: *mut c_char) {
    if !p.is_null() {
        // SAFETY: made by `CString::into_raw` in `give`.
        drop(unsafe { CString::from_raw(p) });
    }
}

/// Start a receiver. `config` is `{"play":bool,"output_device":str,"volume":gain,"data_dir":str}`.
#[unsafe(no_mangle)]
pub extern "C" fn sd_new(config: *const c_char) -> *mut c_void {
    let c: Value = serde_json::from_str(&text(config)).unwrap_or(json!({}));
    let cfg = Config {
        play: c.get("play").and_then(Value::as_bool).unwrap_or(true),
        output_device: c.get("output_device").and_then(Value::as_str).filter(|s| !s.is_empty()).map(String::from),
        volume: c.get("volume").and_then(Value::as_f64).unwrap_or(1.0) as f32,
        data_dir: c.get("data_dir").and_then(Value::as_str).filter(|s| !s.is_empty()).map(PathBuf::from),
    };
    Box::into_raw(Box::new(Receiver::new(cfg))).cast()
}

/// Stop a receiver.
///
/// # Safety
/// `h` comes from `sd_new` and is not used again.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn sd_free(h: *mut c_void) {
    if !h.is_null() {
        // SAFETY: made by `Box::into_raw` in `sd_new`.
        drop(unsafe { Box::from_raw(h.cast::<Receiver>()) });
    }
}

/// Queue `pairs` interleaved float I/Q pairs. The wanted channel sits `shift_hz` from the
/// stream's centre; `mirror` conjugates first. Never blocks.
///
/// # Safety
/// `h` is live and `iq` holds `2 * pairs` floats.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn sd_push_iq(h: *mut c_void, iq: *const f32, pairs: i32, rate: i32, shift_hz: f64, mirror: i32) {
    if h.is_null() || iq.is_null() || pairs <= 0 || rate <= 0 {
        return;
    }
    // SAFETY: as documented above.
    let (r, data) = unsafe { (&*h.cast::<Receiver>(), std::slice::from_raw_parts(iq, pairs as usize * 2)) };
    r.push_iq(data, rate as u32, shift_hz, mirror != 0);
}

/// Send a JSON command, for example `{"cmd":"new_station"}`.
///
/// # Safety
/// `h` is live.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn sd_command(h: *mut c_void, cmd: *const c_char) {
    if h.is_null() {
        return;
    }
    if let Ok(v) = serde_json::from_str(&text(cmd)) {
        // SAFETY: `h` is live.
        unsafe { &*h.cast::<Receiver>() }.command(v);
    }
}

/// The latest status as JSON.
///
/// # Safety
/// `h` is live.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn sd_status(h: *mut c_void) -> *mut c_char {
    if h.is_null() {
        return std::ptr::null_mut();
    }
    // SAFETY: `h` is live.
    give(unsafe { &*h.cast::<Receiver>() }.status())
}

/// Events since the last call, as a JSON array.
///
/// # Safety
/// `h` is live.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn sd_events(h: *mut c_void) -> *mut c_char {
    if h.is_null() {
        return std::ptr::null_mut();
    }
    // SAFETY: `h` is live.
    give(json!(unsafe { &*h.cast::<Receiver>() }.take_events()).to_string())
}

/// Sound-card outputs as a JSON array of names.
#[unsafe(no_mangle)]
pub extern "C" fn sd_output_devices() -> *mut c_char {
    let names: Vec<String> = decdrm_io::list_output_devices().map(|d| d.into_iter().map(|i| i.name).collect()).unwrap_or_default();
    give(json!(names).to_string())
}

/// What to download for the broadcast list.
#[unsafe(no_mangle)]
pub extern "C" fn sd_schedule_sources(dir: *const c_char) -> *mut c_char {
    give(schedule::sources_json(&PathBuf::from(text(dir))).to_string())
}

/// The merged broadcast list from the files in `dir`.
#[unsafe(no_mangle)]
pub extern "C" fn sd_schedule(dir: *const c_char) -> *mut c_char {
    give(schedule::schedule_json(&PathBuf::from(text(dir))).to_string())
}
