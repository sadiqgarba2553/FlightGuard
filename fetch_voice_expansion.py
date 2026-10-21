"""Additional aircraft-project recordings, pinned originals and explicit licences.
Model provenance is recorded; duplicate source bytes never establish OEM authenticity.
"""
from pathlib import Path
import concurrent.futures,hashlib,json,sys,urllib.request,urllib.parse
root=Path(sys.argv[1]).resolve();rows=json.loads((root/'sound-provenance.json').read_text(encoding='utf-8-sig'))
sources={
 'B777':('https://gitlab.com/sidi762/flightgear-777/-/raw/','b0ce71038c371f1ff1df6817bbc9e11789b550d3','GPL-2.0','LICENSE','https://gitlab.com/sidi762/flightgear-777'),
 'DC10':('https://raw.githubusercontent.com/Octal450/DC-10/','91a84131d95eae60c5c3d7127749386f83ba2e82','GPL-2.0','LICENSE','https://github.com/Octal450/DC-10'),
 'A380':('https://raw.githubusercontent.com/FGMEMBERS/A380/','715e972134a2a708115419d2d301b14e8323922a','GPL-2.0','COPYING','https://github.com/FGMEMBERS/A380'),
 'A340':('https://raw.githubusercontent.com/FGMEMBERS/A340-600/','e47f70357fe68df7b9fd107acddaa9755c75e3e7','GPL-3.0','Documentation/License.txt','https://github.com/FGMEMBERS/A340-600'),
 'A350':('https://raw.githubusercontent.com/FGMEMBERS/A350XWB/','04da937ab1ba0b07dabfd71d91aa33eb38316b90','GPL-2.0','COPYING','https://github.com/FGMEMBERS/A350XWB'),
 'MD11':('https://raw.githubusercontent.com/Octal450/MD-11/','c2edac58c2df507fd0dd0412f8cf38657c6a45b3','GPL-2.0','LICENSE','https://github.com/Octal450/MD-11'),
}
heights=[2500,1000,500,400,300,200,100,50,40,30,20,10]
warnings=['pull-up','terrain','sink-rate','dont-sink','bank-angle','too-low-gear','too-low-flaps','too-low-terrain','glideslope']
maps={
 'Boeing 777':('B777',{**{f'altitude-{h}':f'Sounds/GPWS/altitude-{h}.wav' for h in heights},**{c:f'Sounds/GPWS/{c}.wav' for c in warnings},'minimums':'Sounds/GPWS/minimums.wav','v1':'Sounds/v1.wav','rotate':'Sounds/vr.wav','autopilot-disconnect':'Sounds/autopilot-disengage.wav','stall':'Sounds/shaker.wav','takeoff-config':'Sounds/config-warning.wav','boarding-complete':'Sounds/boardingcomplete.wav','seatbelts-on':'Sounds/cabinalert.wav'}),
 'McDonnell Douglas DC-10':('DC10',{**{f'altitude-{h}':f'Sounds/CAWS/{h}.wav' for h in heights},'minimums':'Sounds/CAWS/minimum.wav','autopilot-disconnect':'Sounds/CAWS/apoff.wav',**{c:f'Sounds/GPWS/{c}.wav' for c in warnings if c!='too-low-gear'}}),
 'McDonnell Douglas MD-11':('MD11',{'too-low-gear':'Sounds/CAWS/landing-gear.wav'}),
 'Airbus A380':('A380',{'v1':'Sounds/a380-V1.wav','retard':'Sounds/retard_01_mono.wav','stall':'Sounds/stall_mono.wav','landing':'Sounds/touchdown_mono.wav'}),
 'Airbus A340':('A340',{**{f'altitude-{h}':f'Sounds/altitude-{h}.wav' for h in heights if h!=2500},'minimums':'Sounds/minimums.wav','approaching-minimums':'Sounds/approaching-minimums.wav','stall':'Sounds/stall.wav','landing':'Sounds/touchdown.wav'}),
 'Airbus A350':('A350',{'autopilot-disconnect':'Sounds/apoff.wav'}),
}
def fetch(job):
 profile,key,cue,path=job;base,revision,licence,notice,repo=sources[key];url=base+revision+'/'+urllib.parse.quote(path);dest=root/'SoundSources'/key/path;dest.parent.mkdir(parents=True,exist_ok=True)
 data=dest.read_bytes() if dest.exists() else urllib.request.urlopen(urllib.request.Request(url,headers={'User-Agent':'FlightGuard/0.7'}),timeout=45).read()
 if data[:4]!=b'RIFF' or data[8:12]!=b'WAVE' or len(data)<512:raise ValueError('Invalid original WAV: '+url)
 dest.write_bytes(data);out=root/'Sounds'/profile/(cue+'.wav');out.parent.mkdir(parents=True,exist_ok=True);out.write_bytes(data)
 return dict(family=profile,cue=cue,source_url=url,original_path=path,revision=revision,license=licence,bytes=len(data),sha256=hashlib.sha256(data).hexdigest(),authenticity='Pinned aircraft-project recording. Source bytes preserved. OEM authenticity unverified; matching hashes in other packs indicate shared recording, not a distinct voice.')
jobs=[(profile,key,cue,path) for profile,(key,mapping) in maps.items() for cue,path in mapping.items()]
with concurrent.futures.ThreadPoolExecutor(6) as pool:
 for item in pool.map(fetch,jobs):rows=[r for r in rows if (r['family'],r['cue'])!=(item['family'],item['cue'])];rows.append(item)
for key,(base,revision,licence,name,repo) in sources.items():
 folder=root/'SoundSources'/key;folder.mkdir(parents=True,exist_ok=True);license_data=urllib.request.urlopen(base+revision+'/'+name,timeout=30).read();(folder/'LICENSE.txt').write_bytes(license_data)
 attribution=f'# {repo}\n\nPinned revision {revision}. Licence {licence}; full upstream text retained in LICENSE.txt. Original WAV bytes retained under their source paths. Copyright remains with upstream contributors. No claim of certified OEM recording origin. Identical assets can also occur in shared packs. No speech is generated.\n'
 (folder/'ATTRIBUTION.md').write_text(attribution,encoding='utf-8')
 for profile,(source,mapping) in maps.items():
  if source==key:
   dest=root/'Sounds'/profile;(dest/'ATTRIBUTION.md').write_text(attribution,encoding='utf-8');(dest/('LICENSE-'+licence+'.txt')).write_bytes(license_data)
 (folder/'LICENSE.txt').read_text(encoding='utf-8',errors='replace')
(root/'sound-provenance.json').write_text(json.dumps(rows,indent=2),encoding='utf-8')
print(f'Added {len(jobs)} model mappings; {len(rows)} total recorded mappings',flush=True)
