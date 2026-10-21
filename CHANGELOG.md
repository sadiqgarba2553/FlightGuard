# Changelog

## 0.5.0 — 21 October 2026

Added parked-gate boarding, welcome/safety, taxi ambience, descent/landing preparation and arrival/deboarding recordings. The cabin monitor uses ground speed, brake changes and flight history. Connecting airborne skips boarding; general aviation requires opt-in. Controls support a new parked leg and completing boarding. Cabin level is independent of the main volume.

GPWS/cockpit calls preempt cabin audio. Phase changes remove obsolete announcements. Cabin is silent during takeoff/go-around and below 1,500 ft airborne. Cabin-stop and preview controls preserve active warnings. Brief warning-clear gaps no longer cut the warning immediately.

Polling targets 50 ms. Audio paths are cached and coverage is no longer recalculated from the filesystem each frame. Playback completion wakes the queue immediately. Network and acoustic latency remain outside a timing guarantee.

Added 11 original shared cabin clips, source hashes/licences, a downloadable pack, an original logo/icon/banner and sadiq_ibraheem credit. Total: 195 WAV mappings and 194 downloadable originals in 12 packs. OEM/airline sets and windshear/spoken go-around clips remain incomplete.

Validation: 109 automated checks, 195 audio files, 11 actual cabin downloads, isolated 903-frame replay, 12-sample 777-200ER ground check averaging 52.59 ms. Full gate-to-gate operation still needs flight testing.

## 0.4.0

Per-aircraft settings, recording/replay, approach monitoring, landing reports, sound-pack downloads and deployment/deceleration calls.
