//! The broadcast list: EiBi and DRMDX merged, as JSON for the app.

use decdrm_schedule::source::{Source, default_sources, load, load_sources};
use decdrm_schedule::{AirState, Entry, PREVIEW_MIN, UtcTime};
use serde_json::{Value, json};
use std::path::Path;

/// DRM30 only: the DRMDX list also carries VHF DRM+ entries.
const MAX_KHZ: f64 = 30_000.0;

fn sources(dir: &Path) -> Vec<Source> {
    let (list, _) = load_sources(dir);
    if list.is_empty() { default_sources() } else { list }
}

/// What to download: `[{name, title, url, file}]`.
pub fn sources_json(dir: &Path) -> Value {
    let date = UtcTime::now().date();
    let list: Vec<Value> = sources(dir)
        .iter()
        .map(|s| json!({"name": s.name, "title": s.label(), "url": s.url_at(date), "file": s.file_at(date)}))
        .collect();
    json!(list)
}

fn same_broadcast(a: &Entry, b: &Entry) -> bool {
    (a.khz - b.khz).abs() < 0.5 && (i32::from(a.start) - i32::from(b.start)).abs() <= 15 && (i32::from(a.stop) - i32::from(b.stop)).abs() <= 15
}

/// Every DRM entry of every list, sources in order, later duplicates left out.
pub fn merged(lists: Vec<(String, Vec<Entry>)>) -> Vec<(String, Entry)> {
    let mut out: Vec<(String, Entry)> = Vec::new();
    for (name, entries) in lists {
        for e in entries {
            if e.drm && e.khz < MAX_KHZ && !out.iter().any(|(_, o)| same_broadcast(o, &e)) {
                out.push((name.clone(), e));
            }
        }
    }
    out.sort_by(|a, b| a.1.khz.total_cmp(&b.1.khz).then(a.1.start.cmp(&b.1.start)));
    out
}

pub fn schedule_json(dir: &Path) -> Value {
    let now = UtcTime::now();
    let date = now.date();
    let mut lists = Vec::new();
    let mut loaded = Vec::new();
    for s in sources(dir) {
        if let Ok(Some(l)) = load(&s, dir, date) {
            loaded.push(json!({"name": s.label(), "entries": l.schedule.drm_count(), "current": l.copy.current}));
            lists.push((s.label().to_string(), l.schedule.entries));
        }
    }
    let entries: Vec<Value> = merged(lists)
        .iter()
        .map(|(source, e)| {
            let state = match e.state_at(now, PREVIEW_MIN) {
                AirState::OnAir => "on",
                AirState::EndingSoon => "ending",
                AirState::StartingSoon => "soon",
                AirState::Off => "off",
            };
            json!({
                "khz": e.khz, "start": e.start, "stop": e.stop, "days": e.days_label(),
                "station": e.station.replace(" DIGITAL", ""), "language": e.language, "target": e.target,
                "country": e.country, "site": e.site, "kw": e.power_kw, "state": state, "source": source,
                "today": e.runs_on(date) || e.irregular,
            })
        })
        .collect();
    json!({"sources": loaded, "entries": entries})
}

#[cfg(test)]
mod tests {
    use super::*;
    use decdrm_schedule::{Format, parse};

    const EIBI: &str = "kHz:75;Time(UTC):93;Days:59;ITU:49;Station:201;Lng:49;Target:62;Remarks:135;P:35;Start:60;Stop:60;\n6110;1900-2000;;ROU;Radio Romania DIGITAL;D;WEu;t;1;;\n6110;1800-1900;;CHN;China Radio Int.;E;Eu;x;1;;\n";
    const DREAM: &str = "[DRMSchedule]\nStartStopTimeUTC=1900-2000\nDays[SMTWTFS]=1111111\nFrequency=6110\nTarget=Europe\nPower=300\nProgramme=RRI\nLanguage=German\nSite=Tiganesti\nCountry=\n\nStartStopTimeUTC=0000-2400\nDays[SMTWTFS]=1111111\nFrequency=95200\nTarget=Hanover\nPower=0,03\nProgramme=RFmondial\nLanguage=German\nSite=Hanover\nCountry=\n\nStartStopTimeUTC=0000-2400\nDays[SMTWTFS]=1111111\nFrequency=15785\nTarget=Europe\nPower=1\nProgramme=funklust\nLanguage=German\nSite=Erlangen\nCountry=\n";

    #[test]
    fn merges_keeps_drm_drops_duplicates_and_vhf() {
        let a = parse(Format::Eibi, EIBI.as_bytes()).entries;
        let b = parse(Format::Dream, DREAM.as_bytes()).entries;
        let m = merged(vec![("EiBi".into(), a), ("DRMDX".into(), b)]);
        let names: Vec<(&str, f64)> = m.iter().map(|(s, e)| (s.as_str(), e.khz)).collect();
        assert_eq!(names, vec![("EiBi", 6110.0), ("DRMDX", 15785.0)]);
    }
}
