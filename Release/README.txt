FLIGHTGUARD 0.7 - WINDOWS x64
Created for Infinite Flight Community member sadiq_ibraheem.

Close older copies, then run FlightGuard.exe. Open START-HERE.html for help.
Same Wi-Fi: active flight, Connect enabled, Discover or enter local IP.
No cloud API key, aircraft-control writes or Windows speech are used.

VOICE STUDIO
Separate GPWS, co-pilot and cabin choices/levels save per aircraft. Browse
models without changing the live aircraft. Apply saves the current flight's
preferences. Library & compare provides A/B previews, source/licence/hash
details, search/filters, coverage export, personal templates and WAV import.
Disconnect before import/undo. Every clip decodes before switching packs;
undo restores the previous pack. Preserve your permitted original recordings.
Height schedules: old schedule, suggested simulator template or custom AGL
heights. 5 ft is now selectable and triggers when a recording is available.
Minimums stay separate. Optional routine crew quieting below 500 ft approach.
Inventory checks run in the background; original source WAVs stay unchanged.
72 model additions cover 777, DC-10, A340, A380, A350 and MD-11. Some are
identical to other packs and are labelled. 757 still has only 4 model clips;
A220 has no verified model set and A350 has only 1. OEM fidelity unverified.

EARLIER WARNINGS
Default gear warning 800 ft AGL; flap warning 500 ft AGL. Both are editable.
Configuration reminders schedule every 1.8 seconds while unresolved. A
playing clip finishes before its fresh queued repeat. More urgent GPWS
preempts lower-priority sound. Travel grace and approach/takeoff/climb/
go-around context improve timing. Severe sink/stall protection remains.

GPWS landing flap STATE: -1 = automatic verified-clean detection; otherwise
choose an aircraft stop INDEX, not degrees. Extension angles are used when
available, including reversed index ordering. Unknown states are not guessed.
Approach-monitor flap settings are separate. Verify/arm V-speeds each flight.

FORWARD TERRAIN
Default 60 seconds (15-90 adjustable), projected along GPS ground track.
Recorded Terrain ahead and Terrain ahead / pull up escalate future conflict.
Tiles download in the background and cache under Data/TerrainCache. Status
shows unavailable data; local terrain closure continues. External elevation
can differ from the game. Forecasts with a >500 ft ground mismatch are
withheld. Buildings, obstacles and runway geometry are not modelled.
Read Terrain-notices/README.md. Simulator approximation, not certified TAWS.

GATE TO GATE
Flight tools > Cabin. Parked brake-set boarding begins after 3 seconds.
Welcome, safety, taxi ambience, takeoff/landing preparation and arrival
follow observed flight history. Manual new-leg and boarding-complete
controls are available. Joining airborne skips boarding. Actual tug,
passengers, doors and runway clearance are not inferred. GPWS interrupts
cabin audio; cabin is quiet on takeoff/go-around and below 1,500 ft airborne.

AUDIO AND CHECKS
269 recorded WAV mappings, 17 repair packs, 266 direct-download originals.
Two terrain clips converted from retained FlightAssistant OGG originals.
Shared fallback recordings are labelled. All-aircraft exact OEM coverage,
windshear and spoken go-around recordings remain incomplete; no TTS fallback.
160 automated logic checks, 269 audio decodes, native audio priority,
903-frame isolated replay (zero TCP/terrain requests) and two actual terrain
tile downloads/decodes passed. Validation/ contains reports. Live flight
testing was deferred by the user. Poll target 50 ms depends on game/Wi-Fi;
software submission timing is not acoustic latency.

LOCAL DATA AND LICENCES
Settings, aircraft profiles, recordings, reports, backups and terrain cache
stay in Data. Online terrain requests include geographic tile coordinates;
no recordings or API key are uploaded. Replay performs no terrain downloads.
Application/branding MIT. Audio has separate GPL, LGPL-3.0 and CC BY-NC-SA
licences. Keep SoundSources, notices and sound-provenance.json. Sound bundle
is not an unrestricted commercial library. OEM authenticity is not certified.
GitHub: https://github.com/sadiqgarba2553/FlightGuard

TIMING VARIABILITY
Native timing validation recorded one 258.6 ms scheduling outlier followed by three passing repeats at 5.3–12.1 ms. This is muted software preemption/submission timing, not acoustic latency; Windows/device scheduling can vary. See Validation/audio-timing-trials.json in the Windows bundle.
