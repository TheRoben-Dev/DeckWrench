#!/usr/bin/env bash
# Builds a fake Steam install in $HOME (and an "SD card" library in $2) for the smoke test and
# for trying the CLI without a Steam Deck:
#
#   HOME=/tmp/fakehome tools/smoke/fake-steam.sh /tmp/fakehome /tmp/fakesd
#
# Games on both libraries, Valve Proton and two GE-Proton builds, per-game compatibility tools and
# launch options, prefixes (one orphaned, with "saves"), shader caches (one orphaned), a Proton log
# with a missing DLL, and cover art. Files are sparse, so "sizes" cost no disk space.
set -euo pipefail
H=$1
SD=$2
ROOT=$(cd "$(dirname "$0")/../.." && pwd)
STEAM="$H/.local/share/Steam"
mkdir -p "$STEAM/steamapps/common" "$SD/steamapps/common" "$STEAM/config" "$STEAM/userdata/12345678/config" "$H/.steam"
ln -sfn "$STEAM" "$H/.steam/steam"

sized() {  # sized <file> <size>
  mkdir -p "$(dirname "$1")"
  truncate -s "$2" "$1"
}
game() {  # game <library> <appid> <name> <folder> <size>
  mkdir -p "$1/steamapps/common/$4"
  sized "$1/steamapps/common/$4/data.pak" "$5"
  local bytes
  bytes=$(stat -c %s "$1/steamapps/common/$4/data.pak")
  printf '"AppState"\n{\n\t"appid"\t\t"%s"\n\t"name"\t\t"%s"\n\t"installdir"\t\t"%s"\n\t"SizeOnDisk"\t\t"%s"\n\t"StateFlags"\t\t"4"\n}\n' \
    "$2" "$3" "$4" "$bytes" > "$1/steamapps/appmanifest_$2.acf"
}
prefix() {  # prefix <library> <appid> <proton version> <saves size>
  local p="$1/steamapps/compatdata/$2"
  mkdir -p "$p/pfx/drive_c/windows/system32" "$p/pfx/drive_c/Program Files" "$p/pfx/dosdevices"
  echo "$3" > "$p/version"
  sized "$p/pfx/drive_c/windows/system32/kernel32.dll" 3M
  sized "$p/pfx/drive_c/users/steamuser/Documents/My Games/$2/save1.sav" "$4"
  sized "$p/pfx/drive_c/users/steamuser/AppData/Local/GameStudio/settings.ini" 4K
  echo 'WINE REGISTRY Version 2' > "$p/pfx/system.reg"
  ln -sfn ../drive_c "$p/pfx/dosdevices/c:"
  ln -sfn / "$p/pfx/dosdevices/z:"
}
shaders() {  # shaders <library> <appid> <size>
  sized "$1/steamapps/shadercache/$2/fozpipelinesv6/steamapprun_pipeline_cache.foz" "$3"
  sized "$1/steamapps/shadercache/$2/DXVK_state_cache/game.dxvk-cache" 2M
}

printf '"libraryfolders"\n{\n\t"0"\n\t{\n\t\t"path"\t\t"%s"\n\t}\n\t"1"\n\t{\n\t\t"path"\t\t"%s"\n\t}\n}\n' "$STEAM" "$SD" > "$STEAM/steamapps/libraryfolders.vdf"

game "$STEAM" 1245620 "ELDEN RING" "ELDEN RING" 48G
game "$STEAM" 1091500 "Cyberpunk 2077" "Cyberpunk 2077" 70G
game "$SD" 413150 "Stardew Valley" "Stardew Valley" 600M
game "$SD" 1145360 "Hades" "Hades" 15G
game "$STEAM" 230410 "Warframe" "Warframe" 50G
# Steam's own tools: listed under Proton, never as games.
game "$STEAM" 1493710 "Proton Experimental" "Proton - Experimental" 1G
game "$STEAM" 2805730 "Proton 9.0" "Proton 9.0 (Beta)" 1G
game "$STEAM" 1628350 "Steam Linux Runtime 3.0 (sniper)" "SteamLinuxRuntime_sniper" 600M
for d in "Proton - Experimental" "Proton 9.0 (Beta)"; do : > "$STEAM/steamapps/common/$d/proton"; done

for v in GE-Proton9-20 GE-Proton8-25; do
  t="$STEAM/compatibilitytools.d/$v"
  mkdir -p "$t/files/bin"
  sized "$t/files/lib.tar" 400M
  printf '"compatibilitytools"\n{\n\t"compat_tools"\n\t{\n\t\t"%s"\n\t\t{\n\t\t\t"install_path"\t\t"."\n\t\t\t"display_name"\t\t"%s"\n\t\t\t"from_oslist"\t\t"windows"\n\t\t\t"to_oslist"\t\t"linux"\n\t\t}\n\t}\n}\n' "$v" "$v" > "$t/compatibilitytool.vdf"
done

# A non-Steam game added to Steam (binary shortcuts.vdf), run with GE-Proton.
SC_ID=$(python3 - "$STEAM/userdata/12345678/config/shortcuts.vdf" "$H/Games/Battle.net" <<'PY'
import struct, sys, zlib
out, start = sys.argv[1], sys.argv[2]
exe = '"%s/Battle.net Launcher.exe"' % start
name = "Battle.net"
appid = zlib.crc32((exe + name).encode()) | 0x80000000
def s(k, v): return b"\x01" + k.encode() + b"\x00" + v.encode() + b"\x00"
def i(k, v): return b"\x02" + k.encode() + b"\x00" + struct.pack("<I", v)
entry = i("appid", appid) + s("AppName", name) + s("Exe", exe) + s("StartDir", '"%s"' % start) + s("LaunchOptions", "") + b"\x00tags\x00\x08"
data = b"\x00shortcuts\x00" + b"\x000\x00" + entry + b"\x08" + b"\x08" + b"\x08"
open(out, "wb").write(data)
print(appid)
PY
)
mkdir -p "$H/Games/Battle.net"
sized "$H/Games/Battle.net/Battle.net Launcher.exe" 80M

prefix "$STEAM" 1245620 "GE-Proton9-20" 12M
prefix "$STEAM" "$SC_ID" "GE-Proton9-20" 1M
shaders "$STEAM" "$SC_ID" 50M
prefix "$STEAM" 1091500 "9.0-203" 40M
prefix "$SD" 1145360 "experimental-9.0-20241121" 2M
prefix "$STEAM" 230410 "9.0-203" 8M
prefix "$STEAM" 271590 "8.0-5" 25M     # GTA V: uninstalled, prefix left behind
shaders "$STEAM" 1245620 900M
shaders "$STEAM" 1091500 1500M
shaders "$SD" 1145360 120M
shaders "$STEAM" 271590 700M           # orphaned
# Steam leftovers of the uninstalled game: an unfinished update and its Workshop items. The
# installed game's own download folder and Workshop items are not leftovers.
sized "$STEAM/steamapps/downloading/271590/update/x64/data.rpf" 300M
sized "$SD/steamapps/temp/271590/chunk" 20M
sized "$STEAM/steamapps/workshop/content/271590/2900000001/mod.pak" 150M
sized "$STEAM/steamapps/downloading/1245620/patch.bin" 10M
sized "$STEAM/steamapps/workshop/content/1245620/3100000001/item.bin" 5M

cat > "$STEAM/config/config.vdf" <<VDF
"InstallConfigStore"
{
	"Software"
	{
		"Valve"
		{
			"Steam"
			{
				"CompatToolMapping"
				{
					"0"
					{
						"name"		"proton_experimental"
						"config"		""
						"priority"		"75"
					}
					"1245620"
					{
						"name"		"GE-Proton9-20"
						"config"		""
						"priority"		"250"
					}
					"1145360"
					{
						"name"		"proton_9"
						"config"		""
						"priority"		"250"
					}
					"$SC_ID"
					{
						"name"		"GE-Proton9-20"
						"config"		""
						"priority"		"250"
					}
				}
			}
		}
	}
}
VDF
cat > "$STEAM/userdata/12345678/config/localconfig.vdf" <<VDF
"UserLocalConfigStore"
{
	"Software"
	{
		"Valve"
		{
			"Steam"
			{
				"apps"
				{
					"1245620"
					{
						"LaunchOptions"		"PROTON_LOG=1 gamemoderun %command%"
						"LastPlayed"		"$(( $(date +%s) - 3*86400 ))"
						"Playtime"		"5432"
					}
					"1091500"
					{
						"LaunchOptions"		"-skipStartScreen"
						"LastPlayed"		"$(( $(date +%s) - 400*86400 ))"
						"Playtime"		"95"
					}
				}
			}
		}
	}
}
VDF

{
  echo "======================="
  echo "Proton: 1732000000 GE-Proton9-20"
  echo "SteamGameId: 1245620"
  for i in $(seq 1 40); do echo "1234.$i:0020:0024:fixme:ntdll:NtQuerySystemInformation info_class SYSTEM_PERFORMANCE_INFORMATION"; done
  echo "1240.100:0020:0024:warn:module:load_dll Loading library foo.dll failed"
  echo "1240.200:0020:0024:err:module:import_dll Library MSVCP140.dll (which is needed by L\"Z:\\\\eldenring.exe\") not found"
  echo "1240.300:0020:0024:err:module:import_dll Library d3dx9_43.dll (which is needed by L\"Z:\\\\launcher.exe\") not found"
  echo "1240.400:0020:0024:err:module:LdrInitializeThunk Importing dlls for L\"Z:\\\\eldenring.exe\" failed, status c0000135"
} > "$H/steam-1245620.log"
# Proton names a non-Steam game's log by its 64-bit game id.
{
  echo "Proton: 1732000000 GE-Proton9-20"
  echo "1300.000:0030:0034:err:seh:NtRaiseException Unhandled exception code c0000005 flags 0 addr 0x140001000"
} > "$H/steam-$(python3 -c "print(($SC_ID << 32) | 0x02000000)").log"

if [ -d "$ROOT/tools/smoke/fixtures" ]; then
  for id in 1245620 1091500 413150; do
    mkdir -p "$STEAM/appcache/librarycache/$id"
    cp "$ROOT/tools/smoke/fixtures/cover-$(( id % 2 == 0 ? 0 : 1 )).jpg" "$STEAM/appcache/librarycache/$id/library_600x900.jpg" 2>/dev/null || true
  done
fi
