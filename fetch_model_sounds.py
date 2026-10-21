"""Expand model packs from pinned, licensed upstream WAVs; preserve original bytes."""
from pathlib import Path
import concurrent.futures, hashlib, json, sys, urllib.parse, urllib.request

root=Path(sys.argv[1]).resolve()
records=json.loads((root/'sound-provenance.json').read_text(encoding='utf-8'))
sources={
 'B757':('FGMEMBERS/757-200','873a9a12ec1a2451ece37dff3bc1fbc888615042','GPL-2.0','LICENSE'),
 'B747':('FGMEMBERS/747-400','887ff4746c373e7167e664457200165fb6dfaa22','GPL-2.0','COPYING'),
 'B787':('FGMEMBERS/787-8','b79677d148bc7f573c55accb3cb96450b89156a2','GPL-2.0','COPYING'),
 'A330':('FGMEMBERS/A330-200','59a22737e419783523554fe9123191a7eec721ce','GPL-2.0','COPYING'),
 'FBW':('flybywiresim/a32nx-wwise','b4c58ff3875ef00b61a680ccbb35acc4bcebc2ef','CC-BY-NC-SA-4.0','LICENSE'),
}
maps={
 'Boeing 757':('B757',{'autopilot-disconnect':'Sounds/autopilot-disengage.wav','stall':'Sounds/stall.wav','landing':'Sounds/touchdown.wav','seatbelts-on':'Sounds/cabinalert.wav'}),
 'Boeing 747':('B747',{
   **{f'altitude-{n}':f'Sounds/altitude-{n}.wav' for n in [100,50,40,30,20,10]},
   'minimums':'Sounds/minimums.wav','approaching-minimums':'Sounds/approaching-minimums.wav',
   'autopilot-disconnect':'Sounds/autopilot-disengage.wav','takeoff-config':'Sounds/FGOS_Configuration_Warn.wav',
   'stall':'Sounds/shaker.wav','landing':'Sounds/touchdown.wav','seatbelts-on':'Sounds/FastenSeatbelt.wav'}),
 'Boeing 787':('B787',{
   **{f'altitude-{n}':f'Sounds/GPWS/altitude-{n}.wav' for n in [2500,1000,500,400,300,200,100,50,40,30,20,10]},
   **{cue:f'Sounds/GPWS/{cue}.wav' for cue in ['minimums','approaching-minimums','pull-up','terrain','sink-rate','dont-sink','bank-angle','too-low-gear','too-low-flaps','too-low-terrain','glideslope']},
   'v1':'Sounds/v1.wav','rotate':'Sounds/vr.wav','v2':'Sounds/v2.wav','gear-up':'Sounds/gearsup.wav',
   'gear-down':'Sounds/gearsdown.wav','overspeed':'Sounds/Ovspd.wav','engine-fire':'Sounds/fire-warning.wav',
   'master-warning':'Sounds/master-warning.wav','landing':'Sounds/touchdown.wav'}),
 'Airbus A330':('A330',{'v1':'Sounds/res/v1.wav','rotate':'Sounds/res/vr.wav'}),
 'Airbus':('FBW',{'altitude-5':'sound/Originals/SFX/5.wav'}),
 # Replace FlightGear's known synthetic Airbus-extension stubs with existing recorded assets.
 'Generic':('FBW',{'altitude-2500':'sound/Originals/SFX/2500.wav','approaching-minimums':'sound/Originals/SFX/100-above.wav','retard':'sound/Originals/SFX/retard.wav'}),
}
jobs=[(profile,source,cue,path) for profile,(source,mapping) in maps.items() for cue,path in mapping.items()]
def download(job):
 profile,source,cue,path=job;repo,rev,license,lp=sources[source]
 url=f'https://raw.githubusercontent.com/{repo}/{rev}/'+urllib.parse.quote(path)
 original=root/'SoundSources'/source/path;original.parent.mkdir(parents=True,exist_ok=True)
 data=original.read_bytes() if original.exists() else urllib.request.urlopen(urllib.request.Request(url,headers={'User-Agent':'FlightGuard/0.3'}),timeout=35).read()
 if data[:4]!=b'RIFF' or data[8:12]!=b'WAVE' or len(data)<512:raise ValueError(f'Invalid WAV: {url}')
 original.write_bytes(data);dest=root/'Sounds'/profile/(cue+'.wav');dest.parent.mkdir(parents=True,exist_ok=True);dest.write_bytes(data)
 return dict(family=profile,cue=cue,source_url=url,original_path=path,revision=rev,license=license,bytes=len(data),sha256=hashlib.sha256(data).hexdigest(),
             authenticity='Upstream aircraft simulator asset; exact OEM recording origin not independently certified. No new speech generated.')
with concurrent.futures.ThreadPoolExecutor(6) as pool:
 for item in pool.map(download,jobs):
  records=[r for r in records if (r['family'],r['cue'])!=(item['family'],item['cue'])];records.append(item)
for source,(repo,rev,license,lp) in sources.items():
 folder=root/'SoundSources'/source;folder.mkdir(parents=True,exist_ok=True)
 (folder/'LICENSE.txt').write_bytes(urllib.request.urlopen(f'https://raw.githubusercontent.com/{repo}/{rev}/{lp}',timeout=20).read())
 (folder/'ATTRIBUTION.md').write_text(f'# {repo}\n\nPinned revision: {rev}\nLicence: {license}\nOriginal sources: https://github.com/{repo}/tree/{rev}\n\nOriginal WAV files retained. These are upstream simulator assets, not an independently certified OEM voice pack. Copyright remains with the authors/contributors. Shared fallbacks are identified in the app.\n',encoding='utf-8')
 # Preserve author notices when present; exact pinned URLs are retained in the manifest.
 for name in ['README','README.md','AUTHORS']:
  try:(folder/name).write_bytes(urllib.request.urlopen(f'https://raw.githubusercontent.com/{repo}/{rev}/{name}',timeout=10).read())
  except urllib.error.HTTPError as e:
   if e.code!=404:raise
(root/'sound-provenance.json').write_text(json.dumps(records,indent=2),encoding='utf-8')
print(f'{len(jobs)} assets fetched; {len(records)} total mappings')
