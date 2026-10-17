---
title: 'Compose-Regeln'
description: 'Die Teilmenge von Compose, die ein App-Backend nutzen darf, erzeugt aus compose.v1.rules.json: erlaubte Schlüssel, Werttypen, Grenzen, Muster und abgelehnte Schlüssel.'
status: ready
audience: developer
sidebar:
  order: 3
---

Ein App-Backend wird durch eine Compose-Datei im Paket beschrieben, meist `backend/compose.yaml`. Die Box akzeptiert nur eine Teilmenge von Compose, festgelegt durch die Erlaubnisliste `sdk/schema/compose.v1.rules.json` (veröffentlicht als `@pithyx/sdk/schema/compose.v1.rules.json`). Die Box und `pithyx validate` lesen dieselbe Datei und urteilen deshalb gleich über ein Backend. Jeder Schlüssel, der unten nicht steht, wird mit [`app.compose.key_unsupported`](/de/docs/reference/errors/#app.compose.key_unsupported) abgelehnt. Die Tabellen werden bei jedem Build der Website aus der Regeldatei erzeugt; ihre Beschreibungen und Begründungen sind englisch. Wie man ein Backend baut, erklärt [Backends](/de/docs/developers/apps/backend/).

Die Box schreibt die Datei um, bevor sie sie startet: Projektname, Netzwerk, Benutzer, Logging, Mounts, Grenzen und Labels setzt sie selbst, deshalb werden diese Schlüssel hier abgelehnt. Die Regeln sind ein stabiler Vertrag: Einen Schlüssel zu erlauben oder eine Grenze zu lockern ist eine Minor-Änderung, alles andere eine Major-Änderung.

## Allgemeine Regeln

- Die Datei ist YAML ohne Direktiven, Anker, Aliase, Tags, Merge-Keys oder doppelte Schlüssel ([`app.compose.yaml_invalid`](/de/docs/reference/errors/#app.compose.yaml_invalid)), höchstens so groß, so tief und mit so vielen Einträgen wie unter [Grenzen](#grenzen).
- Strings dürfen keine Steuerzeichen enthalten. `$` beginnt eine Interpolation, die abgelehnt wird; schreib `$$` für ein wörtliches `$`.
- Jedes Image ist per Digest gepinnt (`name@sha256:<64 hex>`, ein Tag darf vor dem Digest stehen).
- Ein Wert, der eine Regel verletzt, ist [`app.compose.value_invalid`](/de/docs/reference/errors/#app.compose.value_invalid) mit einem `reason`: `type`, `format`, `range`, `required`, `duplicate`, `reserved`, `interpolation`, `unpinned`, `host_path`, `anonymous`, `no_value`, `cycle` oder `image_and_build`.
- Namen, die über Dateien hinweg genutzt werden, müssen existieren: Dienste in `depends_on`, die Endpunkte und Speicheranfragen des Manifests ([`app.compose.reference_unknown`](/de/docs/reference/errors/#app.compose.reference_unknown)), und jede Speicheranfrage muss genau von den Diensten eingebunden werden, die sie nennt, bei `read`-Zugriff schreibgeschützt ([`app.compose.storage_mismatch`](/de/docs/reference/errors/#app.compose.storage_mismatch)).
- Ein Schlüssel, der ein erhöhtes Recht braucht, wird in dieser Version (Pithyx 1.0.0-beta.1) mit [`app.elevated.unsupported`](/de/docs/reference/errors/#app.elevated.unsupported) ab.

## Schlüssel der obersten Ebene

<!-- generated:compose-rules top -->

| Schlüssel  | Typ                  | Pflicht | Grenzen | Erweitertes Recht | Beschreibung                                                                                                                               |
| ---------- | -------------------- | ------- | ------- | ----------------- | ------------------------------------------------------------------------------------------------------------------------------------------ |
| `services` | `services`           | ja      | –       | –                 | The containers of the backend, by service name.                                                                                            |
| `volumes`  | `volumeDeclarations` | nein    | –       | –                 | Named volumes, declared without options. Each one is a folder in the app's dataset, or a storage request of the manifest with the same id. |

<!-- /generated -->

## Schlüssel eines Dienstes

<!-- generated:compose-rules service -->

| Schlüssel                    | Typ            | Pflicht | Grenzen                                                         | Erweitertes Recht                              | Beschreibung                                                                                                 |
| ---------------------------- | -------------- | ------- | --------------------------------------------------------------- | ---------------------------------------------- | ------------------------------------------------------------------------------------------------------------ |
| `image`                      | `image`        | ja      | –                                                               | –                                              | The image, pinned by digest. The box takes the image for its architecture from `backend/images.lock.json`.   |
| `command`                    | `command`      | nein    | –                                                               | –                                              | Replaces the command of the image.                                                                           |
| `entrypoint`                 | `command`      | nein    | –                                                               | –                                              | Replaces the entrypoint of the image.                                                                        |
| `environment`                | `environment`  | nein    | –                                                               | –                                              | Environment variables of the container.                                                                      |
| `working_dir`                | `string`       | nein    | Muster `containerPath`                                          | –                                              | The working directory in the container.                                                                      |
| `volumes`                    | `mounts`       | nein    | –                                                               | –                                              | Named volumes to mount, in the short form `VOLUME:TARGET[:ro\|rw]` or the long form. Host paths are refused. |
| `tmpfs`                      | `tmpfs`        | nein    | –                                                               | –                                              | tmpfs mounts, `PATH[:size=<size>,mode=<octal>]`.                                                             |
| `depends_on`                 | `dependsOn`    | nein    | –                                                               | –                                              | Services to start before this one.                                                                           |
| `healthcheck`                | `object`       | nein    | –                                                               | –                                              | The health check of the container; it replaces the one of the image.                                         |
| `healthcheck.test`           | `healthTest`   | nein    | –                                                               | –                                              | The check: `NONE`, a command for the shell, `[CMD, <program>, <argument>...]` or `[CMD-SHELL, <command>]`.   |
| `healthcheck.interval`       | `duration`     | nein    | 1s bis 1h                                                       | –                                              | The time between two checks.                                                                                 |
| `healthcheck.timeout`        | `duration`     | nein    | 1s bis 1h                                                       | –                                              | How long one check may take.                                                                                 |
| `healthcheck.start_period`   | `duration`     | nein    | 0s bis 1h                                                       | –                                              | The time after the start in which failed checks do not count.                                                |
| `healthcheck.start_interval` | `duration`     | nein    | 1s bis 1h                                                       | –                                              | The time between two checks during `start_period`.                                                           |
| `healthcheck.retries`        | `int`          | nein    | 1 bis 100                                                       | –                                              | Failed checks in a row after which the container counts as unhealthy.                                        |
| `healthcheck.disable`        | `bool`         | nein    | –                                                               | –                                              | Switches the health check of the image off.                                                                  |
| `expose`                     | `ports`        | nein    | –                                                               | –                                              | Ports the service listens on for the other services of the app. Nothing is published on the host.            |
| `read_only`                  | `bool`         | nein    | –                                                               | –                                              | Mounts the root file system of the container read-only.                                                      |
| `init`                       | `bool`         | nein    | –                                                               | –                                              | Runs an init process as PID 1 that forwards signals and reaps child processes.                               |
| `stop_signal`                | `string`       | nein    | Muster `signal`                                                 | –                                              | The signal that stops the container, for example `SIGTERM` or `SIGINT`.                                      |
| `stop_grace_period`          | `duration`     | nein    | 0s bis 5m                                                       | –                                              | How long the box waits after the stop signal before it kills the container.                                  |
| `shm_size`                   | `byteSize`     | nein    | höchstens 268435456 (256 MiB)                                   | –                                              | The size of `/dev/shm`.                                                                                      |
| `mem_limit`                  | `byteSize`     | nein    | Muster `memorySize`; 6291456 (6 MiB) bis 274877906944 (256 GiB) | –                                              | The memory limit, with a unit.                                                                               |
| `cpus`                       | `cpus`         | nein    | höchstens 256                                                   | –                                              | The CPU limit in cores, for example `0.5` or `2`.                                                            |
| `labels`                     | `labels`       | nein    | –                                                               | –                                              | Labels of the container. Keys under `com.pithyx` and `com.docker` are reserved.                              |
| `network_mode`               | `enum`         | nein    | `host`                                                          | `host.network` (der Schlüssel braucht es)      | `host`: the container uses the host's network instead of the app's internal network.                         |
| `pid`                        | `enum`         | nein    | `host`                                                          | `host.pid` (der Schlüssel braucht es)          | `host`: the container sees the processes of the host.                                                        |
| `devices`                    | `devices`      | nein    | –                                                               | `device:<path>` (der Schlüssel braucht es)     | Device nodes, `HOST[:CONTAINER[:PERMISSIONS]]`; each device needs its own right `device:<host path>`.        |
| `cap_drop`                   | `capabilities` | nein    | –                                                               | –                                              | Linux capabilities to drop, without the `CAP_` prefix.                                                       |
| `cap_add`                    | `capabilities` | nein    | –                                                               | `capability:<NAME>` (der Schlüssel braucht es) | Linux capabilities to add, without the `CAP_` prefix; each one needs its own right `capability:<NAME>`.      |
| `privileged`                 | `bool`         | nein    | –                                                               | `host.full` (der Schlüssel braucht es)         | A privileged container. The key needs `host.full`, which this version of Pithyx refuses.                     |

<!-- /generated -->

## Mounts in der Langform

Ein Eintrag von `volumes` in einem Dienst ist entweder die Kurzform `VOLUME:TARGET[:ro|rw]` oder eine Map mit diesen Schlüsseln. Die Quelle ist immer ein benanntes Volume, das unter `volumes` auf oberster Ebene deklariert ist; Host-Pfade und anonyme Volumes werden abgelehnt.

<!-- generated:compose-rules mount -->

| Schlüssel    | Typ        | Pflicht | Grenzen                       | Erweitertes Recht | Beschreibung                                                                       |
| ------------ | ---------- | ------- | ----------------------------- | ----------------- | ---------------------------------------------------------------------------------- |
| `type`       | `enum`     | ja      | `volume`, `tmpfs`, `bind`     | –                 | `volume` or `tmpfs`. `bind` is refused: host paths are never mounted.              |
| `source`     | `string`   | nein    | –                             | –                 | The named volume, declared under the top-level `volumes`. Not allowed for `tmpfs`. |
| `target`     | `string`   | ja      | Muster `containerPath`        | –                 | The path in the container.                                                         |
| `read_only`  | `bool`     | nein    | –                             | –                 | Mounts the volume read-only. Required for a storage request with `read` access.    |
| `tmpfs`      | `object`   | nein    | –                             | –                 | Options of a `tmpfs` mount.                                                        |
| `tmpfs.size` | `byteSize` | nein    | höchstens 268435456 (256 MiB) | –                 | The size of the tmpfs.                                                             |

<!-- /generated -->

## depends_on in der Langform

`depends_on` ist eine Liste von Dienstnamen oder eine Map vom Dienstnamen auf diese Schlüssel. Zyklen werden abgelehnt.

<!-- generated:compose-rules depends-on -->

| Schlüssel   | Typ    | Pflicht | Grenzen                                                                | Erweitertes Recht | Beschreibung                                                                                                     |
| ----------- | ------ | ------- | ---------------------------------------------------------------------- | ----------------- | ---------------------------------------------------------------------------------------------------------------- |
| `condition` | `enum` | nein    | `service_started`, `service_healthy`, `service_completed_successfully` | –                 | When the other service counts as ready: started (the default in Compose), healthy, or finished with exit code 0. |
| `restart`   | `bool` | nein    | –                                                                      | –                 | Restarts this service when Compose restarts the other one.                                                       |
| `required`  | `bool` | nein    | –                                                                      | –                 | As in Compose; the other service must still be in this file.                                                     |

<!-- /generated -->

## Reservierte Werte

Mount-Ziele, die abgelehnt werden (auch `/` selbst und alles darunter), die Docker-Sockets, die nur ein Mount mit dem genannten Recht nutzen darf, und die Label-Präfixe, die eine App nicht setzen darf:

<!-- generated:compose-rules reserved -->

- `/proc`, `/sys`, `/dev`
- `/var/run/docker.sock`, `/run/docker.sock`: `docker.socket`
- `com.pithyx`, `com.docker`

<!-- /generated -->

## Quellform compose.dev.yaml

`backend/compose.dev.yaml` ist die Quellform, die [`pithyx build`](/de/docs/reference/cli/build/) liest; die Box liest sie nie. Jede Regel oben gilt, dazu `build`, und jeder Dienst hat genau eines von `image` und `build`. `pithyx build` schreibt `compose.yaml`, in der jedes `build` durch das gebaute, per Digest gepinnte Image ersetzt ist. Ein Katalog baut aus dieser Datei, und jedes `FROM` ihrer Dockerfiles muss per Digest gepinnt sein.

<!-- generated:compose-rules dev-service -->

| Schlüssel | Typ     | Pflicht | Grenzen | Erweitertes Recht | Beschreibung                                                                                                                                                         |
| --------- | ------- | ------- | ------- | ----------------- | -------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `build`   | `build` | nein    | –       | –                 | Where `pithyx build` builds the image of the service: a context folder relative to the file, or a map. `compose.yaml` gets the built image pinned by digest instead. |

<!-- /generated -->

`build` als Map:

<!-- generated:compose-rules dev-build -->

| Schlüssel    | Typ         | Pflicht | Grenzen                 | Erweitertes Recht | Beschreibung                                                  |
| ------------ | ----------- | ------- | ----------------------- | ----------------- | ------------------------------------------------------------- |
| `context`    | `string`    | ja      | Muster `buildPath`      | –                 | The build context, a folder relative to the Compose file.     |
| `dockerfile` | `string`    | nein    | Muster `dockerfilePath` | –                 | The Dockerfile relative to the context; default `Dockerfile`. |
| `target`     | `string`    | nein    | Muster `buildTarget`    | –                 | The stage of a multi-stage Dockerfile to build.               |
| `args`       | `buildArgs` | nein    | –                       | –                 | Build arguments, as a map or as a list of `NAME=value`.       |

<!-- /generated -->

## Werttypen

<!-- generated:compose-rules types -->

| Typ                  | Beschreibung                                                                                                                                                            |
| -------------------- | ----------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `services`           | A map from service name (pattern `serviceName`) to a service, at least one and at most `services` entries.                                                              |
| `volumeDeclarations` | A map from volume name (pattern `volumeName`) to an empty value (`data:` or `data: {}`), at most `volumes` entries. Options are refused.                                |
| `image`              | A string matching `image`. Without `@sha256:` it is refused as `unpinned`; longer than `imageChars` is refused.                                                         |
| `command`            | A string, or a list of strings.                                                                                                                                         |
| `environment`        | A map from name to a string, a whole number or a boolean, or a list of `NAME=value`. Names match `envName` and appear once; every variable has a value.                 |
| `labels`             | A map from key to a string, a whole number, a boolean or nothing, or a list of `key=value`. Keys match `labelKey`, appear once and do not start with a reserved prefix. |
| `string`             | A string. A plain scalar that YAML reads as null, a boolean or a number is refused: quote it. With a pattern, the string must match it.                                 |
| `bool`               | `true` or `false`.                                                                                                                                                      |
| `int`                | A whole number between the rule's minimum and maximum.                                                                                                                  |
| `enum`               | One of the listed strings.                                                                                                                                              |
| `object`             | A map with only the listed keys.                                                                                                                                        |
| `duration`           | A Compose duration such as `30s`, `1m30s` or `500ms` (units `h`, `m`, `s`, `ms`, `us`), between the rule's minimum and maximum.                                         |
| `byteSize`           | A number of bytes, or a number with the unit `b`, `k`, `m` or `g` (also `kb`, `mb`, `gb`; powers of 1024), above 0 and at most the rule's maximum.                      |
| `cpus`               | A number of cores above 0 with at most three decimals, as a number or a string, at most the limit `cpus`.                                                               |
| `mounts`             | A list of mounts, each in the short form `VOLUME:TARGET[:ro\|rw]` or the long form. Two mounts of a service cannot share a target.                                      |
| `tmpfs`              | A path, or a list of `PATH[:OPTIONS]`. The only options are `size` (a byte size up to `tmpfsSizeBytes`) and `mode` (octal).                                             |
| `dependsOn`          | A list of service names, or a map from service name to the long form.                                                                                                   |
| `healthTest`         | A string for the shell, or a list: `[NONE]`, `[CMD, <program>, <argument>...]` or `[CMD-SHELL, <command>]`.                                                             |
| `ports`              | A list of ports or port ranges `a-b`, optionally with `/tcp` or `/udp`, from 1 to 65535.                                                                                |
| `devices`            | A list of `HOST[:CONTAINER[:PERMISSIONS]]` with paths matching `devicePath` and permissions `r`, `w` or `rw`. Each host device appears once.                            |
| `capabilities`       | A list of capability names matching `capability`, each once.                                                                                                            |
| `build`              | A context folder as a string, or a map with the keys of `build`.                                                                                                        |
| `buildArgs`          | Like `environment`.                                                                                                                                                     |

<!-- /generated -->

## Grenzen

<!-- generated:compose-rules limits -->

| Name             | Wert                   | Beschreibung                                                                              |
| ---------------- | ---------------------- | ----------------------------------------------------------------------------------------- |
| `fileBytes`      | 65536 (64 KiB)         | The largest Compose file, in bytes.                                                       |
| `depth`          | 10                     | The deepest nesting of maps and lists.                                                    |
| `entries`        | 256                    | The most entries of one list or map inside a service, such as `environment` or `volumes`. |
| `services`       | 16                     | The most services.                                                                        |
| `volumes`        | 32                     | The most declared volumes.                                                                |
| `shmSizeBytes`   | 268435456 (256 MiB)    | The largest `shm_size`.                                                                   |
| `tmpfsSizeBytes` | 268435456 (256 MiB)    | The largest tmpfs, in a `tmpfs` entry or a long-form mount.                               |
| `imageChars`     | 512                    | The longest image reference.                                                              |
| `memoryMinBytes` | 6291456 (6 MiB)        | The smallest `mem_limit`.                                                                 |
| `memoryMaxBytes` | 274877906944 (256 GiB) | The largest `mem_limit`.                                                                  |
| `cpus`           | 256                    | The largest `cpus`.                                                                       |

<!-- /generated -->

## Muster

Jedes Muster muss auf den ganzen Wert passen.

<!-- generated:compose-rules patterns -->

| Name                | Muster                                                                     | Beschreibung                                                                                                              |
| ------------------- | -------------------------------------------------------------------------- | ------------------------------------------------------------------------------------------------------------------------- |
| `serviceName`       | `^(?:[a-z0-9][a-z0-9_.-]{0,62})$`                                          | Service names.                                                                                                            |
| `volumeName`        | `^(?:[a-z0-9][a-z0-9_-]{0,62})$`                                           | Volume names.                                                                                                             |
| `image`             | ein Muster aus 194 Zeichen, siehe das Schema                               | Image references with an optional registry, an optional tag and the digest.                                               |
| `imageDigest`       | `^(?:.*@sha256:[a-f0-9]{64})$`                                             | Whether a reference is pinned by digest at all.                                                                           |
| `envName`           | `^(?:[A-Za-z_][A-Za-z0-9_]{0,127})$`                                       | Names of environment variables and build arguments.                                                                       |
| `labelKey`          | `^(?:[A-Za-z0-9](?:[A-Za-z0-9._-]{0,126}[A-Za-z0-9])?)$`                   | Label keys.                                                                                                               |
| `containerPath`     | `^(?:/(?:[^/\u0000-\u001f\u007f:\\]+(?:/[^/\u0000-\u001f\u007f:\\]+)*)?)$` | Absolute paths in a container, without `:`, `\` or control characters; `.` and `..` segments are refused as well.         |
| `devicePath`        | `^(?:/dev(?:/[A-Za-z0-9_][A-Za-z0-9._-]*)+)$`                              | Device paths below `/dev`.                                                                                                |
| `devicePermissions` | `^(?:r\|w\|rw)$`                                                           | Permissions of a device.                                                                                                  |
| `capability`        | `^(?:(?!CAP_)[A-Z][A-Z0-9_]*)$`                                            | Linux capability names without the `CAP_` prefix.                                                                         |
| `signal`            | `^(?:SIG[A-Z][A-Z0-9]*(?:\+[0-9]{1,2})?)$`                                 | Signal names, optionally with `+n` for a real-time signal.                                                                |
| `duration`          | ein Muster aus 97 Zeichen, siehe das Schema                                | Durations.                                                                                                                |
| `byteSize`          | `^(?:[0-9]{1,12}(?:[bBkKmMgG]\|[kKmMgG][bB])?)$`                           | Sizes with an optional unit.                                                                                              |
| `port`              | `^(?:[0-9]{1,5}(?:-[0-9]{1,5})?(?:/(?:tcp\|udp))?)$`                       | Ports and port ranges with an optional protocol.                                                                          |
| `memorySize`        | `^(?:[0-9]{1,12}(?:[bBkKmMgG]\|[kKmMgG][bB]))$`                            | Sizes that must have a unit, for `mem_limit`.                                                                             |
| `cpus`              | `^(?:(?:0\|[1-9][0-9]{0,2})(?:\.[0-9]{1,3})?)$`                            | CPU counts.                                                                                                               |
| `tmpfsMode`         | `^(?:[0-7]{3,4})$`                                                         | The octal mode of a tmpfs.                                                                                                |
| `buildPath`         | ein Muster aus 94 Zeichen, siehe das Schema                                | Build contexts relative to the Compose file, at most 16 segments.                                                         |
| `buildTarget`       | `^(?:[A-Za-z0-9][A-Za-z0-9_.-]{0,62})$`                                    | Names of Dockerfile stages.                                                                                               |
| `dockerfilePath`    | ein Muster aus 170 Zeichen, siehe das Schema                               | Dockerfile names: `Dockerfile` or `Containerfile` with an optional suffix, or `<name>.Dockerfile`, in at most 15 folders. |

<!-- /generated -->

## Abgelehnte Schlüssel

Schlüssel, die absichtlich abgelehnt werden, mit der Begründung aus der Regeldatei. Jeder andere Schlüssel, der oben nicht erlaubt ist, wird ebenfalls abgelehnt.

### Oberste Ebene

<!-- generated:compose-rules refused-top -->

| Schlüssel  | Warum er abgelehnt wird                         |
| ---------- | ----------------------------------------------- |
| `name`     | the box sets the project name pithyx-&lt;id&gt; |
| `version`  | obsolete in Compose v2                          |
| `networks` | the box creates the app's network               |
| `secrets`  | reads files or the box's environment            |
| `configs`  | reads files or the box's environment            |
| `include`  | reads other files                               |
| `x-*`      | useless without anchors, which are refused      |

<!-- /generated -->

### Dienste

<!-- generated:compose-rules refused-service -->

| Schlüssel             | Warum er abgelehnt wird                                      |
| --------------------- | ------------------------------------------------------------ |
| `ports`               | backends are reached only through the proxy                  |
| `build`               | the box never builds images                                  |
| `networks`            | the box attaches the app's network                           |
| `links`               | service names resolve on the app's network                   |
| `external_links`      | reaches other projects                                       |
| `volumes_from`        | can reach another app's volumes                              |
| `env_file`            | reads files                                                  |
| `secrets`             | reads files or the box's environment                         |
| `configs`             | reads files or the box's environment                         |
| `label_file`          | reads files                                                  |
| `extends`             | reads other files                                            |
| `profiles`            | every service runs                                           |
| `container_name`      | the box names containers per project                         |
| `user`                | the box sets the app uid, or root with backend.root          |
| `group_add`           | the box adds the share groups of mapped storage              |
| `restart`             | the box runs the lifecycle                                   |
| `logging`             | the box sets journald                                        |
| `deploy`              | use mem_limit and cpus                                       |
| `scale`               | one container per service                                    |
| `mem_reservation`     | use mem_limit                                                |
| `memswap_limit`       | use mem_limit                                                |
| `mem_swappiness`      | box policy                                                   |
| `cpu_shares`          | use cpus                                                     |
| `cpu_quota`           | use cpus                                                     |
| `cpu_period`          | use cpus                                                     |
| `cpuset`              | box policy                                                   |
| `pids_limit`          | the box sets 1024 against fork bombs                         |
| `blkio_config`        | box policy                                                   |
| `oom_kill_disable`    | can stall the host                                           |
| `oom_score_adj`       | the box sets it so app containers go before its own services |
| `security_opt`        | could switch off seccomp, AppArmor or no-new-privileges      |
| `device_cgroup_rules` | could open every device                                      |
| `gpus`                | needs a runtime the box does not ship                        |
| `runtime`             | needs a runtime the box does not ship                        |
| `isolation`           | not on Linux                                                 |
| `userns_mode`         | host would switch off the user namespace                     |
| `ipc`                 | host or another container's IPC namespace                    |
| `uts`                 | host's UTS namespace                                         |
| `cgroup`              | host's cgroup namespace                                      |
| `cgroup_parent`       | would leave the app's limits                                 |
| `sysctls`             | kernel settings                                              |
| `ulimits`             | box policy                                                   |
| `storage_opt`         | box policy                                                   |
| `extra_hosts`         | host-gateway reaches the box                                 |
| `dns`                 | the app's network resolves service names                     |
| `dns_search`          | see dns                                                      |
| `dns_opt`             | see dns                                                      |
| `hostname`            | not needed on the app's network                              |
| `domainname`          | not needed on the app's network                              |
| `mac_address`         | network identity                                             |
| `platform`            | images come per architecture from images.lock.json           |
| `pull_policy`         | the box loads or pulls by digest itself                      |
| `tty`                 | no terminal                                                  |
| `stdin_open`          | no terminal                                                  |
| `attach`              | no terminal                                                  |
| `annotations`         | not needed                                                   |
| `develop`             | no development mode on the box                               |
| `post_start`          | not in Compose 2.26                                          |
| `pre_stop`            | not in Compose 2.26                                          |
| `x-*`                 | useless without anchors, which are refused                   |

<!-- /generated -->

### Volume-Deklarationen

Eine Volume-Deklaration hat keine Optionen: `volumes: { data: }` oder `data: {}` ist alles.

<!-- generated:compose-rules refused-volume -->

| Schlüssel     | Warum er abgelehnt wird                  |
| ------------- | ---------------------------------------- |
| `driver`      | volumes are folders in the app's dataset |
| `driver_opts` | could mount any device or host path      |
| `external`    | reaches volumes outside the app          |
| `name`        | reaches volumes outside the app          |
| `labels`      | not needed                               |

<!-- /generated -->

### Mounts

<!-- generated:compose-rules refused-mount -->

| Schlüssel     | Warum er abgelehnt wird           |
| ------------- | --------------------------------- |
| `bind`        | host paths are never mounted      |
| `volume`      | subpath and nocopy are not needed |
| `consistency` | not on Linux                      |

<!-- /generated -->
