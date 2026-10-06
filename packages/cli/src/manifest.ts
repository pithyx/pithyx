import type { AppManifest } from '@pithyx/sdk';
import schema from '@pithyx/sdk/schema/manifest.v1.schema.json' with { type: 'json' };
import { Ajv2020, type ErrorObject } from 'ajv/dist/2020.js';
import { isValidCron } from './cron';
import type { Issue } from './issues';

export const SUPPORTED_MANIFEST_VERSION = 1;

/** The namespace of the system apps and the official catalog; the box never accepts it from a sideload. */
export const RESERVED_ID_PREFIX = 'org.pithyx.';

/** Capabilities that reach the host's secrets (D71); never granted on their own. */
const BLOCKED_CAPABILITIES = new Set([
  'ALL',
  'SYS_ADMIN',
  'SYS_PTRACE',
  'SYS_MODULE',
  'SYS_RAWIO',
  'DAC_READ_SEARCH',
  'BPF',
]);

const ajv = new Ajv2020({ allErrors: true, strict: true, allowMatchingProperties: true });
// json-schema-to-typescript reads tsType; for validation it is an annotation.
ajv.addVocabulary(['tsType']);
const validateSchema = ajv.compile(schema);

export interface ManifestValidation {
  /** Empty when the manifest is valid. */
  issues: Issue[];
  /** The parsed manifest when it is valid. */
  manifest?: AppManifest;
}

/** Whether this version of Pithyx can grant an elevated right the schema accepts (D71, D304). */
export function isSupportedElevatedRight(right: string): boolean {
  return (
    right !== 'docker.socket' &&
    right !== 'host.full' &&
    !(
      right.startsWith('capability:') && BLOCKED_CAPABILITIES.has(right.slice('capability:'.length))
    )
  );
}

/**
 * Checks the bytes of `pithyx.json` against the manifest schema and the rules a schema cannot express, with the same
 * verdicts and codes as the box (the corpus in `@pithyx/sdk/schema/fixtures` holds both alike). Ids in `org.pithyx.*`
 * are refused unless `allowReservedId` is set, which only the system apps and the official catalog may do.
 */
export function validateManifest(
  bytes: Uint8Array,
  options: { allowReservedId?: boolean } = {},
): ManifestValidation {
  let root: unknown;
  try {
    const text = new TextDecoder('utf-8', { fatal: true }).decode(bytes);
    root = JSON.parse(text);
    if (hasDuplicateKeys(text)) {
      return fail({ code: 'app.manifest.invalid_json', params: {} });
    }
  } catch {
    return fail({ code: 'app.manifest.invalid_json', params: {} });
  }

  if (
    isObject(root) &&
    'manifestVersion' in root &&
    root.manifestVersion !== SUPPORTED_MANIFEST_VERSION
  ) {
    return fail({
      code: 'app.manifest.version_unsupported',
      params: { version: display(root.manifestVersion), supported: SUPPORTED_MANIFEST_VERSION },
    });
  }

  const issues = schemaIssues(root);
  if (!issues.some((issue) => issue.code === 'app.manifest.schema_violation')) {
    const idValid = !issues.some((issue) => issue.code === 'app.manifest.id_invalid');
    issues.push(...semanticIssues(root as AppManifest, options.allowReservedId ?? false, idValid));
  }
  return issues.length === 0 ? { issues, manifest: root as AppManifest } : { issues };
}

function fail(issue: Issue): ManifestValidation {
  return { issues: [issue] };
}

function isObject(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}

/** A param value as the box shows it: strings as they are, everything else as compact JSON. */
function display(value: unknown): string {
  return typeof value === 'string' ? value : JSON.stringify(value);
}

function escapePointer(name: string): string {
  return name.replaceAll('~', '~0').replaceAll('/', '~1');
}

function violation(error: ErrorObject): [string, string] {
  if (error.keyword === 'required') {
    const { missingProperty } = error.params as { missingProperty: string };
    return [`${error.instancePath}/${escapePointer(missingProperty)}`, 'required'];
  }
  if (error.keyword === 'additionalProperties') {
    const { additionalProperty } = error.params as { additionalProperty: string };
    return [`${error.instancePath}/${escapePointer(additionalProperty)}`, 'additionalProperties'];
  }
  return [error.instancePath, error.keyword];
}

function schemaIssues(root: unknown): Issue[] {
  if (validateSchema(root)) {
    return [];
  }
  const seen = new Set<string>();
  const violations: [string, string][] = [];
  for (const error of validateSchema.errors ?? []) {
    const [path, keyword] = violation(error);
    if (!seen.has(`${path}\0${keyword}`)) {
      seen.add(`${path}\0${keyword}`);
      violations.push([path, keyword]);
    }
  }
  violations.sort(([a, x], [b, y]) => (a === b ? compare(x, y) : compare(a, b)));

  const issues: Issue[] = [];
  const record = root as Record<string, unknown>;
  for (const [path, keyword] of violations) {
    // A wrong id or sdk range has a code of its own; a missing one is a plain schema violation.
    if (path === '/id' && keyword !== 'required') {
      addOnce(issues, { code: 'app.manifest.id_invalid', params: { id: display(record.id) } });
    } else if (path === '/sdk' && keyword !== 'required') {
      addOnce(issues, { code: 'app.manifest.sdk_invalid', params: { sdk: display(record.sdk) } });
    } else {
      issues.push({ code: 'app.manifest.schema_violation', params: { path, keyword } });
    }
  }
  return issues;
}

function compare(a: string, b: string): number {
  return a < b ? -1 : a > b ? 1 : 0;
}

function addOnce(issues: Issue[], issue: Issue): void {
  if (!issues.some((existing) => existing.code === issue.code)) {
    issues.push(issue);
  }
}

function duplicate(path: string, value: string): Issue {
  return { code: 'app.manifest.duplicate', params: { path, value } };
}

function unknownReference(path: string, name: string): Issue {
  return { code: 'app.manifest.reference_unknown', params: { path, name } };
}

function semanticIssues(
  manifest: AppManifest,
  allowReservedId: boolean,
  idValid: boolean,
): Issue[] {
  const issues: Issue[] = [];
  if (idValid && !allowReservedId && manifest.id.startsWith(RESERVED_ID_PREFIX)) {
    issues.push({ code: 'app.manifest.id_reserved', params: { id: manifest.id } });
  }

  const { defaultSize, minSize } = manifest.window;
  if (minSize.width > defaultSize.width || minSize.height > defaultSize.height) {
    issues.push({ code: 'app.manifest.window_size_invalid', params: {} });
  }

  const storageIds = new Set<string>();
  manifest.storage?.forEach((request, index) => {
    if (storageIds.has(request.id)) {
      issues.push(duplicate(`/storage/${String(index)}/id`, request.id));
    }
    storageIds.add(request.id);
    if (manifest.backend === undefined) {
      request.services?.forEach((service, serviceIndex) => {
        issues.push(
          unknownReference(`/storage/${String(index)}/services/${String(serviceIndex)}`, service),
        );
      });
    }
  });

  const extensions = new Set<string>();
  manifest.fileHandlers?.forEach((handler, index) => {
    handler.extensions.forEach((extension, extensionIndex) => {
      if (extensions.has(extension)) {
        issues.push(
          duplicate(
            `/fileHandlers/${String(index)}/extensions/${String(extensionIndex)}`,
            extension,
          ),
        );
      }
      extensions.add(extension);
    });
  });

  const backend = manifest.backend;
  if (backend !== undefined) {
    const endpoints = new Set(Object.keys(backend.endpoints));
    if (backend.health !== undefined && !endpoints.has(backend.health.endpoint)) {
      issues.push(unknownReference('/backend/health/endpoint', backend.health.endpoint));
    }
    const scheduleIds = new Set<string>();
    backend.schedules?.forEach((schedule, index) => {
      if (scheduleIds.has(schedule.id)) {
        issues.push(duplicate(`/backend/schedules/${String(index)}/id`, schedule.id));
      }
      scheduleIds.add(schedule.id);
      if (!isValidCron(schedule.cron)) {
        issues.push({
          code: 'app.manifest.cron_invalid',
          params: { path: `/backend/schedules/${String(index)}/cron`, cron: schedule.cron },
        });
      }
      if (!endpoints.has(schedule.endpoint)) {
        issues.push(
          unknownReference(`/backend/schedules/${String(index)}/endpoint`, schedule.endpoint),
        );
      }
    });
    for (const right of backend.elevated ?? []) {
      if (!isSupportedElevatedRight(right)) {
        issues.push({ code: 'app.elevated.unsupported', params: { right } });
      }
    }
  }
  return issues;
}

/** JSON.parse keeps the last of two equal keys; the box refuses them, so the CLI does too. */
function hasDuplicateKeys(text: string): boolean {
  const stack: { keys: Set<string> | undefined; expectKey: boolean }[] = [];
  for (let i = 0; i < text.length; i++) {
    const char = text[i];
    if (char === '"') {
      let end = i + 1;
      while (text[end] !== '"') {
        end += text[end] === '\\' ? 2 : 1;
      }
      const top = stack.at(-1);
      if (top?.keys !== undefined && top.expectKey) {
        const key = JSON.parse(text.slice(i, end + 1)) as string;
        if (top.keys.has(key)) {
          return true;
        }
        top.keys.add(key);
        top.expectKey = false;
      }
      i = end;
    } else if (char === '{') {
      stack.push({ keys: new Set(), expectKey: true });
    } else if (char === '[') {
      stack.push({ keys: undefined, expectKey: false });
    } else if (char === '}' || char === ']') {
      stack.pop();
    } else if (char === ',') {
      const top = stack.at(-1);
      if (top?.keys !== undefined) {
        top.expectKey = true;
      }
    }
  }
  return false;
}
