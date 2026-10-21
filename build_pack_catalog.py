"""Build the downloadable-pack catalog from pinned, licence-attributed source assets."""
from pathlib import Path
import json,sys
app=Path(sys.argv[1]).resolve()
rows=json.loads((app/'sound-provenance.json').read_text(encoding='utf-8'))
gpl=(app/'SoundSources/B757/LICENSE.txt').read_text(encoding='utf-8-sig')
cc=(app/'SoundSources/FBW/LICENSE.txt').read_text(encoding='utf-8-sig')
packs=[]
for profile in sorted({r['family'] for r in rows}):
    assets=[dict(Cue=r['cue'],Url=r['source_url'],Sha256=r['sha256'],Bytes=r['bytes'],License=r['license'],Revision=r['revision']) for r in rows if r['family']==profile and r.get('source_url') and r.get('revision')]
    if not assets:continue
    notices=[dict(Name='ATTRIBUTION.md',Content=(app/'Sounds'/profile/'ATTRIBUTION.md').read_text(encoding='utf-8'))]
    licences={a['License'] for a in assets}
    if any(licence.startswith('GPL-2') for licence in licences):notices.append(dict(Name='LICENSE-GPL-2.txt',Content=gpl+'\nIndividual clips follow the version applicability in ATTRIBUTION.md and the catalog.'))
    if any(licence.startswith('GPL-3') for licence in licences):notices.append(dict(Name='LICENSE-GPL-3.txt',Content=(app/'SoundSources/A340/LICENSE.txt').read_text(encoding='utf-8',errors='replace')))
    if any('CC-BY' in licence for licence in licences):notices.append(dict(Name='LICENSE-CC-BY-NC-SA-4.txt',Content=cc))
    packs.append(dict(Profile=profile,Version='0.7-pinned',Assets=assets,Notices=notices,Note='Pinned source revision, SHA-256 and licence recorded. Exact OEM voice authenticity is not independently certified. Shared fallback recordings remain labelled in the library.'))
(app/'pack-catalog.json').write_text(json.dumps(dict(Schema=1,Packs=packs),indent=2),encoding='utf-8')
print(f'Catalog built: {len(packs)} packs, {sum(len(p["Assets"]) for p in packs)} downloadable clips')
