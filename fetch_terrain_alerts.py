"""Reproduce recorded terrain-ahead alerts; retain source OGGs and LGPL/GPL notices."""
from pathlib import Path
import hashlib,io,json,sys,urllib.request
import numpy as np
import soundfile as sf
app=Path(sys.argv[1]).resolve();rev='266e2f2f14592737c529956273312b1dcfcfa043'
base=f'https://raw.githubusercontent.com/Octol1ttle/FlightAssistant/{rev}/'
folder=app/'SoundSources/FlightAssistant';folder.mkdir(parents=True,exist_ok=True)
decoded={};originals={}
for name in ['terrain_ahead','pull_up']:
 url=base+'src/main/resources/assets/flightassistant/sounds/'+name+'.ogg';data=urllib.request.urlopen(url,timeout=30).read();(folder/(name+'.ogg')).write_bytes(data)
 pcm,rate=sf.read(io.BytesIO(data),dtype='float32',always_2d=True);mono=pcm.mean(axis=1);target=24000
 converted=np.interp(np.arange(int(len(mono)*target/rate))*rate/target,np.arange(len(mono)),mono)
 decoded[name]=converted;originals[name]=dict(url=url,sha256=hashlib.sha256(data).hexdigest())
(folder/'LICENSE-LGPL-3.txt').write_bytes(urllib.request.urlopen(base+'LICENSE',timeout=30).read())
(folder/'LICENSE-GPL-3.txt').write_bytes(urllib.request.urlopen('https://www.gnu.org/licenses/gpl-3.0.txt',timeout=30).read())
sounds=app/'Sounds/Generic';rows=[]
for cue,pcm,names in [('terrain-ahead',decoded['terrain_ahead'],['terrain_ahead']),('terrain-ahead-pull-up',np.concatenate([decoded['terrain_ahead'],np.zeros(2400),decoded['pull_up']]),['terrain_ahead','pull_up'])]:
 path=sounds/(cue+'.wav');sf.write(path,pcm,24000,subtype='PCM_16')
 rows.append(dict(family='Generic',cue=cue,source_urls=[originals[n]['url'] for n in names],revision=rev,license='LGPL-3.0',bytes=path.stat().st_size,sha256=hashlib.sha256(path.read_bytes()).hexdigest(),originals=[originals[n] for n in names],adaptation='Upstream recorded OGG decoded/downmixed/resampled to PCM16 24kHz; urgent sequence joins recordings with 100 ms pause. Original OGGs and notices retained. No speech synthesis generated. OEM authenticity unverified.'))
 print(cue,round(len(pcm)/24000,2),'seconds',flush=True)
manifest=app/'sound-provenance.json';old=json.loads(manifest.read_text(encoding='utf-8-sig'));old=[r for r in old if r['cue'] not in {'terrain-ahead','terrain-ahead-pull-up'}];manifest.write_text(json.dumps(old+rows,indent=2),encoding='utf-8')
(folder/'ATTRIBUTION.md').write_text(f'# FlightAssistant terrain recordings\n\nOctol1ttle / FlightAssistant contributors. Source: https://github.com/Octol1ttle/FlightAssistant/tree/{rev}\n\nRepository licence: GNU LGPL version 3; GPL version 3 incorporated terms also included. Original recorded OGGs are retained as editable source assets. Conversion and the joined urgent phrase are described in sound-provenance.json. No program code from FlightAssistant is used. Aircraft/OEM recording origin is not independently certified.\n',encoding='utf-8')
notice=sounds/'ATTRIBUTION.md';text=notice.read_text(encoding='utf-8') if notice.exists() else '# Generic recordings\n';text=text.split('## Forward terrain alerts')[0];notice.write_text(text+'\n## Forward terrain alerts\n\nterrain-ahead and terrain-ahead-pull-up use LGPL-3.0 FlightAssistant recorded assets. Original OGGs, LGPL/GPL notices and reproduction instructions: ../../SoundSources/FlightAssistant/. These two files do not use the GPL-2.0 MK VIII sources.\n',encoding='utf-8')
