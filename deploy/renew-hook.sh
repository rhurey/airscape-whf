#!/usr/bin/env bash
# Install a renewed certificate into config/certs/ and restart the container.
#
#   deploy/renew-hook.sh <acme-output-dir> [install-dir]
#
# <acme-output-dir> holds the chain and key under either naming convention:
#   certbot:  fullchain.pem + privkey.pem
#   acme.sh:  fullchain.cer + <domain>.key  (or privkey.pem / private.key)
# [install-dir] is the checkout the compose file lives in (default: /opt/airscape-whf).
#
# Kestrel reads the PEM pair once at startup and does not watch the files, so a
# renewal without a restart keeps serving the old certificate until it expires.
set -euo pipefail

src=${1:?usage: renew-hook.sh <acme-output-dir> [install-dir]}
root=${2:-/opt/airscape-whf}
dest="$root/config/certs"
uid=1654   # the user the chiseled .NET image runs as

pick() {
  for candidate in "$@"; do
    [[ -f "$src/$candidate" ]] && { echo "$src/$candidate"; return; }
  done
  return 1
}

chain=$(pick fullchain.pem fullchain.cer) \
  || { echo "no fullchain.pem or fullchain.cer in $src" >&2; exit 1; }
key=$(pick privkey.pem private.key "$(basename "$src" | sed 's/_ecc$//').key") \
  || { echo "no private key (privkey.pem, private.key, <domain>.key) in $src" >&2; exit 1; }

# The extension means nothing; the contents must be PEM.
head -1 "$chain" | grep -q -- '-----BEGIN CERTIFICATE-----' \
  || { echo "$chain is not a PEM certificate" >&2; exit 1; }
head -1 "$key" | grep -Eq -- '-----BEGIN (EC |RSA )?PRIVATE KEY-----' \
  || { echo "$key is not a PEM private key" >&2; exit 1; }

install -d -m 0755 "$dest"
install -m 0444 -o "$uid" -g "$uid" "$chain" "$dest/fullchain.pem"
install -m 0400 -o "$uid" -g "$uid" "$key"   "$dest/privkey.pem"

if systemctl is-enabled --quiet airscape-whf 2>/dev/null; then
  systemctl reload airscape-whf
else
  (cd "$root" && docker compose restart airscape-whf)
fi
echo "installed $(basename "$chain") and $(basename "$key") into $dest; restarted"
