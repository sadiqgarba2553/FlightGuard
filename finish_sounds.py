"""Assemble a test from recorded phrases and write per-recording licence tables."""
from pathlib import Path
import json,sys,hashlib,wave,struct

root=Path(sys.argv[1]).resolve()
records=json.loads((root/'sound-provenance.json').read_text(encoding='utf-8'))
parts=[];urls=[]
for cue in ['sink-rate','terrain','pull-up','bank-angle']:
    path=root/'Sounds/Generic'/(cue+'.wav')
    with wave.open(str(path),'rb') as f:
        channels,width,rate,n=f.getnchannels(),f.getsampwidth(),f.getframerate(),f.getnframes()
        data=f.readframes(n)
    values=[]
    for i in range(n):
        samples=[]
        for c in range(channels):
            at=(i*channels+c)*width
            samples.append((data[at]-128)/128 if width==1 else struct.unpack_from('<h',data,at)[0]/32768)
        values.append(sum(samples)/channels)
    result=bytearray()
    for i in range(int(n*24000/rate)):
        position=i*rate/24000;lo=min(n-1,int(position));hi=min(n-1,lo+1)
        value=values[lo]*(1-(position-lo))+values[hi]*(position-lo)
        result.extend(struct.pack('<h',max(-32768,min(32767,int(value*28000)))))
    parts.extend([bytes(result),b'\0'*4800])
    urls.append(next(r['source_url'] for r in records if r['family']=='Generic' and r['cue']==cue))
path=root/'Sounds/Generic/gpws-test.wav'
with wave.open(str(path),'wb') as f:
    f.setnchannels(1);f.setsampwidth(2);f.setframerate(24000);f.writeframes(b''.join(parts))
records=[r for r in records if not(r['family']=='Generic' and r['cue']=='gpws-test')]
records.append(dict(family='Generic',cue='gpws-test',source_urls=urls,license='GPL-2.0',
                    adaptation='Four GPL GPWS recordings concatenated with pauses and resampled to PCM16 24kHz; no synthesized speech.',
                    sha256=hashlib.sha256(path.read_bytes()).hexdigest(),bytes=path.stat().st_size))
(root/'sound-provenance.json').write_text(json.dumps(records,indent=2),encoding='utf-8')
for folder in (root/'Sounds').iterdir():
    if not folder.is_dir():continue
    entries=[r for r in records if r['family']==folder.name]
    text=f'# {folder.name} recordings\n\nLicences apply per recording. FlyByWire audio is CC BY-NC-SA 4.0 (noncommercial, attribution, share alike). FlightGear audio is GPL v2 / v2-or-later as specified. Full notices and original WAVs are in SoundSources and Sounds. Runtime playback trims silence, downmixes and applies gain without synthesizing speech. Shared recordings are not exact manufacturer-specific sets.\n\n| Cue | Licence | Original source |\n| --- | --- | --- |\n'
    for record in entries:
        text+='| '+record['cue']+' | '+record['license']+' | '+record.get('source_url',', '.join(record.get('source_urls',[])))+' |\n'
    (folder/'ATTRIBUTION.md').write_text(text,encoding='utf-8')
print(f'{len(records)} recorded mappings; source hashes and licence tables written.')
