#!/usr/bin/env bash
# Checks Debian's Docker for app backends on the dev VM (step A1, D309): userns-remap with identity sub-ranges,
# overlay2 on a ZFS dataset, internal networks, Compose, image ids after save and load, the journald fields, cgroup
# limits and the Engine API from .NET over the Unix socket. Prints PASS, FAIL or NOTE per check.
#
#   tools/dev-vm/docker-spike.sh [--record] [--keep] [--disk <serial>]
#
#   --record  save the Engine API answers in daemon/tests/fixtures/docker and the container's journal entries in
#             daemon/tests/fixtures/journal/docker-app.jsonl
#   --keep    leave Docker running with the spike configuration, the pool and the remap user; the next run reuses them
#   --disk    the empty test disk for the throwaway pool pithyxtestdocker (default VYXTEST06)
#
# Docker runs with the configuration D309 chooses for the product, except that it comes from a runtime drop-in in
# /run/systemd/system instead of /etc/docker/daemon.json. The pool has no <pool>/pithyx dataset, so the dev daemon on
# 7484 leaves it alone (no cache dataset, D276) and keeps running. Afterwards Docker and containerd are stopped and
# disabled again, the remap user and its subordinate ids are removed, the pool is destroyed and its disk wiped.
#
# Exit codes: 0 every check passed, 1 a check failed, 2 refused or a setup error.
set -euo pipefail
export LC_ALL=C.UTF-8

readonly POOL=pithyxtestdocker
readonly DOCKER_ROOT=/$POOL/.pithyx/docker
readonly APP_ID=org.pithyx.spike
readonly APP_DIR=/$POOL/.pithyx/apps/$APP_ID
readonly PROJECT=pithyx-org_pithyx_spike
readonly REMAP_USER=pithyx-docker
# Container ids map to this base plus the id, except the identity windows: app uids 40000 to 49999, and share and app
# gids 30000 to 49999. The base lies above SUB_UID_MAX in login.defs, so useradd never hands these ids to a user.
readonly REMAP_BASE=1000000000
readonly SUBUID_LINES="$REMAP_USER:$REMAP_BASE:40000
$REMAP_USER:40000:10000
$REMAP_USER:$((REMAP_BASE + 50000)):15536"
readonly SUBGID_LINES="$REMAP_USER:$REMAP_BASE:30000
$REMAP_USER:30000:20000
$REMAP_USER:$((REMAP_BASE + 50000)):15536"
readonly IMAGE=busybox:1.37.0@sha256:bdf57e528e45e4433820e045b29b4597825a1c9e38353532d90a01445013f82e
readonly SPIKE_RUN_DIR=/run/pithyx-docker-spike
readonly DROP_IN_DIR=/run/systemd/system/docker.service.d
readonly DROP_IN=$DROP_IN_DIR/pithyx-spike.conf
readonly NFT_TABLE=pithyx-spike
readonly DEV_URL=http://localhost:7484/

repo="$(cd "$(dirname "$0")/../.." && pwd)"
disk_serial=VYXTEST06
record=false
keep=false
passed=0
failed=0

die() {
  echo "docker-spike: $*" >&2
  exit 2
}

pass() {
  passed=$((passed + 1))
  printf 'PASS  %s: %s\n' "$1" "$2"
}

fail() {
  failed=$((failed + 1))
  printf 'FAIL  %s: %s\n' "$1" "$2"
}

note() {
  printf 'NOTE  %s: %s\n' "$1" "$2"
}

dk() {
  sudo docker "$@"
}

while [[ $# -gt 0 ]]; do
  case "$1" in
    --record)
      record=true
      shift
      ;;
    --keep)
      keep=true
      shift
      ;;
    --disk)
      disk_serial="${2:?--disk needs a serial}"
      shift 2
      ;;
    *) die "unknown argument $1" ;;
  esac
done

[[ "$(head -n1 /etc/pithyx/environment 2>/dev/null)" == dev ]] || die "/etc/pithyx/environment does not say dev; this is for the dev VM only"
[[ "$EUID" -ne 0 ]] || die "run as your user; the script asks sudo for the root steps"
for command in docker zpool dotnet curl jq nft; do
  sudo sh -c "command -v $command" >/dev/null || die "$command not found; run tools/dev-vm/provision.sh first"
done
docker compose version >/dev/null 2>&1 || die "docker compose not found; run tools/dev-vm/provision.sh first"
[[ ! -e /etc/docker/daemon.json ]] || die "/etc/docker/daemon.json exists: the Pithyx daemon owns Docker on this machine"

work="$(mktemp -d)"
since="$(date +%s)"
fixtures="$work/fixtures"
mkdir -p "$fixtures"

dev_status() {
  printf '%s %s %s %s' "$(systemctl is-active pithyx-dev.service || true)" \
    "$(curl -s -o /dev/null -m 5 -w '%{http_code}' "$DEV_URL" || true)" \
    "$(systemctl is-active ssh.service || true)" "$(systemctl is-active smbd.service || true)"
}
dev_before="$(dev_status)"
sudo nft list ruleset 2>/dev/null | cat >"$work/nft-before.txt"

# Pool and datasets, with the properties of a Pithyx pool (ZfsPlans.CreateOptions) on the datasets.
setup_pool() {
  if ! sudo zpool list -H -o name "$POOL" >/dev/null 2>&1; then
    local disk
    disk="$(lsblk -dn -o NAME,SERIAL,TYPE | awk -v s="$disk_serial" '$2 == s && $3 == "disk" { print $1 }')"
    [[ -n "$disk" ]] || die "no disk with serial $disk_serial"
    if [[ "$(lsblk -nrpo NAME "/dev/$disk" | wc -l)" -ne 1 || -n "$(lsblk -dno FSTYPE "/dev/$disk")" ]]; then
      die "$disk_serial is not empty; if it is a leftover of this script, run tools/dev-vm/testpool.sh wipe $disk_serial"
    fi
    "$repo/tools/dev-vm/testpool.sh" create "$POOL" "$disk_serial" >/dev/null
  fi
  local dataset
  for dataset in docker apps; do
    if ! sudo zfs list -H -o name "$POOL/$dataset" >/dev/null 2>&1; then
      sudo zfs create -o compression=lz4 -o xattr=sa -o acltype=posixacl -o atime=off -o normalization=formD \
        -o mountpoint="/$POOL/.pithyx/$dataset" "$POOL/$dataset"
    fi
  done
}

# Replaces every subordinate id line of the remap user with the wanted ones.
write_subids() {
  local file=$1 lines=$2 current
  current="$(grep "^$REMAP_USER:" "$file" || true)"
  if [[ "$current" != "$lines" ]]; then
    { grep -v "^$REMAP_USER:" "$file" || true; printf '%s\n' "$lines"; } >"$work/subids"
    sudo install -m 644 -o root -g root "$work/subids" "$file"
  fi
}

setup_remap_user() {
  if ! getent passwd "$REMAP_USER" >/dev/null; then
    sudo useradd --system --user-group --no-create-home --home-dir /nonexistent --shell /usr/sbin/nologin "$REMAP_USER"
  fi
  write_subids /etc/subuid "$SUBUID_LINES"
  write_subids /etc/subgid "$SUBGID_LINES"
}

# The daemon.json D309 proposes for the product, with the data root on the app pool.
setup_docker() {
  cat >"$work/daemon.json" <<EOF
{
  "data-root": "$DOCKER_ROOT",
  "storage-driver": "overlay2",
  "userns-remap": "$REMAP_USER",
  "bridge": "none",
  "default-address-pools": [{ "base": "172.29.0.0/16", "size": 24 }],
  "log-driver": "journald",
  "log-opts": { "labels": "com.pithyx.app,com.pithyx.service", "tag": "{{.Name}}" },
  "no-new-privileges": true
}
EOF
  cat >"$work/drop-in.conf" <<EOF
[Unit]
RequiresMountsFor=$DOCKER_ROOT

[Service]
ExecStart=
ExecStart=/usr/sbin/dockerd -H fd:// --containerd=/run/containerd/containerd.sock --config-file $SPIKE_RUN_DIR/daemon.json
EOF
  sudo install -d -m 755 "$SPIKE_RUN_DIR" "$DROP_IN_DIR"
  sudo install -m 644 "$work/daemon.json" "$SPIKE_RUN_DIR/daemon.json"
  sudo install -m 644 "$work/drop-in.conf" "$DROP_IN"
  sudo systemctl daemon-reload
  sudo systemctl restart docker.service
  dk info >/dev/null || die "docker does not answer after the start; see journalctl -u docker"
}

remove_spike_objects() {
  local ids=()
  if systemctl is-active --quiet docker.service; then
    (cd "$APP_DIR/run" 2>/dev/null && dk compose -p "$PROJECT" down --timeout 5 >/dev/null 2>&1) || true
    mapfile -t ids < <(dk ps -aq --filter "label=com.pithyx.app" || true)
    [[ ${#ids[@]} -eq 0 ]] || dk rm -f "${ids[@]}" >/dev/null
    for network in pithyx-spike-other pithyx-spike-probe; do
      dk network rm "$network" >/dev/null 2>&1 || true
    done
  fi
  sudo nft delete table inet "$NFT_TABLE" 2>/dev/null || true
}

cleanup() {
  local status=$?
  remove_spike_objects
  if [[ "$keep" == true ]]; then
    echo "docker-spike: kept Docker running with the spike configuration, the pool $POOL and the user $REMAP_USER"
  else
    sudo systemctl stop docker.socket docker.service containerd.service
    sudo systemctl disable --quiet docker.socket docker.service containerd.service 2>/dev/null || true
    sudo rm -f "$DROP_IN"
    sudo rmdir --ignore-fail-on-non-empty "$DROP_IN_DIR" 2>/dev/null || true
    sudo rm -rf "$SPIKE_RUN_DIR"
    sudo systemctl daemon-reload
    if getent passwd "$REMAP_USER" >/dev/null; then
      write_subids /etc/subuid ""
      write_subids /etc/subgid ""
      sudo sed -i '/^$/d' /etc/subuid /etc/subgid
      sudo userdel "$REMAP_USER"
    fi
    if sudo zpool list -H -o name "$POOL" >/dev/null 2>&1; then
      "$repo/tools/dev-vm/testpool.sh" destroy "$POOL" >/dev/null
      "$repo/tools/dev-vm/testpool.sh" wipe "$disk_serial" >/dev/null
    fi
  fi
  sudo rm -rf "$work"
  exit "$status"
}
trap cleanup EXIT

setup_pool
setup_remap_user
setup_docker
remove_spike_objects

echo "Docker $(dk version --format '{{.Server.Version}}'), API $(dk version --format '{{.Server.APIVersion}}'), $(docker compose version), OpenZFS $(zfs version | head -n1)"

# Image: pulled by digest, saved, removed and loaded again. The id is the config digest and must survive.
check_image() {
  dk pull -q "$IMAGE" >/dev/null || die "cannot pull $IMAGE"
  local id loaded digests
  id="$(dk image inspect -f '{{.Id}}' "$IMAGE")"
  dk save -o "$work/image.tar" "$IMAGE"
  dk rmi "$IMAGE" >/dev/null
  loaded="$(dk load -q -i "$work/image.tar" | sed -n 's/^Loaded image ID: //p')"
  if [[ "$loaded" == "$id" ]] && dk image inspect "$id" >/dev/null; then
    pass "image id after save and load" "$id"
  else
    fail "image id after save and load" "pulled $id, loaded '$loaded'"
  fi
  digests="$(dk image inspect -f '{{json .RepoDigests}}' "$id")"
  if dk run --rm --pull never --network none "$IMAGE" true >/dev/null 2>&1; then
    note "image reference after load" "the digest reference still resolves (RepoDigests $digests)"
  else
    note "image reference after load" "RepoDigests $digests: the digest reference no longer resolves without a pull, only the image id does"
  fi
  image_id=$id
}

check_storage() {
  local driver backing root fstype
  driver="$(dk info -f '{{.Driver}}')"
  backing="$(dk info -f '{{range .DriverStatus}}{{if eq (index . 0) "Backing Filesystem"}}{{index . 1}}{{end}}{{end}}')"
  root="$(dk info -f '{{.DockerRootDir}}')"
  fstype="$(sudo stat -f -c %T "$root")"
  if [[ "$driver" == overlay2 && "$backing" == zfs && "$fstype" == zfs && "$root" == "$DOCKER_ROOT/$REMAP_BASE.$REMAP_BASE" ]]; then
    pass "overlay2 on zfs" "driver $driver, backing filesystem $backing, data root $root ($(sudo zfs list -H -o name "$root"))"
  else
    fail "overlay2 on zfs" "driver $driver, backing filesystem $backing, data root $root on $fstype"
  fi
  dk rm -f pithyx-spike-overlay >/dev/null 2>&1 || true
  if dk run --name pithyx-spike-overlay --network none --label com.pithyx.app=$APP_ID "$image_id" \
    sh -c 'echo spike >/etc/spike && rm /bin/vi && mv /etc/group /etc/group.moved && [ ! -e /bin/vi ] && cat /etc/spike' \
    >/dev/null 2>&1; then
    local diff
    diff="$(dk diff pithyx-spike-overlay | sort | tr '\n' ' ')"
    if [[ "$diff" == *"A /etc/spike"* && "$diff" == *"D /bin/vi"* && "$diff" == *"D /etc/group "* ]]; then
      pass "overlay2 copy-up and whiteout" "$diff"
    else
      fail "overlay2 copy-up and whiteout" "unexpected diff: $diff"
    fi
  else
    fail "overlay2 copy-up and whiteout" "writing, deleting or renaming in the container failed"
  fi
  dk rm -f pithyx-spike-overlay >/dev/null
}

check_userns() {
  local maps expected
  maps="$(dk run --rm --network none "$image_id" cat /proc/self/uid_map /proc/self/gid_map | awk '{ print $1, $2, $3 }' | tr '\n' ';')"
  expected="0 $REMAP_BASE 40000;40000 40000 10000;50000 $((REMAP_BASE + 50000)) 15536;0 $REMAP_BASE 30000;30000 30000 20000;50000 $((REMAP_BASE + 50000)) 15536;"
  if [[ "$maps" == "$expected" ]]; then
    pass "userns id maps" "uid_map and gid_map have the identity windows ($maps)"
  else
    fail "userns id maps" "got $maps, expected $expected"
  fi

  local volume=$APP_DIR/volumes/ids secret=$APP_DIR/volumes/root-only share=$APP_DIR/volumes/share
  sudo rm -rf "$APP_DIR/volumes"
  sudo install -d -m 1777 "$volume"
  sudo install -d -m 2770 -o root -g 39999 "$share"
  echo secret | sudo tee "$secret" >/dev/null
  sudo chmod 600 "$secret"

  # Container root: the file it writes and its processes belong to the unprivileged base id on the host, and a
  # root-only host file stays closed to it.
  local owner pid host_uid
  dk run --rm --network none -v "$volume:/v" "$image_id" touch /v/root >/dev/null
  owner="$(stat -c %u:%g "$volume/root")"
  dk run -d --name pithyx-spike-root --network none --label com.pithyx.app=$APP_ID "$image_id" sleep 30 >/dev/null
  pid="$(dk inspect -f '{{.State.Pid}}' pithyx-spike-root)"
  host_uid="$(ps -o uid= -p "$pid" | tr -d ' ')"
  dk rm -f pithyx-spike-root >/dev/null
  if dk run --rm --network none -v "$secret:/secret:ro" "$image_id" cat /secret >/dev/null 2>&1; then
    fail "userns container root" "container root read a root-only host file"
  elif [[ "$owner" == "$REMAP_BASE:$REMAP_BASE" && "$host_uid" == "$REMAP_BASE" ]]; then
    pass "userns container root" "files $owner, process uid $host_uid on the host, a root-only host file is denied"
  else
    fail "userns container root" "files $owner, process uid $host_uid on the host"
  fi

  # Container ids at the edges of the identity windows and outside them (20000 is a Pithyx user on the host).
  local id want got results="" ok=true
  for id in 40000 49999 50000 20000; do
    if ((id >= 40000 && id <= 49999)); then want="$id:$id"; else want="$((REMAP_BASE + id)):$((REMAP_BASE + id))"; fi
    dk run --rm --network none --user "$id:$id" -v "$volume:/v" "$image_id" touch "/v/$id" >/dev/null
    got="$(stat -c %u:%g "$volume/$id")"
    results+="$id->$got "
    [[ "$got" == "$want" ]] || ok=false
  done
  if [[ "$ok" == true ]]; then
    pass "userns app uids 1:1" "container to host: $results"
  else
    fail "userns app uids 1:1" "container to host: $results"
  fi

  # A share folder of group 39999 (mode 2770): an app uid gets in only with that supplementary group.
  local with without group
  with="$(dk run --rm --network none --user 49999:49999 --group-add 39999 -v "$share:/share" "$image_id" \
    sh -c 'touch /share/file && id -G' 2>&1 || true)"
  group="$(sudo stat -c %g "$share/file" 2>/dev/null || echo missing)"
  without="$(dk run --rm --network none --user 49999:49999 -v "$share:/share" "$image_id" touch /share/denied 2>&1 || true)"
  if [[ "$group" == 39999 && "$without" == *"Permission denied"* ]]; then
    pass "userns share gids 1:1" "with group_add 39999 the app writes (groups $with, file group $group on the host), without it: denied"
  else
    fail "userns share gids 1:1" "with group_add: '$with' (file group $group), without: '$without'"
  fi
  local edge=denied
  sudo install -d -m 2770 -o root -g 30000 "$share-30000"
  if dk run --rm --network none --user 49999:49999 --group-add 30000 -v "$share-30000:/share" "$image_id" \
    touch /share/file >/dev/null 2>&1; then
    edge="$(sudo stat -c %g "$share-30000/file")"
  fi
  if [[ "$edge" == 30000 ]]; then
    pass "userns share gid 30000" "the lower edge of the gid window maps 1:1"
  else
    fail "userns share gid 30000" "file group $edge"
  fi
}

# Compose with the product's shape: project pithyx-<id>, an internal network of its own, the app uid with a share
# group, a read-only root, limits and labels; the image by id, because a loaded image has no digest reference.
check_compose() {
  sudo install -d -m 755 "$APP_DIR/run"
  sed "s/@IMAGE@/$image_id/" >"$work/compose.yaml" <<'EOF'
services:
  web:
    image: @IMAGE@
    pull_policy: never
    command:
      - sh
      - -c
      - echo to-stdout; echo to-stderr >&2; mkdir -p /tmp/www && echo spike >/tmp/www/index.html && exec httpd -f -p 8080 -h /tmp/www
    user: '49999:49999'
    group_add: ['39999']
    read_only: true
    tmpfs: [/tmp]
    mem_limit: 64m
    cpus: 0.5
    pids_limit: 100
    labels:
      com.pithyx.app: org.pithyx.spike
      com.pithyx.service: web
    healthcheck:
      test: [CMD, wget, -q, -O, /dev/null, http://127.0.0.1:8080/]
      interval: 1s
      timeout: 2s
      retries: 20
    networks: [app]
networks:
  app:
    internal: true
    driver_opts:
      com.docker.network.bridge.name: px-spike0
    labels:
      com.pithyx.app: org.pithyx.spike
EOF
  sudo install -m 644 "$work/compose.yaml" "$APP_DIR/run/compose.yaml"

  local output
  output="$(cd "$APP_DIR/run" && { dk compose -p "$PROJECT" --progress json up -d --wait 2>&1 || true; })"
  if [[ "$output" == *'unsupported --progress value "json"'* ]]; then
    note "compose --progress json" "Docker Compose $(docker compose version --short) refuses it ($output); job progress comes from Engine events (D309)"
  elif jq -e . >/dev/null 2>&1 <<<"$output"; then
    pass "compose --progress json" "$(wc -l <<<"$output") JSON lines"
  else
    fail "compose --progress json" "$output"
  fi

  local status
  if output="$(cd "$APP_DIR/run" && dk compose -p "$PROJECT" --progress plain up -d --wait 2>&1)"; then
    status="$(dk inspect -f '{{.State.Health.Status}}' "$PROJECT-web-1")"
    if [[ "$status" == healthy ]]; then
      pass "compose up --wait" "returned once $PROJECT-web-1 was $status"
    else
      fail "compose up --wait" "returned with $PROJECT-web-1 $status"
    fi
  else
    fail "compose up --wait" "$(tail -n 3 <<<"$output" | tr '\n' ' ')"
  fi
}

check_network() {
  local web=$PROJECT-web-1 ip body internet gateway
  ip="$(dk inspect -f '{{range .NetworkSettings.Networks}}{{.IPAddress}}{{end}}' "$web")"
  body="$(curl -s -m 5 "http://$ip:8080/" || true)"
  if [[ "$body" == spike ]]; then
    pass "internal network from the host" "the host reaches $web at $ip:8080 on bridge px-spike0"
  else
    fail "internal network from the host" "http://$ip:8080/ answered '$body'"
  fi

  internet="$(dk run --rm --network "${PROJECT}_app" "$image_id" sh -c \
    'wget -q -T 3 -O /dev/null http://1.1.1.1/ 2>&1 && echo open; nslookup -timeout=2 example.com >/dev/null 2>&1 && echo dns; wget -q -T 3 -O - http://web:8080/' 2>&1 || true)"
  if [[ "$internet" != *open* && "$internet" != *dns* && "$internet" == *spike* ]]; then
    pass "internal network without internet" "no route to 1.1.1.1, no external DNS, the service name web resolves inside the network"
  else
    fail "internal network without internet" "$(tr '\n' ' ' <<<"$internet")"
  fi

  dk network create --internal --label com.pithyx.app=org.pithyx.other -o com.docker.network.bridge.name=px-spike1 \
    pithyx-spike-other >/dev/null
  if dk run --rm --network pithyx-spike-other "$image_id" wget -q -T 3 -O /dev/null "http://$ip:8080/" >/dev/null 2>&1; then
    fail "internal networks isolated" "a container on another app network reached $ip:8080"
  else
    pass "internal networks isolated" "a container on another app network cannot reach $ip:8080"
  fi

  # The host's own address on the bridge is in the network, so containers reach the host's services there unless an
  # input rule for the app bridges (px-*) drops new connections.
  gateway="$(ip -4 -o addr show dev px-spike0 | awk '{ print $4 }' | cut -d/ -f1)"
  local probe="wget -q -T 3 -O /dev/null http://$gateway:7484/ 2>/dev/null && echo reachable || echo blocked"
  if [[ "$(dk run --rm --network "${PROJECT}_app" "$image_id" sh -c "$probe")" == reachable ]]; then
    note "host services from an app network" "without a rule a container reaches 7484 on the bridge address $gateway"
  fi
  sudo nft -f - <<EOF
table inet $NFT_TABLE {
  chain input {
    type filter hook input priority filter - 1; policy accept;
    iifname "px-*" ct state established,related accept
    iifname "px-*" drop
  }
}
EOF
  local after
  after="$(dk run --rm --network "${PROJECT}_app" "$image_id" sh -c "$probe")"
  body="$(curl -s -m 5 "http://$ip:8080/" || true)"
  if [[ "$after" == blocked && "$body" == spike ]]; then
    pass "host services blocked for app networks" "with the nft rule on px-* the container cannot reach $gateway:7484, the host still reaches the container"
  else
    fail "host services blocked for app networks" "container to host: $after, host to container: '$body'"
  fi
  sudo nft delete table inet "$NFT_TABLE"
}

check_cgroups() {
  local web=$PROJECT-web-1 limits
  limits="$(dk exec "$web" cat /sys/fs/cgroup/memory.max /sys/fs/cgroup/cpu.max /sys/fs/cgroup/pids.max | tr '\n' ';')"
  if [[ "$limits" == "67108864;50000 100000;100;" ]]; then
    pass "cgroup limits" "memory.max, cpu.max and pids.max from mem_limit 64m, cpus 0.5, pids_limit 100: $limits"
  else
    fail "cgroup limits" "got $limits"
  fi
  local oom
  # The main process itself must be the one killed: when only a child dies, OOMKilled and the oom event can come too
  # late or not at all (seen with sh -c 'head -c 100m /dev/zero | tail'), and only the exit code 137 remains.
  dk run --name pithyx-spike-oom --network none --memory 32m --memory-swap 32m \
    --label com.pithyx.app=$APP_ID --label com.pithyx.service=oom "$image_id" tail /dev/zero >/dev/null 2>&1 || true
  oom="$(dk inspect -f '{{.State.OOMKilled}} {{.State.ExitCode}}' pithyx-spike-oom)"
  if [[ "$oom" == "true 137" ]]; then
    pass "cgroup memory limit enforced" "a container above 32m was OOM-killed (OOMKilled true, exit 137)"
  else
    fail "cgroup memory limit enforced" "OOMKilled and exit code: $oom"
  fi
}

check_journal() {
  local web=$PROJECT-web-1 lines=""
  for _ in 1 2 3 4 5; do
    lines="$(sudo journalctl --no-pager -o json "CONTAINER_NAME=$web" || true)"
    [[ "$(grep -c . <<<"$lines")" -lt 2 ]] || break
    sleep 1
  done
  printf '%s\n' "$lines" >"$fixtures/docker-app.jsonl"
  local summary
  summary="$(jq -rs --arg app "$APP_ID" '
    def has_all: has("CONTAINER_NAME") and has("CONTAINER_ID") and has("CONTAINER_ID_FULL") and has("CONTAINER_TAG")
      and has("IMAGE_NAME") and has("SYSLOG_IDENTIFIER") and .COM_PITHYX_APP == $app and .COM_PITHYX_SERVICE == "web";
    (map(select(.MESSAGE == "to-stdout" and .PRIORITY == "6" and has_all)) | length > 0) and
    (map(select(.MESSAGE == "to-stderr" and .PRIORITY == "3" and has_all)) | length > 0)
  ' <<<"$lines" 2>/dev/null || echo false)"
  if [[ "$summary" == true ]]; then
    pass "journald fields" "CONTAINER_NAME, CONTAINER_ID(_FULL), CONTAINER_TAG, IMAGE_NAME, SYSLOG_IDENTIFIER, COM_PITHYX_APP, COM_PITHYX_SERVICE; stdout PRIORITY 6, stderr 3"
  else
    fail "journald fields" "missing fields or priorities in $(grep -c . <<<"$lines") entries for $web"
  fi
}

check_dotnet() {
  local output status=0
  if ! dotnet build "$repo/tools/dev-vm/docker-engine-probe.cs" -o "$work/probe" --nologo --verbosity quiet >"$work/build.log" 2>&1; then
    fail "dotnet probe build" "$(tail -n 5 "$work/build.log" | tr '\n' ' ')"
    return
  fi
  output="$(sudo dotnet "$work/probe/docker-engine-probe.dll" /run/docker.sock "$fixtures" "$since" "$APP_ID")" || status=$?
  printf '%s\n' "$output"
  passed=$((passed + $(grep -c '^PASS' <<<"$output" || true)))
  failed=$((failed + $(grep -c '^FAIL' <<<"$output" || true)))
  if [[ "$status" -ne 0 && "$output" != *FAIL* ]]; then
    fail "dotnet probe" "exited with $status"
  fi
}

check_host() {
  local now
  now="$(dev_status)"
  if [[ "$now" == "$dev_before" && "$now" == "active 200 active active" ]]; then
    pass "host services unchanged" "pithyx-dev, http 200 on 7484, ssh and smbd as before ($now)"
  else
    fail "host services unchanged" "before: $dev_before, now: $now (pithyx-dev, http on 7484, ssh, smbd)"
  fi
  sudo nft list ruleset 2>/dev/null | cat >"$work/nft-after.txt"
  local changes
  changes="$(diff <(grep -E '^\s*(table|chain) ' "$work/nft-before.txt") <(grep -E '^\s*(table|chain) ' "$work/nft-after.txt") |
    grep -E '^[<>]' | tr -s ' \t' ' ' | tr '\n' ',' || true)"
  note "firewall" "${changes:-no tables or chains added or removed}; Docker keeps policy drop on forward and its DOCKER-* chains"
}

image_id=""
check_image
check_storage
check_userns
check_compose
check_network
check_cgroups
check_journal
check_dotnet
check_host

sudo chown -R "$(id -u):$(id -g)" "$fixtures"
if [[ "$record" == true ]]; then
  mkdir -p "$repo/daemon/tests/fixtures/docker"
  cp "$fixtures/docker-app.jsonl" "$repo/daemon/tests/fixtures/journal/docker-app.jsonl"
  find "$fixtures" -maxdepth 1 -type f ! -name docker-app.jsonl -exec cp -t "$repo/daemon/tests/fixtures/docker" {} +
  echo "docker-spike: recorded fixtures in daemon/tests/fixtures/docker and daemon/tests/fixtures/journal/docker-app.jsonl"
fi

echo "docker-spike: $passed passed, $failed failed"
[[ "$failed" -eq 0 ]] || exit 1
