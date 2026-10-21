from pathlib import Path
import json,urllib.request,urllib.parse,concurrent.futures,hashlib,shutil,wave,struct,sys

root=Path(sys.argv[1]).resolve();root.mkdir(exist_ok=True,parents=True)
old=root
records=json.loads((old/'sound-provenance.json').read_text())
sources={
 'FBW':('flybywiresim/a32nx-wwise','b4c58ff3875ef00b61a680ccbb35acc4bcebc2ef','CC-BY-NC-SA-4.0','LICENSE'),
 'EJets':('YV3399/E-jet-family-YV','1338d8f4a3cb884171f7136488e069e219d06611','GPL-2.0-or-later','License.txt'),
 'CRJ':('YoRyan/FlightGear-CRJ700-family','2a999a31c256b0966a1cca8be41887a918d1cc5e','GPL-2.0-or-later','Docs/License-GPL-2.txt'),
 '737':('omega13a/737-Next-Generation','f0cbdc58213a4a29e3a7b48d87dc8bdabab96458','GPL-2.0','LICENSE')}
shared={
 'retard':('FBW','retard.wav'),
 'v1':('FBW','v1-1.wav'),'rotate':('FBW','rotate1.wav'),'v2':('EJets','Sounds/Cockpit/v2.wav'),
 '80-knots':('CRJ','Sounds/80kt.wav'),'100-knots':('FBW','100kt1.wav'),'positive-rate':('FBW','positiverate1.wav'),
 'gear-up':('FBW','gearup (1).wav'),'gear-down':('FBW','geardown (1).wav'),'gear-locked':('FBW','gearlocked.wav'),
 'spoilers-armed':('FBW','SpoilersARM.wav'),'spoilers-disarmed':('FBW','spoilersDISARM.wav'),'spoilers-deployed':('FBW','spoilers1.wav'),
 'parking-brake-set':('FBW','parkbrk.wav'),'parking-brake-released':('FBW','parkbrk2.wav'),
 'flaps-up':('FBW','flap0(1).wav'),'flaps-set':('FBW','flapnotch.wav'),
 '70-knots':('FBW','70c.wav'),'60-knots':('FBW','60c.wav'),
 'thrust-set':('FBW','thrustset1.wav'),'reverse-green':('FBW','revgreen1.wav'),'deceleration':('FBW','decel1.wav'),
 'clear-of-conflict':('FBW','ClearOfConflict.wav'),'seatbelts-on':('FBW','seatbelt.wav'),
 'before-takeoff':('FBW','CAPTAIN-CABINCREW-SEATS-FOR-TAKEOFF.wav'),
 'after-landing':('FBW','A_After landing.wav')}
generic={
 'stall':('737','Sounds/stall.wav'),'overspeed':('737','Sounds/overspeed.wav'),
 'engine-fire':('737','Sounds/fire-bell.wav'),'autopilot-disconnect':('737','Sounds/Apdisco.wav'),
 'landing':('737','Sounds/wow.wav'), 'master-caution':('FBW','mastercaution.wav'),
 'master-warning':('FBW','masterwarning.wav'),'traffic':('FBW','TrafficTraffic.wav'),
 'climb':('FBW','ClimbClimb.wav'),'descend':('FBW','DescendDescend.wav'),
 'altitude-2000':('FBW','2000.wav')}
regional={
 'Airbus':{'70-knots':('FBW','70c.wav'),'100-knots':('FBW','100KTS.wav')},
 'Embraer':{k:('EJets','Sounds/Cockpit/'+v+'.wav') for k,v in [('80-knots','80kt'),('v1','v1'),('rotate','vr'),('v2','v2')]},
 'Bombardier':{k:('CRJ','Sounds/'+v+'.wav') for k,v in [('80-knots','80kt'),('v1','v1'),('rotate','vr'),('v2','v2'),('autopilot-disconnect','autopilot-disconnect'),('overspeed','overspeed')]},
 'Boeing':{k:('737','Sounds/'+v+'.wav') for k,v in [('autopilot-disconnect','Apdisco'),('overspeed','overspeed'),('stall','stall'),('engine-fire','fire-bell')]}}
regional['Embraer']['autopilot-disconnect']=('EJets','Sounds/APdisco.wav')
jobs=[]
for family,mapping in {'Shared Crew':shared,'Generic':generic,**regional}.items():
 for cue,(source,path) in mapping.items():
  repo,rev,license,license_path=sources[source]
  if source=='FBW':path='sound/Originals/SFX/'+path
  url=f'https://raw.githubusercontent.com/{repo}/{rev}/'+urllib.parse.quote(path)
  jobs.append((family,cue,source,path,url))
def fetch(job):
 family,cue,source,path,url=job;repo,rev,license,license_path=sources[source]
 output=root/'Sounds'/family/(cue+'.wav');output.parent.mkdir(parents=True,exist_ok=True)
 original=root/'SoundSources'/source/path;original.parent.mkdir(parents=True,exist_ok=True)
 data=original.read_bytes() if original.exists() else urllib.request.urlopen(urllib.request.Request(url,headers={'User-Agent':'FlightGuard/0.2'}),timeout=40).read()
 if data[:4]!=b'RIFF' or data[8:12]!=b'WAVE' or len(data)<512:raise ValueError('Empty/invalid sound '+str(job))
 original.write_bytes(data);output.write_bytes(data)
 return dict(family=family,cue=cue,source_url=url,original_path=path,revision=rev,license=license,bytes=len(data),sha256=hashlib.sha256(data).hexdigest())
with concurrent.futures.ThreadPoolExecutor(6) as pool:
 for item in pool.map(fetch,jobs):
  records=[r for r in records if (r['family'],r['cue'])!=(item['family'],item['cue'])];records.append(item);print(item['family'],item['cue'],item['bytes'],flush=True)
for source,(repo,rev,license,lp) in sources.items():
 folder=root/'SoundSources'/source;folder.mkdir(exist_ok=True,parents=True)
 for name,path in [('LICENSE.txt',lp),('UPSTREAM-README.md','README.md')]+([('COPYING.md','COPYING.md')] if source=='EJets' else [('LICENSE-APPLICABILITY.txt','LICENSE')] if source=='CRJ' else []):
  try:(folder/name).write_bytes(urllib.request.urlopen(f'https://raw.githubusercontent.com/{repo}/{rev}/'+path,timeout=25).read())
  except Exception as e:print(source,name,e)
 (folder/'ATTRIBUTION.md').write_text(f'# {source} recordings\n\nUpstream: https://github.com/{repo}\nPinned revision: {rev}\nLicense: {license}\n\nCopyright remains with the upstream authors and sound contributors. E-Jet sound credits include HanselVB. CRJ sound credits are in the upstream documentation. FlyByWire audio is noncommercial and share-alike. Original bytes are preserved; runtime playback trims silence, downmixes and applies gain. Shared Crew recordings are shared actors/effects, not manufacturer-specific recordings.\n',encoding='utf-8')
(root/'sound-provenance.json').write_text(json.dumps(records,indent=2))
print('Assets ready',len(records),'WAV mappings')
