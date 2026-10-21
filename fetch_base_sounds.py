"""Reproduce the bundled sound collection from pinned, publicly licensed sources.
Run: python fetch_sounds.py <destination-app-directory>
Original WAV bytes are preserved. Each sound pack retains its upstream license.
"""
import concurrent.futures
import hashlib
import json
import pathlib
import sys
import urllib.parse
import urllib.request

FBW = "b4c58ff3875ef00b61a680ccbb35acc4bcebc2ef"
MD11 = "c2edac58c2df507fd0dd0412f8cf38657c6a45b3"
FGDATA = "0e3007cb74eb06c2e8e7c600562e88633b594759"
DEST = pathlib.Path(sys.argv[1]).resolve()
DEST.mkdir(parents=True, exist_ok=True)

airbus = {
    "pull-up": "pullup.wav", "sink-rate": "sinkrate.wav", "dont-sink": "dontsink.wav",
    "too-low-flaps": "too-low-flaps.wav", "too-low-gear": "too-low-gear.wav", "too-low-terrain": "too-low-terrain.wav",
    "minimums": "minimum.wav", "retard": "retard.wav", "stall": "stall.wav",
    "v1": "v1-1.wav", "rotate": "rotate1.wav", "100-knots": "100kt1.wav",
    "positive-rate": "positiverate1.wav", "gear-down": "geardown (1).wav", "gear-up": "gearup (1).wav",
    "spoilers-armed": "SpoilersARM.wav", "spoilers-disarmed": "spoilersDISARM.wav",
    "70-knots": "seventykts1.wav", "thrust-set": "thrustset1.wav", "reverse-green": "revgreen1.wav",
    "deceleration": "decel1.wav", "master-caution": "mastercaution.wav", "master-warning": "masterwarning.wav",
    "traffic": "TrafficTraffic.wav", "climb": "ClimbClimb.wav", "descend": "DescendDescend.wav",
}
for number in [2500, 2000, 1000, 500, 400, 300, 200, 100, 50, 40, 30, 20, 10]:
    airbus[f"altitude-{number}"] = f"{number}.wav"

generic_ids = ["pull-up", "sink-rate", "dont-sink", "terrain", "too-low-gear", "too-low-flaps", "too-low-terrain",
               "bank-angle", "glideslope", "minimums", "altitude-2500", "altitude-1000", "altitude-500", "altitude-400",
               "altitude-300", "altitude-200", "altitude-100", "altitude-50", "altitude-40", "altitude-30", "altitude-20", "altitude-10"]
generic = {name: name + ".wav" for name in generic_ids}
generic["approaching-minimums"] = "100-above.wav"
md = {"altitude-" + str(n): str(n) + ".wav" for n in [2500, 1000, 500, 400, 300, 200, 100, 50, 40, 30, 20, 10]}
md.update({"autopilot-disconnect": "apoff.wav", "approaching-minimums": "approaching-minimum.wav", "minimums": "minimum.wav", "overspeed": "overspeed.wav"})

jobs = []
for family, mapping, repo, revision, folder, license_name in [
    ("Airbus", airbus, "flybywiresim/a32nx-wwise", FBW, "sound/Originals/SFX/", "CC-BY-NC-SA-4.0"),
    ("Generic", generic, "flightgear/fgdata", FGDATA, "Sounds/mk-viii/", "GPL-2.0"),
    ("McDonnell Douglas", md, "Octal450/MD-11", MD11, "Sounds/CAWS/", "GPL-2.0"),
]:
    out = DEST / "Sounds" / family
    out.mkdir(parents=True, exist_ok=True)
    for cue, filename in mapping.items():
        path = folder + filename
        url = (f"https://gitlab.com/{repo}/-/raw/{revision}/" if family == "Generic" else f"https://raw.githubusercontent.com/{repo}/{revision}/") + urllib.parse.quote(path)
        jobs.append((family, cue, url, out / (cue + ".wav"), repo, revision, path, license_name))
    license_url = f"https://gitlab.com/{repo}/-/raw/{revision}/LICENSE.md" if family == "Generic" else f"https://raw.githubusercontent.com/{repo}/{revision}/LICENSE"
    data = urllib.request.urlopen(license_url, timeout=30).read()
    (out / "LICENSE.txt").write_bytes(data)
    attribution = (f"# {family} sound collection\n\nSource: https://{'gitlab.com' if family == 'Generic' else 'github.com'}/{repo}\nRevision: {revision}\n"
                   f"License: {license_name}; see LICENSE.txt.\n\n"
                   "Copyright and attribution remain with the upstream project and its contributors.\n"
                   "WAV bytes are unchanged; files were renamed to the FlightGuard cue IDs. Original WAV files are the preferred editable source form and are included here.\n")
    if family == "Airbus":
        attribution += "\nFlyByWire Simulations A32NX Wwise sound project contributors. Noncommercial use only. Adaptations must use CC BY-NC-SA 4.0.\n"
    if family == "Generic":
        attribution += "\nFlightGear fgdata MK VIII GPWS collection. This is the shared Honeywell-style simulator fallback; it is not a verified recording set for every Boeing model.\n"
    if family == "McDonnell Douglas":
        attribution += "\nOctal450 MD-11 project contributors. CAWS recordings are specific to that simulator project, not a complete certified MD-80/DC-10/MD-11 sound set. Missing warnings use shared recorded GPWS; unavailable clips stay silent.\n"
    (out / "ATTRIBUTION.md").write_text(attribution, encoding="utf-8")

def fetch(job):
    family, cue, url, output, repo, revision, original, license_name = job
    req = urllib.request.Request(url, headers={"User-Agent": "FlightGuard-development/0.1"})
    data = urllib.request.urlopen(req, timeout=45).read()
    if data[:4] != b"RIFF" or data[8:12] != b"WAVE" or len(data) < 512:
        raise ValueError(f"{family}/{cue}: not a WAV (possibly a Git LFS pointer)")
    output.write_bytes(data)
    return {"family": family, "cue": cue, "source_url": url, "original_path": original,
            "revision": revision, "license": license_name, "bytes": len(data), "sha256": hashlib.sha256(data).hexdigest()}

records = []
with concurrent.futures.ThreadPoolExecutor(max_workers=6) as pool:
    for result in pool.map(fetch, jobs):
        records.append(result)
        print(result["family"], result["cue"], result["bytes"], flush=True)
(DEST / "sound-provenance.json").write_text(json.dumps(records, indent=2), encoding="utf-8")
print(f"Downloaded and verified {len(records)} WAV files.")
