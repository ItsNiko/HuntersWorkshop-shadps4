"""Opt-in live integration run against isolated copied saves.
Requires the exact supported port and your existing playable character.
Normal solo and party save hashes are checked afterwards.
"""
import argparse
import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess
import time

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('--port', type=Path, required=True)
parser.add_argument('--game', type=Path, required=True, help='Your CUSA03173 directory')
parser.add_argument('--fixture', type=Path, required=True, help='Private directory outside the installation')
parser.add_argument('--reuse', action='store_true', help='Reload the already-created isolated save')
parser.add_argument('--verify-existing', action='store_true', help='Verify items survived a prior successful grant run')
parser.add_argument('--save-wait', type=int, default=45, help='Seconds to allow the game autosave to flush')
parser.add_argument('--verify-expanded', action='store_true', help='Verify persisted extras and all five shapes')
parser.add_argument('--expanded', action='store_true', help='Grant all five shapes, extras, curse, rune and DLC chalice weapon')
options = parser.parse_args()
PORT = options.port.resolve()
FIXTURE = options.fixture.resolve()
TOOL = Path(__file__).resolve().parents[1]/'bin/HuntersWorkshop.exe'
if FIXTURE == PORT or PORT in FIXTURE.parents:
    raise RuntimeError('Fixture must be outside the port installation.')
running = subprocess.run(['tasklist', '/FI', 'IMAGENAME eq bbport.exe', '/FO', 'CSV', '/NH'], capture_output=True, text=True)
if '"bbport.exe"' in running.stdout.lower():
    raise RuntimeError('Close the running game before starting the isolated fixture.')

def hashes(root):
    result = {}
    for group in ['savedata', 'savedata_party']:
        folder = root/group
        if folder.exists():
            for path in folder.rglob('*'):
                if path.is_symlink():
                    raise RuntimeError('The integration fixture does not follow save links.')
                if path.is_file():
                    result[str(path.relative_to(root))] = hashlib.sha256(path.read_bytes()).hexdigest()
    return result

before = hashes(PORT/'user')
if not before:
    raise RuntimeError('No normal saves found to copy.')
FIXTURE.mkdir(parents=True, exist_ok=True)
solo = FIXTURE/'user/savedata'
if solo.exists() and not options.reuse:
    raise RuntimeError('Fixture exists; use --reuse deliberately or select a new directory.')
if not options.reuse:
    shutil.copytree(PORT/'user/savedata', solo)
    party = PORT/'user/savedata_party'
    shutil.copytree(party if party.exists() else PORT/'user/savedata', FIXTURE/'user/savedata_party')
    if (PORT/'user/addcont').exists():
        shutil.copytree(PORT/'user/addcont', FIXTURE/'user/addcont')
    shutil.copy2(PORT/'bbport.ini', FIXTURE/'bbport.ini')
    (FIXTURE/'original-save-hashes.json').write_text(json.dumps(before, indent=2), encoding='utf-8')
elif not solo.exists():
    raise RuntimeError('No isolated save exists to reload.')

env = os.environ.copy()
settings = PORT/'out/verified-settings.bat'
if settings.exists():
    for line in settings.read_text(encoding='utf-8-sig').splitlines():
        if line.startswith('set "') and line.endswith('"'):
            key, value = line[5:-1].split('=', 1)
            if key.startswith('BB_'):
                env[key] = value
env.update(BB_CONFIG=str(FIXTURE/'bbport.ini'), BB_GPU_USER_DIR=str(FIXTURE/'user'),
           BB_DATA_DIR=str(FIXTURE), BB_PARTY='host', BB_PARTY_AUTOCONTINUE='1',
           BB_PARTY_LOOPBACK='1', BB_PARTY_UPNP='0', BB_PARTY_STUN='0', BB_PARTY_PORT='28991', BB_PARTY_AUTO='0',
           BB_PARTY_START='continue', BB_PARTY_RESTART='0', BB_WATCHDOG='0',
           BB_WINDOW_MODE='windowed', BB_FULLSCREEN='0', BB_SHADER_PRECOMPILE='0', BB_TIMEOUT='180',
           HUNTERS_WORKSHOP_DATA=str(FIXTURE/'workshop-data'))
args = [str(PORT/'out/bbport.exe'), str(PORT/'out/boot-linked.bin'),
        '--content-profile', str(PORT/'out/content.bin'), '--patches', str(PORT/'out/patches.bin'),
        '--app0', str(options.game.resolve()), '--user', str(FIXTURE/'user'), '--timeout', '180']
suffix = 'reload' if options.reuse else 'grant'
status = 1
with (FIXTURE/('game-'+suffix+'.log')).open('w', encoding='utf-8') as log:
    process = subprocess.Popen(args, cwd=PORT, env=env, stdout=log, stderr=subprocess.STDOUT,
                               creationflags=subprocess.CREATE_NO_WINDOW)
    print(f'Isolated game PID {process.pid}', flush=True)
    try:
        time.sleep(15)
        result = FIXTURE/(suffix+'-result.json')
        switches = ['--verify-existing'] if options.verify_existing else ['--give-test']
        if options.expanded:
            switches.append('--give-expanded')
        if options.verify_expanded:
            switches.append('--verify-expanded')
        probe = subprocess.run([str(TOOL), '--probe', str(process.pid), str(result), '--wait-ready', *switches],
                               env=env, timeout=120)
        status = probe.returncode
        print(f'Grant probe exit {status}', flush=True)
        if result.exists():
            print(result.read_text(encoding='utf-8'), flush=True)
        error = Path(str(result)+'.error.txt')
        if status and error.exists():
            print(error.read_text(encoding='utf-8'), flush=True)
        time.sleep(options.save_wait)
    finally:
        if process.poll() is None:
            process.terminate()
        try:
            process.wait(timeout=15)
        except subprocess.TimeoutExpired:
            process.kill()
            process.wait(timeout=10)
        unchanged = before == hashes(PORT/'user')
        (FIXTURE/(suffix+'-verification.json')).write_text(json.dumps(dict(
            NormalSavesUnchanged=unchanged, ProbeExit=status, Pid=process.pid), indent=2), encoding='utf-8')
        assert unchanged, 'Normal saves changed during the isolated test'
        print('Normal solo and party save hashes unchanged.', flush=True)
raise SystemExit(status)
