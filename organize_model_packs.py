"""Give existing known model assets their actual pack names without duplicating WAVs."""
from pathlib import Path
import json, shutil, sys
root=Path(sys.argv[1]).resolve()
rename={'Airbus':'Airbus A320 family','Boeing':'Boeing 737','McDonnell Douglas':'McDonnell Douglas MD-11','Bombardier':'Bombardier CRJ','Embraer':'Embraer E-Jet'}
records=json.loads((root/'sound-provenance.json').read_text(encoding='utf-8'))
for old,new in rename.items():
 oldpath=root/'Sounds'/old;newpath=root/'Sounds'/new;newpath.mkdir(exist_ok=True)
 if oldpath.exists():
  for path in oldpath.iterdir():
   if path.is_file():
    if path.suffix.lower()=='.wav':
     target=newpath/path.name
     if target.exists():
      if path.read_bytes()!=target.read_bytes():raise ValueError('Refusing to overwrite model pack: '+str(target))
      path.unlink()
     else:path.rename(target)
    else:shutil.copy2(path,newpath/path.name)
 for r in records:
  if r['family']==old:r['family']=new
records=list({(r['family'],r['cue']):r for r in records}.values())
(root/'sound-provenance.json').write_text(json.dumps(records,indent=2),encoding='utf-8')
print('Model pack names corrected; WAV count unchanged')
