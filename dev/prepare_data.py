"""Build compact catalogues from this installation and Noxde's GPL-3.0 catalogue.
No game assets or executable bytes are distributed in the catalogue.
"""
import argparse
import hashlib
import json
from pathlib import Path

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('--params', type=Path, required=True, help='JSON parameter export from your Bloodborne 1.09 installation')
parser.add_argument('--names', type=Path, required=True, help='Noxde save-editor src-tauri/resources at the commit in data/provenance.json')
parser.add_argument('--out', type=Path, default=Path(__file__).resolve().parents[1]/'data')
options = parser.parse_args()
OUT = options.out
SOURCE = options.names
PARAMS = json.loads(options.params.read_text(encoding='utf-8'))
OUT.mkdir(exist_ok=True, parents=True)

def load(name):
    return json.loads((SOURCE / name).read_text(encoding='utf-8'))

items = []
for file, param, category in [('weapons.json', 'EquipParamWeapon', 0),
                               ('armors.json', 'EquipParamProtector', 1),
                               ('items.json', 'EquipParamGoods', 4)]:
    raw = load(file)
    names = raw if file == 'armors.json' else {k: v for group in raw.values() for k, v in group.items()}
    rows = {r['id']: r for r in PARAMS[param]}
    for rid, r in rows.items():
        # Reinforcement rows are selected via the upgrade control, not duplicate list entries.
        if category == 0 and rid % 10000 != 0 and rid >= 10000:
            continue
        info = names.get(str(rid), {})
        name = info.get('item_name', f'Unknown / unused {param} #{rid}')
        maximum = 10 if category == 0 and rid + 1000 in rows else 0
        items.append(dict(Id=rid, Name=name, Category=category,
                          Group=('Weapons' if category == 0 else 'Armor' if category == 1 else 'Items'),
                          Description=info.get('item_desc', ''), Known=bool(info) and '[CUT]' not in name,
                          Max=maximum, Stack=max(1, r.get('maxNum', 1)),
                          Variant=('Uncanny' if 'Uncanny' in name else 'Lost' if 'Lost' in name else 'Standard'),
                          Upgrades=[n for n in range(11) if rid+100*n in rows] if category == 0 else [0]))
for r in PARAMS['EquipParamAccessory']:
    items.append(dict(Id=r['id'], Name=f'Unused accessory #{r["id"]}', Category=2,
                      Group='Accessories (advanced)', Description='Unused accessory parameter. Caryll runes are in the gem builder.',
                      Known=False, Max=0, Stack=1, Variant='Standard', Upgrades=[0]))

upgrade = load('upgrades.json')
effects = []
for r in PARAMS['GemeffectParam']:
    key = str(r['id'])
    rune = key in upgrade['runeEffects']
    info = upgrade['runeEffects' if rune else 'gemEffects'].get(key, {})
    effects.append(dict(Id=r['id'], Name=info.get('name', f'Unknown / unused effect #{key}'),
                        Effect=info.get('effect', f'GemeffectParam {key}; SpEffect {r["speffectId"]}'),
                        Rating=info.get('rating', r['effectRank']), Rune=rune, Known=bool(info),
                        Note=info.get('note', '')))
gems = [dict(Id=r['id'], Flags=r['equipableSegmentWeapon'] + 2*r['equipableSegmentBody']) for r in PARAMS['GemGenParam']]
catalog = dict(Items=items, Effects=effects, Gems=gems,
               WeaponRows=[r['id'] for r in PARAMS['EquipParamWeapon']],
               GameSourceHash='d65f0b4f01d59166aed16f8604196d8b7dd805abbf0758b356e8f1354c9429f9')
(OUT / 'catalog.json').write_text(json.dumps(catalog, ensure_ascii=False, separators=(',', ':')), encoding='utf-8')
print(f'{len(items)} items, {sum(x["Known"] for x in items)} named; {len(effects)} effects, {sum(x["Known"] for x in effects)} named; {len(gems)} gem templates')
