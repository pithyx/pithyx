#!/usr/bin/env bash
# Makes sure the dev unit on port 7484 has the owner's administrator and the test accounts.
#
#   tools/dev-vm/seed-dev-accounts.sh
#
# The config folder is ~/.config/pithyx-dev (override: PITHYX_DEV_CONFIG).
# The owner account comes from owner.env there (mode 600, never in the repo). When the file is missing,
# it is created with the user name admin (override: PITHYX_OWNER_USERNAME) and a random password. On a fresh database the script runs the setup with
# it and stores the recovery key in the same file; otherwise it only checks that the owner can sign in. The owner
# account is never changed.
#
# The test accounts e2e-admin and e2e-user get random passwords in ~/.config/pithyx-dev/e2e.env. They need the users
# API; while the daemon has none, the script says so and skips them.
set -euo pipefail

readonly URL=http://127.0.0.1:7484
readonly CONFIG="${PITHYX_DEV_CONFIG:-$HOME/.config/pithyx-dev}"
readonly OWNER_ENV="$CONFIG/owner.env"
readonly E2E_ENV="$CONFIG/e2e.env"
jar="$(mktemp)"
body="$(mktemp)"
trap 'rm -f "$jar" "$body"' EXIT

die() {
  echo "seed-dev-accounts: $*" >&2
  exit 1
}

[[ "$(head -n1 /etc/pithyx/environment 2>/dev/null)" == dev ]] || die "/etc/pithyx/environment does not say dev; this is for the dev VM only"
[[ "$EUID" -ne 0 ]] || die "run as the owner's user, not as root: the credentials live in that user's home"

random_password() {
  openssl rand -base64 24 | tr -d '/+=\n'
}

# Writes KEY=value into an env file, replacing an earlier value.
set_value() {
  local file="$1" key="$2" value="$3"
  touch "$file"
  chmod 600 "$file"
  sed -i "/^$key=/d" "$file"
  printf '%s=%s\n' "$key" "$value" >>"$file"
}

json_field() {
  python3 -c 'import json, sys; value = json.load(sys.stdin).get(sys.argv[1]); print("" if value is None else value)' "$1"
}

# Prints the HTTP status; the body goes to $body.
api() {
  local method="$1" path="$2" data="${3:-}"
  local args=(-sS -o "$body" -w '%{http_code}' -b "$jar" -c "$jar" -X "$method" -H 'X-Pithyx-Request: 1')
  [[ -z "$data" ]] || args+=(-H 'Content-Type: application/json' --data "$data")
  curl "${args[@]}" "$URL$path"
}

json_credentials() {
  python3 -c 'import json, sys; print(json.dumps({"username": sys.argv[1], "password": sys.argv[2]}))' "$1" "$2"
}

install -d -m 0700 "$CONFIG"
if [[ ! -f "$OWNER_ENV" ]]; then
  set_value "$OWNER_ENV" PITHYX_OWNER_USERNAME "${PITHYX_OWNER_USERNAME:-admin}"
  set_value "$OWNER_ENV" PITHYX_OWNER_PASSWORD "$(random_password)"
  echo "created $OWNER_ENV"
fi
if [[ ! -f "$E2E_ENV" ]]; then
  set_value "$E2E_ENV" PITHYX_E2E_ADMIN_USERNAME e2e-admin
  set_value "$E2E_ENV" PITHYX_E2E_ADMIN_PASSWORD "$(random_password)"
  set_value "$E2E_ENV" PITHYX_E2E_USER_USERNAME e2e-user
  set_value "$E2E_ENV" PITHYX_E2E_USER_PASSWORD "$(random_password)"
  echo "created $E2E_ENV"
fi
# shellcheck source=/dev/null
source "$OWNER_ENV"
# shellcheck source=/dev/null
source "$E2E_ENV"

curl -fsS "$URL/api/health" >/dev/null || die "the dev unit does not answer on $URL"

[[ "$(api GET /api/setup)" == 200 ]] || die "GET /api/setup failed: $(cat "$body")"
owner_session=0
if [[ "$(json_field required <"$body")" == True ]]; then
  status="$(api POST /api/setup "$(json_credentials "$PITHYX_OWNER_USERNAME" "$PITHYX_OWNER_PASSWORD")")"
  [[ "$status" == 200 ]] || die "setup failed ($status): $(cat "$body")"
  set_value "$OWNER_ENV" PITHYX_OWNER_RECOVERY_KEY "$(json_field recoveryKey <"$body")"
  owner_session=1
  echo "set up the owner $PITHYX_OWNER_USERNAME; the recovery key is in $OWNER_ENV"
else
  status="$(api POST /api/auth/login "$(json_credentials "$PITHYX_OWNER_USERNAME" "$PITHYX_OWNER_PASSWORD")")"
  [[ "$status" == 200 ]] || die "the owner cannot sign in ($status: $(cat "$body")); the database has another administrator, run tools/dev-vm/reset-dev-db.sh"
  if [[ "$(json_field totpRequired <"$body")" == True ]]; then
    echo "the owner signs in (password accepted, 2FA is on, so the test accounts are not checked)"
    exit 0
  fi
  owner_session=1
  echo "the owner $PITHYX_OWNER_USERNAME signs in"
fi

if [[ "$owner_session" == 1 ]]; then
  for account in "$PITHYX_E2E_ADMIN_USERNAME:$PITHYX_E2E_ADMIN_PASSWORD:true" "$PITHYX_E2E_USER_USERNAME:$PITHYX_E2E_USER_PASSWORD:false"; do
    IFS=: read -r username password admin <<<"$account"
    data="$(python3 -c 'import json, sys; print(json.dumps({"username": sys.argv[1], "password": sys.argv[2], "isAdmin": sys.argv[3] == "true"}))' "$username" "$password" "$admin")"
    status="$(api POST /api/users "$data")"
    case "$status" in
      201) echo "created $username" ;;
      404 | 405)
        echo "this daemon has no users API yet; the test accounts are skipped"
        break
        ;;
      *)
        [[ "$(json_field code <"$body")" == users.user.exists ]] || die "creating $username failed ($status): $(cat "$body")"
        echo "$username exists"
        ;;
    esac
  done
  api POST /api/auth/logout >/dev/null
fi
