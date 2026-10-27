---
title: 'Compose rules'
description: 'The Compose subset an app backend may use, generated from compose.v1.rules.json: allowed keys, value types, limits, patterns and refused keys.'
status: ready
audience: developer
sidebar:
  order: 3
---

An app backend is described by a Compose file in the package, usually `backend/compose.yaml`. The box accepts only a subset of Compose, defined by the allowlist `sdk/schema/compose.v1.rules.json` (published as `@pithyx/sdk/schema/compose.v1.rules.json`). The box and `pithyx validate` read the same file, so they judge a backend alike. Every key that is not listed below is refused with [`app.compose.key_unsupported`](/docs/reference/errors/#app.compose.key_unsupported). The tables are generated from the rules file each time the site is built; how a backend is built is explained in [Backends](/docs/developers/apps/backend/).

The box rewrites the file before it runs it: it sets the project name, the network, the user, logging, mounts, limits and labels itself, so those keys are refused here. The rules are a stable contract: allowing a key or loosening a limit is a minor change, anything else a major one.

## General rules

- The file is YAML without directives, anchors, aliases, tags, merge keys or duplicate keys ([`app.compose.yaml_invalid`](/docs/reference/errors/#app.compose.yaml_invalid)), at most the size, depth and number of entries in [Limits](#limits).
- Strings cannot contain control characters. `$` starts an interpolation, which is refused; write `$$` for a literal `$`.
- Every image is pinned by digest (`name@sha256:<64 hex>`, a tag may stand before the digest).
- A value that breaks a rule is [`app.compose.value_invalid`](/docs/reference/errors/#app.compose.value_invalid) with a `reason`: `type`, `format`, `range`, `required`, `duplicate`, `reserved`, `interpolation`, `unpinned`, `host_path`, `anonymous`, `no_value`, `cycle` or `image_and_build`.
- Names used across files must exist: services in `depends_on`, the manifest's endpoints and storage requests ([`app.compose.reference_unknown`](/docs/reference/errors/#app.compose.reference_unknown)), and every storage request must be mounted exactly by the services it lists, read-only for `read` access ([`app.compose.storage_mismatch`](/docs/reference/errors/#app.compose.storage_mismatch)).
- A key that needs an elevated right (column **Elevated right**) needs that right in the manifest's `backend.elevated`, and a declared right needs its key ([`app.compose.right_missing`](/docs/reference/errors/#app.compose.right_missing), [`app.compose.right_unused`](/docs/reference/errors/#app.compose.right_unused)). The box grants `host.network`, `host.pid`, devices and capabilities (see [Elevated rights](#elevated-rights)); `privileged` and the Docker socket are refused with [`app.elevated.unsupported`](/docs/reference/errors/#app.elevated.unsupported).

## Top-level keys

<!-- generated:compose-rules top -->

| Key        | Type                 | Required | Limits | Elevated right | Description                                                                                                                                |
| ---------- | -------------------- | -------- | ------ | -------------- | ------------------------------------------------------------------------------------------------------------------------------------------ |
| `services` | `services`           | yes      | –      | –              | The containers of the backend, by service name.                                                                                            |
| `volumes`  | `volumeDeclarations` | no       | –      | –              | Named volumes, declared without options. Each one is a folder in the app's dataset, or a storage request of the manifest with the same id. |

<!-- /generated -->

## Service keys

<!-- generated:compose-rules service -->

| Key                          | Type           | Required | Limits                                                          | Elevated right                               | Description                                                                                                  |
| ---------------------------- | -------------- | -------- | --------------------------------------------------------------- | -------------------------------------------- | ------------------------------------------------------------------------------------------------------------ |
| `image`                      | `image`        | yes      | –                                                               | –                                            | The image, pinned by digest. The box takes the image for its architecture from `backend/images.lock.json`.   |
| `command`                    | `command`      | no       | –                                                               | –                                            | Replaces the command of the image.                                                                           |
| `entrypoint`                 | `command`      | no       | –                                                               | –                                            | Replaces the entrypoint of the image.                                                                        |
| `environment`                | `environment`  | no       | –                                                               | –                                            | Environment variables of the container.                                                                      |
| `working_dir`                | `string`       | no       | Pattern `containerPath`                                         | –                                            | The working directory in the container.                                                                      |
| `volumes`                    | `mounts`       | no       | –                                                               | –                                            | Named volumes to mount, in the short form `VOLUME:TARGET[:ro\|rw]` or the long form. Host paths are refused. |
| `tmpfs`                      | `tmpfs`        | no       | –                                                               | –                                            | tmpfs mounts, `PATH[:size=<size>,mode=<octal>]`.                                                             |
| `depends_on`                 | `dependsOn`    | no       | –                                                               | –                                            | Services to start before this one.                                                                           |
| `healthcheck`                | `object`       | no       | –                                                               | –                                            | The health check of the container; it replaces the one of the image.                                         |
| `healthcheck.test`           | `healthTest`   | no       | –                                                               | –                                            | The check: `NONE`, a command for the shell, `[CMD, <program>, <argument>...]` or `[CMD-SHELL, <command>]`.   |
| `healthcheck.interval`       | `duration`     | no       | 1s to 1h                                                        | –                                            | The time between two checks.                                                                                 |
| `healthcheck.timeout`        | `duration`     | no       | 1s to 1h                                                        | –                                            | How long one check may take.                                                                                 |
| `healthcheck.start_period`   | `duration`     | no       | 0s to 1h                                                        | –                                            | The time after the start in which failed checks do not count.                                                |
| `healthcheck.start_interval` | `duration`     | no       | 1s to 1h                                                        | –                                            | The time between two checks during `start_period`.                                                           |
| `healthcheck.retries`        | `int`          | no       | 1 to 100                                                        | –                                            | Failed checks in a row after which the container counts as unhealthy.                                        |
| `healthcheck.disable`        | `bool`         | no       | –                                                               | –                                            | Switches the health check of the image off.                                                                  |
| `expose`                     | `ports`        | no       | –                                                               | –                                            | Ports the service listens on for the other services of the app. Nothing is published on the host.            |
| `read_only`                  | `bool`         | no       | –                                                               | –                                            | Mounts the root file system of the container read-only.                                                      |
| `init`                       | `bool`         | no       | –                                                               | –                                            | Runs an init process as PID 1 that forwards signals and reaps child processes.                               |
| `stop_signal`                | `string`       | no       | Pattern `signal`                                                | –                                            | The signal that stops the container, for example `SIGTERM` or `SIGINT`.                                      |
| `stop_grace_period`          | `duration`     | no       | 0s to 5m                                                        | –                                            | How long the box waits after the stop signal before it kills the container.                                  |
| `shm_size`                   | `byteSize`     | no       | at most 268435456 (256 MiB)                                     | –                                            | The size of `/dev/shm`.                                                                                      |
| `mem_limit`                  | `byteSize`     | no       | Pattern `memorySize`; 6291456 (6 MiB) to 274877906944 (256 GiB) | –                                            | The memory limit, with a unit.                                                                               |
| `cpus`                       | `cpus`         | no       | at most 256                                                     | –                                            | The CPU limit in cores, for example `0.5` or `2`.                                                            |
| `labels`                     | `labels`       | no       | –                                                               | –                                            | Labels of the container. Keys under `com.pithyx` and `com.docker` are reserved.                              |
| `network_mode`               | `enum`         | no       | `host`                                                          | `host.network` (using the key needs it)      | `host`: the container uses the host's network instead of the app's internal network.                         |
| `pid`                        | `enum`         | no       | `host`                                                          | `host.pid` (using the key needs it)          | `host`: the container sees the processes of the host.                                                        |
| `devices`                    | `devices`      | no       | –                                                               | `device:<path>` (using the key needs it)     | Device nodes, `HOST[:CONTAINER[:PERMISSIONS]]`; each device needs its own right `device:<host path>`.        |
| `cap_drop`                   | `capabilities` | no       | –                                                               | –                                            | Linux capabilities to drop, without the `CAP_` prefix.                                                       |
| `cap_add`                    | `capabilities` | no       | –                                                               | `capability:<NAME>` (using the key needs it) | Linux capabilities to add, without the `CAP_` prefix; each one needs its own right `capability:<NAME>`.      |
| `privileged`                 | `bool`         | no       | –                                                               | `host.full` (using the key needs it)         | A privileged container. The key needs `host.full`, which this version of Pithyx refuses.                     |

<!-- /generated -->

## Mounts in the long form

An entry of `volumes` in a service is either the short form `VOLUME:TARGET[:ro|rw]` or a map with these keys. The source is always a named volume declared under the top-level `volumes`; host paths and anonymous volumes are refused.

<!-- generated:compose-rules mount -->

| Key          | Type       | Required | Limits                      | Elevated right | Description                                                                        |
| ------------ | ---------- | -------- | --------------------------- | -------------- | ---------------------------------------------------------------------------------- |
| `type`       | `enum`     | yes      | `volume`, `tmpfs`, `bind`   | –              | `volume` or `tmpfs`. `bind` is refused: host paths are never mounted.              |
| `source`     | `string`   | no       | –                           | –              | The named volume, declared under the top-level `volumes`. Not allowed for `tmpfs`. |
| `target`     | `string`   | yes      | Pattern `containerPath`     | –              | The path in the container.                                                         |
| `read_only`  | `bool`     | no       | –                           | –              | Mounts the volume read-only. Required for a storage request with `read` access.    |
| `tmpfs`      | `object`   | no       | –                           | –              | Options of a `tmpfs` mount.                                                        |
| `tmpfs.size` | `byteSize` | no       | at most 268435456 (256 MiB) | –              | The size of the tmpfs.                                                             |

<!-- /generated -->

## depends_on in the long form

`depends_on` is a list of service names or a map from service name to these keys. Cycles are refused.

<!-- generated:compose-rules depends-on -->

| Key         | Type   | Required | Limits                                                                 | Elevated right | Description                                                                                                      |
| ----------- | ------ | -------- | ---------------------------------------------------------------------- | -------------- | ---------------------------------------------------------------------------------------------------------------- |
| `condition` | `enum` | no       | `service_started`, `service_healthy`, `service_completed_successfully` | –              | When the other service counts as ready: started (the default in Compose), healthy, or finished with exit code 0. |
| `restart`   | `bool` | no       | –                                                                      | –              | Restarts this service when Compose restarts the other one.                                                       |
| `required`  | `bool` | no       | –                                                                      | –              | As in Compose; the other service must still be in this file.                                                     |

<!-- /generated -->

## Reserved values

Mount targets that are refused (also `/` itself and everything below these), the Docker sockets that only a mount with the right shown may use, and the label prefixes an app cannot set:

<!-- generated:compose-rules reserved -->

- `/proc`, `/sys`, `/dev`
- `/var/run/docker.sock`, `/run/docker.sock`: `docker.socket`
- `com.pithyx`, `com.docker`

<!-- /generated -->

## Elevated rights

An administrator grants each right of `backend.elevated` after confirming with the password and, with two-factor sign-in on, an authenticator code, and can revoke it later; the box then leaves the key out and restarts the backend ([Apps and Store](/docs/admin/apps-and-store/#special-rights)). The lists below are generated from the rules file: the capabilities no app gets, the rights that run a service in the host's user namespace, and the devices that work inside the box's user namespace.

<!-- generated:compose-rules elevated -->

- `ALL`, `AUDIT_CONTROL`, `AUDIT_READ`, `BLOCK_SUSPEND`, `BPF`, `CHECKPOINT_RESTORE`, `DAC_READ_SEARCH`, `LINUX_IMMUTABLE`, `MAC_ADMIN`, `MAC_OVERRIDE`, `PERFMON`, `SYS_ADMIN`, `SYS_BOOT`, `SYS_MODULE`, `SYS_RAWIO`, `SYS_TIME`, `SYSLOG`, `WAKE_ALARM`
- `host.network`, `host.pid`
- `/dev/net/tun`

<!-- /generated -->

- A service with `network_mode: host` or `pid: host`, or with a device outside the last list, runs in the host's user namespace with the app's uid and the device's group, because Docker refuses the host's network and processes inside a user namespace and a device's group exists only on the host. Root there would be root on the box, so these rights cannot go together with `backend.root` ([`app.elevated.root_conflict`](/docs/reference/errors/#app.elevated.root_conflict)).
- A capability works only for root, so `capability:<NAME>` needs `backend.root` ([`app.elevated.root_required`](/docs/reference/errors/#app.elevated.root_required)); root stays unprivileged on the box through the user namespace.
- Device nodes matching the pattern `blockedDevice` (memory, consoles and terminals, disks, ZFS, input devices, watchdogs and others) are refused. When the backend starts, a device that is missing, is no character device or is a link to a blocked one stops the start with [`app.device.unavailable`](/docs/reference/errors/#app.device.unavailable).

## Source form compose.dev.yaml

`backend/compose.dev.yaml` is the source form that [`pithyx build`](/docs/reference/cli/build/) reads; the box never reads it. Every rule above applies, plus `build`, and each service has exactly one of `image` and `build`. `pithyx build` writes `compose.yaml` with each `build` replaced by the built image pinned by digest. A catalog builds from this file, and every `FROM` of its Dockerfiles must be pinned by digest.

<!-- generated:compose-rules dev-service -->

| Key     | Type    | Required | Limits | Elevated right | Description                                                                                                                                                          |
| ------- | ------- | -------- | ------ | -------------- | -------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `build` | `build` | no       | –      | –              | Where `pithyx build` builds the image of the service: a context folder relative to the file, or a map. `compose.yaml` gets the built image pinned by digest instead. |

<!-- /generated -->

`build` as a map:

<!-- generated:compose-rules dev-build -->

| Key          | Type        | Required | Limits                   | Elevated right | Description                                                   |
| ------------ | ----------- | -------- | ------------------------ | -------------- | ------------------------------------------------------------- |
| `context`    | `string`    | yes      | Pattern `buildPath`      | –              | The build context, a folder relative to the Compose file.     |
| `dockerfile` | `string`    | no       | Pattern `dockerfilePath` | –              | The Dockerfile relative to the context; default `Dockerfile`. |
| `target`     | `string`    | no       | Pattern `buildTarget`    | –              | The stage of a multi-stage Dockerfile to build.               |
| `args`       | `buildArgs` | no       | –                        | –              | Build arguments, as a map or as a list of `NAME=value`.       |

<!-- /generated -->

## Value types

<!-- generated:compose-rules types -->

| Type                 | Description                                                                                                                                                             |
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

## Limits

<!-- generated:compose-rules limits -->

| Name             | Value                  | Description                                                                               |
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

## Patterns

Each pattern matches the whole value.

<!-- generated:compose-rules patterns -->

| Name                | Pattern                                                                    | Description                                                                                                                                                                                                                                                                            |
| ------------------- | -------------------------------------------------------------------------- | -------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `serviceName`       | `^(?:[a-z0-9][a-z0-9_.-]{0,62})$`                                          | Service names.                                                                                                                                                                                                                                                                         |
| `volumeName`        | `^(?:[a-z0-9][a-z0-9_-]{0,62})$`                                           | Volume names.                                                                                                                                                                                                                                                                          |
| `image`             | a pattern of 194 characters, see the schema                                | Image references with an optional registry, an optional tag and the digest.                                                                                                                                                                                                            |
| `imageDigest`       | `^(?:.*@sha256:[a-f0-9]{64})$`                                             | Whether a reference is pinned by digest at all.                                                                                                                                                                                                                                        |
| `envName`           | `^(?:[A-Za-z_][A-Za-z0-9_]{0,127})$`                                       | Names of environment variables and build arguments.                                                                                                                                                                                                                                    |
| `labelKey`          | `^(?:[A-Za-z0-9](?:[A-Za-z0-9._-]{0,126}[A-Za-z0-9])?)$`                   | Label keys.                                                                                                                                                                                                                                                                            |
| `containerPath`     | `^(?:/(?:[^/\u0000-\u001f\u007f:\\]+(?:/[^/\u0000-\u001f\u007f:\\]+)*)?)$` | Absolute paths in a container, without `:`, `\` or control characters; `.` and `..` segments are refused as well.                                                                                                                                                                      |
| `devicePath`        | `^(?:/dev(?:/[A-Za-z0-9_][A-Za-z0-9._-]*)+)$`                              | Device paths below `/dev`.                                                                                                                                                                                                                                                             |
| `devicePermissions` | `^(?:r\|w\|rw)$`                                                           | Permissions of a device.                                                                                                                                                                                                                                                               |
| `capability`        | `^(?:(?!CAP_)[A-Z][A-Z0-9_]*)$`                                            | Linux capability names without the `CAP_` prefix.                                                                                                                                                                                                                                      |
| `blockedDevice`     | a pattern of 411 characters, see the schema                                | Device nodes no app gets, whatever it declares: memory, consoles and terminals, disks and partitions and their control nodes, ZFS, input devices, watchdogs, firmware and hardware buses (D311). The box also refuses any node that is not a character device when the backend starts. |
| `signal`            | `^(?:SIG[A-Z][A-Z0-9]*(?:\+[0-9]{1,2})?)$`                                 | Signal names, optionally with `+n` for a real-time signal.                                                                                                                                                                                                                             |
| `duration`          | a pattern of 97 characters, see the schema                                 | Durations.                                                                                                                                                                                                                                                                             |
| `byteSize`          | `^(?:[0-9]{1,12}(?:[bBkKmMgG]\|[kKmMgG][bB])?)$`                           | Sizes with an optional unit.                                                                                                                                                                                                                                                           |
| `port`              | `^(?:[0-9]{1,5}(?:-[0-9]{1,5})?(?:/(?:tcp\|udp))?)$`                       | Ports and port ranges with an optional protocol.                                                                                                                                                                                                                                       |
| `memorySize`        | `^(?:[0-9]{1,12}(?:[bBkKmMgG]\|[kKmMgG][bB]))$`                            | Sizes that must have a unit, for `mem_limit`.                                                                                                                                                                                                                                          |
| `cpus`              | `^(?:(?:0\|[1-9][0-9]{0,2})(?:\.[0-9]{1,3})?)$`                            | CPU counts.                                                                                                                                                                                                                                                                            |
| `tmpfsMode`         | `^(?:[0-7]{3,4})$`                                                         | The octal mode of a tmpfs.                                                                                                                                                                                                                                                             |
| `buildPath`         | a pattern of 94 characters, see the schema                                 | Build contexts relative to the Compose file, at most 16 segments.                                                                                                                                                                                                                      |
| `buildTarget`       | `^(?:[A-Za-z0-9][A-Za-z0-9_.-]{0,62})$`                                    | Names of Dockerfile stages.                                                                                                                                                                                                                                                            |
| `dockerfilePath`    | a pattern of 170 characters, see the schema                                | Dockerfile names: `Dockerfile` or `Containerfile` with an optional suffix, or `<name>.Dockerfile`, in at most 15 folders.                                                                                                                                                              |

<!-- /generated -->

## Refused keys

Keys that are refused on purpose, with the reason from the rules file. Any other key that is not allowed above is refused as well.

### Top level

<!-- generated:compose-rules refused-top -->

| Key        | Why it is refused                               |
| ---------- | ----------------------------------------------- |
| `name`     | the box sets the project name pithyx-&lt;id&gt; |
| `version`  | obsolete in Compose v2                          |
| `networks` | the box creates the app's network               |
| `secrets`  | reads files or the box's environment            |
| `configs`  | reads files or the box's environment            |
| `include`  | reads other files                               |
| `x-*`      | useless without anchors, which are refused      |

<!-- /generated -->

### Services

<!-- generated:compose-rules refused-service -->

| Key                   | Why it is refused                                            |
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

### Volume declarations

A volume declaration has no options: `volumes: { data: }` or `data: {}` is all there is.

<!-- generated:compose-rules refused-volume -->

| Key           | Why it is refused                        |
| ------------- | ---------------------------------------- |
| `driver`      | volumes are folders in the app's dataset |
| `driver_opts` | could mount any device or host path      |
| `external`    | reaches volumes outside the app          |
| `name`        | reaches volumes outside the app          |
| `labels`      | not needed                               |

<!-- /generated -->

### Mounts

<!-- generated:compose-rules refused-mount -->

| Key           | Why it is refused                 |
| ------------- | --------------------------------- |
| `bind`        | host paths are never mounted      |
| `volume`      | subpath and nocopy are not needed |
| `consistency` | not on Linux                      |

<!-- /generated -->
