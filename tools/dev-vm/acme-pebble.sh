#!/usr/bin/env bash
# Runs the ACME integration test (Category=AcmeIntegration, D378) against Pebble, the test authority of Let's Encrypt
# (Debian package pebble), on the dev VM: a throwaway CA for Pebble's own HTTPS, pebble-challtestsrv as its DNS with
# 127.0.0.1 for every name, and Pebble checking HTTP-01 on port 5002, where the test's daemon serves its plain proxy
# port. The CA reaches only the test process through SSL_CERT_FILE; the system's trust store stays as it is.
#
#   tools/dev-vm/acme-pebble.sh
#
# Everything lives in a scratch folder that is removed afterwards, together with both servers. GitHub CI never runs it.
set -euo pipefail
export LC_ALL=C.UTF-8

repo="$(cd "$(dirname "$0")/../.." && pwd)"
command -v pebble >/dev/null || { echo "acme-pebble: install the Debian package pebble first" >&2; exit 2; }

scratch="$(mktemp -d)"
pids=()
# shellcheck disable=SC2317 # called by the EXIT trap
cleanup() {
  for pid in "${pids[@]}"; do kill "$pid" 2>/dev/null || true; done
  rm -rf "$scratch"
}
trap cleanup EXIT

openssl req -x509 -newkey ec -pkeyopt ec_paramgen_curve:P-256 -nodes -days 1 -subj "/CN=Pebble test CA" \
  -keyout "$scratch/ca.key" -out "$scratch/ca.pem" 2>/dev/null
openssl req -newkey ec -pkeyopt ec_paramgen_curve:P-256 -nodes -subj "/CN=localhost" \
  -keyout "$scratch/pebble.key" -out "$scratch/pebble.csr" 2>/dev/null
openssl x509 -req -in "$scratch/pebble.csr" -CA "$scratch/ca.pem" -CAkey "$scratch/ca.key" -CAcreateserial -days 1 \
  -extfile <(printf 'subjectAltName=DNS:localhost,IP:127.0.0.1\n') -out "$scratch/pebble.pem" 2>/dev/null
cat > "$scratch/pebble.json" <<JSON
{"pebble": {"listenAddress": "127.0.0.1:14000", "managementListenAddress": "127.0.0.1:15000",
  "certificate": "$scratch/pebble.pem", "privateKey": "$scratch/pebble.key", "httpPort": 5002, "tlsPort": 5001,
  "ocspResponderURL": "", "externalAccountBindingRequired": false}}
JSON
cat /etc/ssl/certs/ca-certificates.crt "$scratch/ca.pem" > "$scratch/trust.pem"

pebble-challtestsrv -defaultIPv4 127.0.0.1 -defaultIPv6 "" -dns01 127.0.0.1:8053 -http01 "" -https01 "" -doh "" \
  -tlsalpn01 "" -management 127.0.0.1:8055 >"$scratch/challtestsrv.log" 2>&1 &
pids+=("$!")
PEBBLE_VA_NOSLEEP=1 PEBBLE_WFE_NONCEREJECT=0 pebble -config "$scratch/pebble.json" -dnsserver 127.0.0.1:8053 \
  >"$scratch/pebble.log" 2>&1 &
pids+=("$!")
for _ in $(seq 50); do
  curl -sf --cacert "$scratch/ca.pem" https://127.0.0.1:14000/dir >/dev/null && break
  sleep 0.2
done
curl -sf --cacert "$scratch/ca.pem" https://127.0.0.1:14000/dir >/dev/null || { cat "$scratch/pebble.log" >&2; exit 1; }

dotnet build "$repo/daemon/tests/Pithyx.Daemon.Tests" -c Release --nologo -v quiet
status=0
SSL_CERT_FILE="$scratch/trust.pem" PITHYX_PEBBLE_DIRECTORY=https://127.0.0.1:14000/dir PITHYX_PEBBLE_HTTP_PORT=5002 \
  "$repo/daemon/tests/Pithyx.Daemon.Tests/bin/Release/net10.0/Pithyx.Daemon.Tests" -trait "Category=AcmeIntegration" || status=$?
if [[ "$status" -ne 0 ]]; then
  echo "acme-pebble: the test failed; Pebble said:" >&2
  tail -40 "$scratch/pebble.log" >&2
fi
exit "$status"
