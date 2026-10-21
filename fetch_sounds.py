"""Reproduce FlightGuard v0.7 recorded sound packs from pinned upstream sources."""
import pathlib,subprocess,sys
here=pathlib.Path(__file__).resolve().parent
if len(sys.argv)!=2:raise SystemExit('Usage: python fetch_sounds.py <app-directory>')
for script in ['fetch_base_sounds.py','expand_sounds.py','fetch_model_sounds.py','organize_model_packs.py','finish_sounds.py','fetch_cabin_sounds.py','fetch_terrain_alerts.py','fetch_voice_expansion.py','build_pack_catalog.py']:
 subprocess.run([sys.executable,str(here/script),sys.argv[1]],check=True)
