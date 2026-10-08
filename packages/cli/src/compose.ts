import type { AppManifest } from '@pithyx/sdk';
import rules from '@pithyx/sdk/schema/compose.v1.rules.json' with { type: 'json' };
import {
  escapePointer,
  readComposeYaml,
  scalarKind,
  type YamlMap,
  type YamlNode,
} from './compose-yaml';
import type { Issue } from './issues';
import { isSupportedElevatedRight } from './manifest';

/** The allowlist of `@pithyx/sdk/schema/compose.v1.rules.json`, as both the CLI and the box read it. */
export const COMPOSE_RULES = rules;

interface KeyRule {
  type: string;
  required?: boolean;
  pattern?: string;
  values?: string[];
  min?: number | string;
  max?: number | string;
  right?: string;
  keys?: Record<string, KeyRule>;
}

type Limits = typeof rules.limits;

const patterns = Object.fromEntries(
  Object.entries(rules.patterns).map(([name, pattern]) => [name, new RegExp(pattern)]),
) as Record<keyof typeof rules.patterns, RegExp>;

const DURATION_PART = /([0-9]+)(ms|us|h|m|s)/g;
const DURATION_UNITS: Record<string, number> = {
  h: 3_600_000_000,
  m: 60_000_000,
  s: 1_000_000,
  ms: 1_000,
  us: 1,
};
const BYTE_UNITS: Record<string, number> = { '': 1, b: 1, k: 1024, m: 1024 ** 2, g: 1024 ** 3 };
// Control characters other than tab and line feed, C1 controls and the Unicode line separators.
// eslint-disable-next-line no-control-regex
const CONTROL = /[\u0000-\u0008\u000b-\u001f\u007f-\u009f\u2028\u2029]/;

export interface ComposeValidation {
  /** Empty when the Compose file is valid for the manifest. */
  issues: Issue[];
}

interface Mount {
  volume: string;
  path: string;
  readOnly: boolean;
}

/** A mount as written in the Compose file: a named volume (`volume`) or a tmpfs. */
interface ParsedMount {
  target: string;
  volume?: string;
  readOnly: boolean;
}

interface Reference {
  name: string;
  path: string;
}

interface ServiceFacts {
  name: string;
  mounts: Mount[];
  dependsOn: Reference[];
}

/**
 * Checks the bytes of a backend's Compose file against the allowlist and against the manifest it belongs to, with the
 * same verdicts and codes as the box (the corpus in `@pithyx/sdk/schema/fixtures/compose` holds both alike). The
 * manifest must already be valid. With `dev` the file is `backend/compose.dev.yaml`, the source form of `pithyx build`,
 * where a service has `build` instead of `image` (`dev` in the rules).
 */
export function validateCompose(
  bytes: Uint8Array,
  manifest: AppManifest,
  options: { dev?: boolean } = {},
): ComposeValidation {
  const yaml = readComposeYaml(bytes, rules.limits);
  if (yaml.root === undefined) {
    return { issues: yaml.issues };
  }
  return { issues: new Checker(manifest, options.dev ?? false).check(yaml.root) };
}

/** The path of the source form next to a manifest's `backend.compose`: `backend/compose.yaml` gives `backend/compose.dev.yaml`. */
export function devComposePath(compose: string): string {
  const dot = compose.lastIndexOf('.');
  return `${compose.slice(0, dot)}.dev${compose.slice(dot)}`;
}

class Checker {
  private readonly issues: Issue[] = [];
  private readonly rights: Reference[] = [];
  private declaredVolumes = new Set<string>();

  private readonly serviceRules: Record<string, KeyRule>;

  constructor(
    private readonly manifest: AppManifest,
    private readonly dev: boolean,
  ) {
    this.serviceRules = dev ? { ...rules.service, ...rules.dev.service } : rules.service;
  }

  check(root: YamlNode): Issue[] {
    if (root.kind !== 'map') {
      this.invalid('', 'type');
      return this.issues;
    }
    const top = rules.top as Record<string, KeyRule>;
    for (const { key } of root.entries) {
      if (!(key in top)) {
        this.unsupported(`/${escapePointer(key)}`, key);
      }
    }
    const volumes = entry(root, 'volumes');
    if (volumes !== undefined) {
      this.volumeDeclarations(volumes);
    }
    const services = entry(root, 'services');
    if (services === undefined) {
      this.invalid('/services', 'required');
      return this.issues;
    }
    const facts = this.services(services);
    if (facts !== undefined) {
      this.crossChecks(facts);
    }
    return this.issues;
  }

  private add(code: Issue['code'], params: Issue['params']): void {
    this.issues.push({ code, params });
  }

  private invalid(path: string, reason: string): void {
    this.add('app.compose.value_invalid', { path, reason });
  }

  private mismatch(path: string, storage: string, reason: string): void {
    this.add('app.compose.storage_mismatch', { path, storage, reason });
  }

  private unsupported(path: string, key: string): void {
    this.add('app.compose.key_unsupported', { path, key });
  }

  private volumeDeclarations(node: YamlNode): void {
    if (node.kind !== 'map') {
      this.invalid('/volumes', 'type');
      return;
    }
    if (node.entries.length > rules.limits.volumes) {
      this.invalid('/volumes', 'range');
      return;
    }
    for (const { key, value } of node.entries) {
      const path = `/volumes/${escapePointer(key)}`;
      if (!patterns.volumeName.test(key)) {
        this.invalid(path, 'format');
        continue;
      }
      this.declaredVolumes.add(key);
      if (value.kind === 'map') {
        for (const option of value.entries) {
          this.unsupported(`${path}/${escapePointer(option.key)}`, option.key);
        }
      } else if (value.kind !== 'scalar' || scalarKind(value) !== 'null') {
        this.invalid(path, 'type');
      }
    }
  }

  private services(node: YamlNode): ServiceFacts[] | undefined {
    if (node.kind !== 'map') {
      this.invalid('/services', 'type');
      return undefined;
    }
    if (node.entries.length === 0 || node.entries.length > rules.limits.services) {
      this.invalid('/services', 'range');
      return undefined;
    }
    return node.entries.map(({ key, value }) => {
      const path = `/services/${escapePointer(key)}`;
      if (!patterns.serviceName.test(key)) {
        this.invalid(path, 'format');
      }
      const facts: ServiceFacts = { name: key, mounts: [], dependsOn: [] };
      if (value.kind !== 'map') {
        this.invalid(path, 'type');
      } else {
        this.service(value, path, facts);
      }
      return facts;
    });
  }

  private service(node: YamlMap, path: string, facts: ServiceFacts): void {
    const serviceRules = this.serviceRules;
    const targets = new Set<string>();
    for (const { key, value } of node.entries) {
      const rule = serviceRules[key];
      const keyPath = `${path}/${escapePointer(key)}`;
      if (rule === undefined) {
        this.unsupported(keyPath, key);
        continue;
      }
      switch (rule.type) {
        case 'image':
          this.image(value, keyPath);
          break;
        case 'build':
          this.build(value, keyPath);
          break;
        case 'command':
          this.command(value, keyPath);
          break;
        case 'environment':
          this.keyValues(value, keyPath, true);
          break;
        case 'labels':
          this.keyValues(value, keyPath, false);
          break;
        case 'mounts':
          this.mounts(value, keyPath, facts, targets);
          break;
        case 'tmpfs':
          this.tmpfs(value, keyPath, targets);
          break;
        case 'dependsOn':
          this.dependsOn(value, keyPath, facts);
          break;
        case 'ports':
          this.ports(value, keyPath);
          break;
        case 'devices':
          this.devices(value, keyPath, rule.right ?? '');
          break;
        case 'capabilities':
          this.capabilities(value, keyPath, rule.right);
          break;
        default:
          if (this.value(value, keyPath, rule) !== undefined && rule.right !== undefined) {
            this.rights.push({ name: rule.right, path: keyPath });
          }
      }
    }
    if (this.dev && entry(node, 'build') !== undefined) {
      if (entry(node, 'image') !== undefined) {
        this.invalid(`${path}/build`, 'image_and_build');
      }
    } else {
      this.required(node, path, serviceRules);
    }
  }

  /** `build` of the source form: a folder next to the file, or `context` with options. */
  private build(node: YamlNode, path: string): void {
    if (node.kind === 'map') {
      this.object(node, path, rules.dev.build.keys);
    } else {
      this.value(node, path, rules.dev.build.keys.context);
    }
  }

  private required(node: YamlMap, path: string, keys: Record<string, KeyRule>): void {
    for (const [key, rule] of Object.entries(keys)) {
      if (rule.required === true && entry(node, key) === undefined) {
        this.invalid(`${path}/${escapePointer(key)}`, 'required');
      }
    }
  }

  /** Checks a value of a plain rule type; returns the value when it is valid. */
  private value(node: YamlNode, path: string, rule: KeyRule): unknown {
    switch (rule.type) {
      case 'string': {
        const text = this.text(node, path);
        if (text === undefined) {
          return undefined;
        }
        if (rule.pattern !== undefined && !this.matches(rule.pattern, text)) {
          this.invalid(path, 'format');
          return undefined;
        }
        return text;
      }
      case 'bool':
        if (node.kind !== 'scalar' || scalarKind(node) !== 'bool') {
          this.invalid(path, 'type');
          return undefined;
        }
        return node.value.toLowerCase() === 'true';
      case 'int': {
        if (node.kind !== 'scalar' || scalarKind(node) !== 'int') {
          this.invalid(path, 'type');
          return undefined;
        }
        const number = Number(node.value);
        if (number < Number(rule.min) || number > Number(rule.max)) {
          this.invalid(path, 'range');
          return undefined;
        }
        return number;
      }
      case 'enum': {
        const text = this.text(node, path);
        if (text === undefined) {
          return undefined;
        }
        if (!(rule.values ?? []).includes(text)) {
          this.invalid(path, 'format');
          return undefined;
        }
        return text;
      }
      case 'duration':
        return this.duration(node, path, rule);
      case 'byteSize':
        return this.byteSize(node, path, rule);
      case 'cpus':
        return this.cpus(node, path, rule);
      case 'object':
        return this.object(node, path, rule.keys ?? {});
      case 'healthTest':
        return this.healthTest(node, path);
      case 'buildArgs':
        this.keyValues(node, path, true);
        return node;
      default:
        throw new Error(`unknown rule type ${rule.type}`);
    }
  }

  private matches(pattern: string, text: string): boolean {
    const regex = patterns[pattern as keyof typeof patterns];
    return regex.test(text) && (pattern !== 'containerPath' || hasNoDotSegments(text));
  }

  private object(node: YamlNode, path: string, keys: Record<string, KeyRule>): unknown {
    if (node.kind !== 'map') {
      this.invalid(path, 'type');
      return undefined;
    }
    for (const { key, value } of node.entries) {
      const rule = keys[key];
      if (rule === undefined) {
        this.unsupported(`${path}/${escapePointer(key)}`, key);
      } else {
        this.value(value, `${path}/${escapePointer(key)}`, rule);
      }
    }
    this.required(node, path, keys);
    return node;
  }

  /** A string value: quoted, or plain and not null, a boolean or a number; `$$` stands for `$`. */
  private text(node: YamlNode, path: string): string | undefined {
    if (node.kind !== 'scalar' || scalarKind(node) !== 'string') {
      this.invalid(path, 'type');
      return undefined;
    }
    return this.checkedText(node.value, path);
  }

  private checkedText(value: string, path: string): string | undefined {
    if (CONTROL.test(value)) {
      this.invalid(path, 'format');
      return undefined;
    }
    if (value.replaceAll('$$', '').includes('$')) {
      this.invalid(path, 'interpolation');
      return undefined;
    }
    return value.replaceAll('$$', '$');
  }

  /** A value of an environment variable or label: a string, an integer or a boolean, or null when it is empty. */
  private keyValue(node: YamlNode, path: string): string | null | undefined {
    if (node.kind !== 'scalar') {
      this.invalid(path, 'type');
      return undefined;
    }
    switch (scalarKind(node)) {
      case 'null':
        return null;
      case 'bool':
        return node.value.toLowerCase();
      case 'int':
        return node.value;
      case 'number':
        this.invalid(path, 'type');
        return undefined;
      default:
        return this.checkedText(node.value, path);
    }
  }

  private list(node: YamlNode, path: string): YamlNode[] | undefined {
    if (node.kind !== 'seq') {
      this.invalid(path, 'type');
      return undefined;
    }
    if (node.items.length > rules.limits.entries) {
      this.invalid(path, 'range');
      return undefined;
    }
    return node.items;
  }

  private image(node: YamlNode, path: string): void {
    const text = this.text(node, path);
    if (text === undefined) {
      return;
    }
    if (!patterns.imageDigest.test(text)) {
      this.invalid(path, 'unpinned');
    } else if (text.length > rules.limits.imageChars || !patterns.image.test(text)) {
      this.invalid(path, 'format');
    }
  }

  private command(node: YamlNode, path: string): void {
    if (node.kind === 'scalar') {
      this.text(node, path);
      return;
    }
    this.list(node, path)?.forEach((item, index) => this.text(item, `${path}/${String(index)}`));
  }

  private keyValues(node: YamlNode, path: string, environment: boolean): void {
    const pattern = environment ? patterns.envName : patterns.labelKey;
    const seen = new Set<string>();
    const check = (key: string, value: string | null | undefined, itemPath: string) => {
      if (!pattern.test(key)) {
        this.invalid(itemPath, 'format');
      } else if (!environment && isReservedLabel(key)) {
        this.invalid(itemPath, 'reserved');
      } else if (environment && value === null) {
        this.invalid(itemPath, 'no_value');
      } else if (seen.has(key)) {
        this.invalid(itemPath, 'duplicate');
      }
      seen.add(key);
    };
    if (node.kind === 'map') {
      if (node.entries.length > rules.limits.entries) {
        this.invalid(path, 'range');
        return;
      }
      for (const { key, value } of node.entries) {
        const itemPath = `${path}/${escapePointer(key)}`;
        const text = this.keyValue(value, itemPath);
        if (text !== undefined) {
          check(key, text, itemPath);
        }
      }
      return;
    }
    this.list(node, path)?.forEach((item, index) => {
      const itemPath = `${path}/${String(index)}`;
      const text = this.text(item, itemPath);
      if (text !== undefined) {
        const separator = text.indexOf('=');
        check(
          separator === -1 ? text : text.slice(0, separator),
          separator === -1 ? null : text.slice(separator + 1),
          itemPath,
        );
      }
    });
  }

  private mounts(node: YamlNode, path: string, facts: ServiceFacts, targets: Set<string>): void {
    this.list(node, path)?.forEach((item, index) => {
      const itemPath = `${path}/${String(index)}`;
      const mount =
        item.kind === 'map' ? this.longMount(item, itemPath) : this.shortMount(item, itemPath);
      if (mount === undefined) {
        return;
      }
      if (targets.has(mount.target)) {
        this.invalid(itemPath, 'duplicate');
        return;
      }
      targets.add(mount.target);
      if (mount.volume !== undefined) {
        facts.mounts.push({ volume: mount.volume, path: itemPath, readOnly: mount.readOnly });
      }
    });
  }

  /** `VOLUME:TARGET[:ro|rw]`; returns the mount when it is valid. */
  private shortMount(node: YamlNode, path: string): ParsedMount | undefined {
    const text = this.text(node, path);
    if (text === undefined) {
      return undefined;
    }
    const parts = text.split(':');
    const [source = '', target = '', mode] = parts;
    if (parts.length === 1) {
      this.invalid(path, source.startsWith('/') ? 'anonymous' : 'format');
      return undefined;
    }
    if (parts.length > 3 || (mode !== undefined && mode !== 'ro' && mode !== 'rw')) {
      this.invalid(path, 'format');
      return undefined;
    }
    return this.mountSource(source, target, mode === 'ro', path);
  }

  private mountSource(
    source: string,
    target: string,
    readOnly: boolean,
    path: string,
  ): ParsedMount | undefined {
    if (isHostPath(source)) {
      this.hostPath(source, path);
      return undefined;
    }
    if (!patterns.volumeName.test(source)) {
      this.invalid(path, 'format');
      return undefined;
    }
    if (!this.target(target, path)) {
      return undefined;
    }
    if (!this.declaredVolumes.has(source)) {
      this.add('app.compose.reference_unknown', { path, name: source });
      return undefined;
    }
    return { target, volume: source, readOnly };
  }

  private hostPath(source: string, path: string): void {
    if (rules.mount.dockerSockets.includes(source)) {
      this.rights.push({ name: rules.mount.dockerSocketRight, path });
    } else {
      this.invalid(path, 'host_path');
    }
  }

  private target(target: string, path: string): boolean {
    if (!this.matches('containerPath', target)) {
      this.invalid(path, 'format');
      return false;
    }
    if (isReservedTarget(target)) {
      this.invalid(path, 'reserved');
      return false;
    }
    return true;
  }

  private longMount(node: YamlMap, path: string): ParsedMount | undefined {
    const keys = rules.mount.keys as Record<string, KeyRule>;
    let valid = true;
    const values: Record<string, unknown> = {};
    for (const { key, value } of node.entries) {
      const rule = keys[key];
      const keyPath = `${path}/${escapePointer(key)}`;
      if (rule === undefined) {
        this.unsupported(keyPath, key);
        valid = false;
        continue;
      }
      const checked = this.value(value, keyPath, rule);
      if (checked === undefined) {
        valid = false;
      }
      values[key] = checked;
    }
    for (const [key, rule] of Object.entries(keys)) {
      if (rule.required === true && entry(node, key) === undefined) {
        this.invalid(`${path}/${escapePointer(key)}`, 'required');
        valid = false;
      }
    }
    if (!valid) {
      return undefined;
    }
    const type = values.type as string;
    const target = values.target as string;
    const source = values.source as string | undefined;
    if (type === 'bind') {
      this.hostPath(source ?? '', path);
      return undefined;
    }
    if (type === 'tmpfs') {
      if (source !== undefined) {
        this.unsupported(`${path}/source`, 'source');
        return undefined;
      }
      return this.target(target, path) ? { target, readOnly: false } : undefined;
    }
    if (entry(node, 'tmpfs') !== undefined) {
      this.unsupported(`${path}/tmpfs`, 'tmpfs');
      return undefined;
    }
    if (source === undefined) {
      this.invalid(path, 'anonymous');
      return undefined;
    }
    return this.mountSource(source, target, values.read_only === true, path);
  }

  private tmpfs(node: YamlNode, path: string, targets: Set<string>): void {
    const items =
      node.kind === 'scalar'
        ? [{ node, path }]
        : this.list(node, path)?.map((item, index) => ({
            node: item,
            path: `${path}/${String(index)}`,
          }));
    for (const item of items ?? []) {
      const text = this.text(item.node, item.path);
      if (text === undefined) {
        continue;
      }
      const separator = text.indexOf(':');
      const target = separator === -1 ? text : text.slice(0, separator);
      const options = separator === -1 ? [] : text.slice(separator + 1).split(',');
      if (!this.target(target, item.path) || !this.tmpfsOptions(options, item.path)) {
        continue;
      }
      if (targets.has(target)) {
        this.invalid(item.path, 'duplicate');
        continue;
      }
      targets.add(target);
    }
  }

  private tmpfsOptions(options: string[], path: string): boolean {
    const seen = new Set<string>();
    for (const option of options) {
      const separator = option.indexOf('=');
      const name = option.slice(0, separator);
      const value = option.slice(separator + 1);
      if (separator === -1 || seen.has(name)) {
        this.invalid(path, 'format');
        return false;
      }
      seen.add(name);
      if (name === 'mode' && patterns.tmpfsMode.test(value)) {
        continue;
      }
      if (name !== 'size' || !patterns.byteSize.test(value)) {
        this.invalid(path, 'format');
        return false;
      }
      const bytes = byteCount(value);
      if (bytes === 0 || bytes > rules.limits.tmpfsSizeBytes) {
        this.invalid(path, 'range');
        return false;
      }
    }
    return true;
  }

  private dependsOn(node: YamlNode, path: string, facts: ServiceFacts): void {
    if (node.kind === 'map') {
      if (node.entries.length > rules.limits.entries) {
        this.invalid(path, 'range');
        return;
      }
      for (const { key, value } of node.entries) {
        const itemPath = `${path}/${escapePointer(key)}`;
        this.object(value, itemPath, rules.dependsOn.keys);
        facts.dependsOn.push({ name: key, path: itemPath });
      }
      return;
    }
    const seen = new Set<string>();
    this.list(node, path)?.forEach((item, index) => {
      const itemPath = `${path}/${String(index)}`;
      const name = this.text(item, itemPath);
      if (name === undefined) {
        return;
      }
      if (seen.has(name)) {
        this.invalid(itemPath, 'duplicate');
        return;
      }
      seen.add(name);
      facts.dependsOn.push({ name, path: itemPath });
    });
  }

  private healthTest(node: YamlNode, path: string): unknown {
    if (node.kind === 'scalar') {
      return this.text(node, path);
    }
    const items = this.list(node, path);
    if (items === undefined) {
      return undefined;
    }
    const texts = items.map((item, index) => this.text(item, `${path}/${String(index)}`));
    if (texts.some((text) => text === undefined)) {
      return undefined;
    }
    const [first] = texts;
    const valid =
      (first === 'NONE' && texts.length === 1) ||
      (first === 'CMD' && texts.length >= 2) ||
      (first === 'CMD-SHELL' && texts.length === 2);
    if (!valid) {
      this.invalid(path, 'format');
      return undefined;
    }
    return texts;
  }

  private duration(node: YamlNode, path: string, rule: KeyRule): number | undefined {
    const text = this.text(node, path);
    if (text === undefined) {
      return undefined;
    }
    if (!patterns.duration.test(text)) {
      this.invalid(path, 'format');
      return undefined;
    }
    const value = microseconds(text);
    if (value < microseconds(String(rule.min)) || value > microseconds(String(rule.max))) {
      this.invalid(path, 'range');
      return undefined;
    }
    return value;
  }

  private byteSize(node: YamlNode, path: string, rule: KeyRule): number | undefined {
    let text: string | undefined;
    if (node.kind === 'scalar' && scalarKind(node) === 'int') {
      text = node.value;
    } else {
      text = this.text(node, path);
      if (text === undefined) {
        return undefined;
      }
    }
    if (!patterns[(rule.pattern ?? 'byteSize') as keyof typeof patterns].test(text)) {
      this.invalid(path, 'format');
      return undefined;
    }
    const bytes = byteCount(text);
    const min = rule.min === undefined ? 1 : rules.limits[rule.min as keyof Limits];
    if (bytes < min || bytes > rules.limits[rule.max as keyof Limits]) {
      this.invalid(path, 'range');
      return undefined;
    }
    return bytes;
  }

  /** A CPU limit in cores: a plain decimal or a string, above 0. */
  private cpus(node: YamlNode, path: string, rule: KeyRule): number | undefined {
    const kind = node.kind === 'scalar' ? scalarKind(node) : undefined;
    if (node.kind !== 'scalar' || (kind !== 'int' && kind !== 'number' && kind !== 'string')) {
      this.invalid(path, 'type');
      return undefined;
    }
    if (!patterns.cpus.test(node.value)) {
      this.invalid(path, 'format');
      return undefined;
    }
    const cores = Number(node.value);
    if (cores <= 0 || cores > rules.limits[rule.max as keyof Limits]) {
      this.invalid(path, 'range');
      return undefined;
    }
    return cores;
  }

  private ports(node: YamlNode, path: string): void {
    this.list(node, path)?.forEach((item, index) => {
      const itemPath = `${path}/${String(index)}`;
      let text: string | undefined;
      if (item.kind === 'scalar' && scalarKind(item) === 'int') {
        text = item.value;
      } else {
        text = this.text(item, itemPath);
        if (text === undefined) {
          return;
        }
      }
      if (!patterns.port.test(text)) {
        this.invalid(itemPath, 'format');
        return;
      }
      const [low = 0, high = low] = text
        .replace(/\/.*$/, '')
        .split('-')
        .map((part) => Number(part));
      if (low < 1 || high > 65535 || low > high) {
        this.invalid(itemPath, 'range');
      }
    });
  }

  private devices(node: YamlNode, path: string, right: string): void {
    const seen = new Set<string>();
    this.list(node, path)?.forEach((item, index) => {
      const itemPath = `${path}/${String(index)}`;
      const text = this.text(item, itemPath);
      if (text === undefined) {
        return;
      }
      const parts = text.split(':');
      const [host = '', container = host, permissions = 'rw'] = parts;
      if (
        parts.length > 3 ||
        !patterns.devicePath.test(host) ||
        !patterns.devicePath.test(container) ||
        !patterns.devicePermissions.test(permissions)
      ) {
        this.invalid(itemPath, 'format');
        return;
      }
      if (seen.has(host)) {
        this.invalid(itemPath, 'duplicate');
        return;
      }
      seen.add(host);
      this.rights.push({ name: right + host, path: itemPath });
    });
  }

  private capabilities(node: YamlNode, path: string, right: string | undefined): void {
    const seen = new Set<string>();
    this.list(node, path)?.forEach((item, index) => {
      const itemPath = `${path}/${String(index)}`;
      const text = this.text(item, itemPath);
      if (text === undefined) {
        return;
      }
      if (!patterns.capability.test(text)) {
        this.invalid(itemPath, 'format');
        return;
      }
      if (seen.has(text)) {
        this.invalid(itemPath, 'duplicate');
        return;
      }
      seen.add(text);
      if (right !== undefined) {
        this.rights.push({ name: right + text, path: itemPath });
      }
    });
  }

  private crossChecks(services: ServiceFacts[]): void {
    const names = new Set(services.map((service) => service.name));
    const backend = this.manifest.backend;

    for (const service of services) {
      for (const reference of service.dependsOn) {
        if (!names.has(reference.name)) {
          this.add('app.compose.reference_unknown', { path: reference.path, name: reference.name });
        }
      }
    }
    const edges = new Map(
      services.map((service) => [
        service.name,
        service.dependsOn.map((reference) => reference.name),
      ]),
    );
    for (const service of services) {
      if (reaches(edges, service.name)) {
        this.invalid(`/services/${escapePointer(service.name)}/depends_on`, 'cycle');
      }
    }

    for (const [endpoint, { service }] of Object.entries(backend?.endpoints ?? {})) {
      if (!names.has(service)) {
        this.add('app.compose.reference_unknown', {
          path: `/backend/endpoints/${escapePointer(endpoint)}/service`,
          name: service,
        });
      }
    }

    this.manifest.storage?.forEach((request, index) => {
      const listed = request.services ?? [];
      listed.forEach((name, serviceIndex) => {
        const path = `/storage/${String(index)}/services/${String(serviceIndex)}`;
        const service = services.find((candidate) => candidate.name === name);
        if (service === undefined) {
          this.add('app.compose.reference_unknown', { path, name });
        } else if (!service.mounts.some((mount) => mount.volume === request.id)) {
          this.mismatch(path, request.id, 'not_mounted');
        }
      });
      for (const service of services) {
        for (const mount of service.mounts) {
          if (mount.volume !== request.id) {
            continue;
          }
          if (!listed.includes(service.name)) {
            this.mismatch(mount.path, request.id, 'not_listed');
          } else if (request.access === 'read' && !mount.readOnly) {
            this.mismatch(mount.path, request.id, 'read_only');
          }
        }
      }
    });

    const declared = backend?.elevated ?? [];
    for (const { name, path } of this.rights) {
      if (!isSupportedElevatedRight(name)) {
        this.add('app.elevated.unsupported', { right: name, path });
      } else if (!(declared as string[]).includes(name)) {
        this.add('app.compose.right_missing', { path, right: name });
      }
    }
    declared.forEach((right, index) => {
      if (isSupportedElevatedRight(right) && !this.rights.some((used) => used.name === right)) {
        this.add('app.compose.right_unused', { path: `/backend/elevated/${String(index)}`, right });
      }
    });
  }
}

function entry(map: YamlMap, key: string): YamlNode | undefined {
  return map.entries.find((candidate) => candidate.key === key)?.value;
}

function hasNoDotSegments(path: string): boolean {
  return path.split('/').every((segment) => segment !== '.' && segment !== '..');
}

function isHostPath(source: string): boolean {
  return (
    source === '' ||
    source.startsWith('.') ||
    source.startsWith('~') ||
    source.includes('/') ||
    source.includes('\\')
  );
}

function isReservedTarget(target: string): boolean {
  return (
    target === '/' ||
    rules.mount.reservedTargets.some(
      (reserved) => target === reserved || target.startsWith(`${reserved}/`),
    )
  );
}

function isReservedLabel(key: string): boolean {
  const lower = key.toLowerCase();
  return rules.reservedLabelPrefixes.some(
    (prefix) => lower === prefix || lower.startsWith(`${prefix}.`),
  );
}

function microseconds(duration: string): number {
  let total = 0;
  for (const [, amount = '0', unit = 's'] of duration.matchAll(DURATION_PART)) {
    total += Number(amount) * (DURATION_UNITS[unit] ?? 0);
  }
  return total;
}

function byteCount(size: string): number {
  const match = /^([0-9]+)([a-zA-Z]?)/.exec(size);
  return Number(match?.[1] ?? 0) * (BYTE_UNITS[(match?.[2] ?? '').toLowerCase()] ?? 1);
}

function reaches(edges: Map<string, string[]>, start: string): boolean {
  const seen = new Set<string>();
  const queue = [...(edges.get(start) ?? [])];
  while (queue.length > 0) {
    const next = queue.shift() ?? '';
    if (next === start) {
      return true;
    }
    if (!seen.has(next) && edges.has(next)) {
      seen.add(next);
      queue.push(...(edges.get(next) ?? []));
    }
  }
  return false;
}
