"""Adds a release to manifest.json, the plugin catalog file Jellyfin reads (Dashboard > Plugins > Repositories)."""
import argparse
import json
import re
from datetime import datetime, timezone

parser = argparse.ArgumentParser()
parser.add_argument("--version", required=True, help="Plugin version, e.g. 1.0.0.0")
parser.add_argument("--checksum", required=True, help="MD5 of the release zip")
parser.add_argument("--url", required=True, help="Download URL of the release zip")
parser.add_argument("--changelog", default="")
args = parser.parse_args()

# The Jellyfin version the plugin is built against decides which servers can install it.
with open("Jellyfin.Plugin.DailyPicks/Jellyfin.Plugin.DailyPicks.csproj", encoding="utf-8") as f:
    controller = re.search(r'Include="Jellyfin.Controller" Version="([\d.]+)"', f.read()).group(1)
target_abi = controller + ".0" * (4 - controller.count(".") - 1)

with open("manifest.json", encoding="utf-8") as f:
    manifest = json.load(f)

plugin = manifest[0]
plugin["versions"] = [v for v in plugin["versions"] if v["version"] != args.version]
plugin["versions"].insert(0, {
    "version": args.version,
    "changelog": args.changelog or f"Release {args.version}",
    "targetAbi": target_abi,
    "sourceUrl": args.url,
    "checksum": args.checksum,
    "timestamp": datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ"),
})

with open("manifest.json", "w", encoding="utf-8") as f:
    json.dump(manifest, f, indent=2)
    f.write("\n")

print(f"manifest.json: added {args.version} (targetAbi {target_abi})")
