# 0.7.0 — Aircraft voices and Voice Studio

- Add per-aircraft GPWS/co-pilot/cabin routing and independent levels.
- Add background voice inventory, source/licence/hash and duration details, A/B compare, search/filters and coverage CSV export.
- Add validated personal WAV mapping/metadata import, backups/undo and template export; block imports while connected/replaying.
- Add editable/suggested height schedules including optional 5 ft, and routine crew quieting on final.
- Add 72 pinned model mappings: 269 total, 17 repair packs and 266 downloadable originals. Identify identical source bytes rather than assuming unique OEM voices.
- Avoid automatic A320 routing for A220; preserve labelled fallbacks and disclose incomplete 757/A220/A350 coverage.
- 160 logic checks plus full voice inventory, 269 audio, native priority and isolated replay checks.

# 0.6.0 — Earlier, context-aware terrain and configuration warnings

- Add GPS-based forward terrain monitoring with a cached elevation corridor, configurable 60-second horizon, validity checks and explicit unavailable status.
- Add recorded terrain-ahead and terrain-ahead/pull-up audio from pinned LGPL-3.0 FlightAssistant originals, with reproducible conversion and licences.
- Raise default gear/flap gates to 800/500 ft; use aircraft flap angles, travel grace and approach/departure history.
- Keep one fresh warning repeat while audio plays; discard it on resolution. Independent persistence timers prevent marginal conditions from delaying another warning.
- Reject isolated AGL spikes and inconsistent terrain closure; show and record warning reasons and forward assessments.
- Replay stays offline, with recorded terrain assessments; 141 logic checks, 197 audio decodes, native priority, 903-frame replay and real terrain-tile checks passed.
- Live flight testing deferred at the user's request.

# Changelog

## 0.5.0 — 21 October 2026

Added parked-gate boarding, welcome/safety, taxi ambience, descent/landing preparation and arrival/deboarding recordings. The cabin monitor uses ground speed, brake changes and flight history. Connecting airborne skips boarding; general aviation requires opt-in. Controls support a new parked leg and completing boarding. Cabin level is independent of the main volume.

GPWS/cockpit calls preempt cabin audio. Phase changes remove obsolete announcements. Cabin is silent during takeoff/go-around and below 1,500 ft airborne. Cabin-stop and preview controls preserve active warnings. Brief warning-clear gaps no longer cut the warning immediately.

Polling targets 50 ms. Audio paths are cached and coverage is no longer recalculated from the filesystem each frame. Playback completion wakes the queue immediately. Network and acoustic latency remain outside a timing guarantee.

Added 11 original shared cabin clips, source hashes/licences, a downloadable pack, an original logo/icon/banner and sadiq_ibraheem credit. Total: 195 WAV mappings and 194 downloadable originals in 12 packs. OEM/airline sets and windshear/spoken go-around clips remain incomplete.

Validation: 109 automated checks, 195 audio files, 11 actual cabin downloads, isolated 903-frame replay, 12-sample 777-200ER ground check averaging 52.59 ms. Full gate-to-gate operation still needs flight testing.

## 0.4.0

Per-aircraft settings, recording/replay, approach monitoring, landing reports, sound-pack downloads and deployment/deceleration calls.
