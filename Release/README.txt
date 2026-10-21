FLIGHTGUARD 0.6 - WINDOWS x64
Created for Infinite Flight Community member sadiq_ibraheem.

Close older copies, then run FlightGuard.exe. Open START-HERE.html for help.
Same Wi-Fi: active flight, Connect enabled, Discover or enter local IP.
No cloud API key, aircraft-control writes or Windows speech are used.

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
197 recorded WAV mappings, 12 repair packs, 194 direct-download originals.
Two terrain clips converted from retained FlightAssistant OGG originals.
Shared fallback recordings are labelled. All-aircraft exact OEM coverage,
windshear and spoken go-around recordings remain incomplete; no TTS fallback.
141 automated logic checks, 197 audio decodes, native audio priority,
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
