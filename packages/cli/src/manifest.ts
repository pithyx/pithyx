import type { AppManifest } from '@pithyx/sdk';
import composeRules from '@pithyx/sdk/schema/compose.v1.rules.json' with { type: 'json' };
import schema from '@pithyx/sdk/schema/manifest.v2.schema.json' with { type: 'json' };
import { isValidCron } from './cron';
import type { Issue } from './issues';
import { compileSchema, display, isObject, parseStrictJson, schemaViolations } from './json-schema';
import { DEVICE_PREFIX, elevatedRightsOf } from './rights';

export const SUPPORTED_MANIFEST_VERSION = 2;

/** The namespace of the system apps and the official catalog; the box never accepts it from a sideload. */
export const RESERVED_ID_PREFIX = 'org.pithyx.';

const validateSchema = compileSchema(schema);

export interface ManifestValidation {
  /** Empty when the manifest is valid. */
  issues: Issue[];
  /** The parsed manifest when it is valid. */
  manifest?: AppManifest;
}

const CAPABILITY_PREFIX = 'capability:';
const elevatedRules = composeRules.elevated;
const blockedDevice = new RegExp(composeRules.patterns.blockedDevice);

/**
 * Whether this version of Pithyx can grant an elevated right the schema accepts (D311, D387): `host.network`,
 * `host.pid`, `docker.socket`, `docker.socket.raw`, a device node or class outside the block list and a capability
 * outside the block list. `host.full` stays refused.
 */
export function isSupportedElevatedRight(right: string): boolean {
  if (
    elevatedRules.hostNamespaceRights.includes(right) ||
    right === composeRules.mount.dockerSocketRight ||
    right === composeRules.mount.dockerSocketRawRight
  ) {
    return true;
  }
  if (right.startsWith(DEVICE_PREFIX)) {
    return !blockedDevice.test(right.slice(DEVICE_PREFIX.length));
  }
  return (
    right.startsWith(CAPABILITY_PREFIX) &&
    !elevatedRules.blockedCapabilities.includes(right.slice(CAPABILITY_PREFIX.length))
  );
}

/**
 * Whether a granted right runs its service in the host's user namespace: the host's network or processes, or a device
 * whose group exists only on the host. Root there would be root on the box, so `backend.root` refuses it.
 */
export function needsHostNamespace(right: string): boolean {
  return (
    elevatedRules.hostNamespaceRights.includes(right) ||
    (right.startsWith(DEVICE_PREFIX) &&
      !elevatedRules.namespacedDevices.includes(right.slice(DEVICE_PREFIX.length)))
  );
}

/** How much an elevated right can do on the box, and so how much granting it asks (D388). */
export type RiskTier = 'elevated' | 'high' | 'critical';

/** The tier of an elevated right, from `elevated.tiers` of the compose rules, as the box reads it. */
export function tierOf(right: string): RiskTier {
  const tiers = elevatedRules.tiers.rights as Record<string, string | undefined>;
  // A device below a listed folder has its tier: device:/dev/dri/renderD128 is one node of device:/dev/dri.
  for (let name = right; ; name = name.slice(0, name.lastIndexOf('/'))) {
    const tier = tiers[name];
    if (tier !== undefined) {
      return tier as RiskTier;
    }
    if (!name.startsWith(DEVICE_PREFIX) || name.lastIndexOf('/') <= DEVICE_PREFIX.length) {
      return elevatedRules.tiers.default as RiskTier;
    }
  }
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
  const parsed = parseStrictJson(bytes);
  if (parsed === undefined) {
    return fail({ code: 'app.manifest.invalid_json', params: {} });
  }
  const root = parsed.value;

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

function schemaIssues(root: unknown): Issue[] {
  const violations = schemaViolations(validateSchema, root);
  const issues: Issue[] = [];
  const record = root as Record<string, unknown>;
  for (const [path, keyword] of violations) {
    // A wrong id, sdk range or pithyx range has a code of its own; a missing one is a plain schema violation.
    if (path === '/id' && keyword !== 'required') {
      addOnce(issues, { code: 'app.manifest.id_invalid', params: { id: display(record.id) } });
    } else if (path === '/sdk' && keyword !== 'required') {
      addOnce(issues, { code: 'app.manifest.sdk_invalid', params: { sdk: display(record.sdk) } });
    } else if (path === '/pithyx' && keyword !== 'required') {
      addOnce(issues, {
        code: 'app.manifest.pithyx_invalid',
        params: { pithyx: display(record.pithyx) },
      });
    } else {
      issues.push({ code: 'app.manifest.schema_violation', params: { path, keyword } });
    }
  }
  return issues;
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

  const actionIds = new Set<string>();
  manifest.fileActions?.forEach((action, index) => {
    if (actionIds.has(action.id)) {
      issues.push(duplicate(`/fileActions/${String(index)}/id`, action.id));
    }
    actionIds.add(action.id);
    // An action applies to files with these extensions, to folders, or to both.
    if (action.extensions === undefined && action.folders !== true) {
      issues.push({
        code: 'app.manifest.schema_violation',
        params: { path: `/fileActions/${String(index)}`, keyword: 'required' },
      });
    }
  });
  if (manifest.fileActions !== undefined && !('files.shares' in manifest.permissions)) {
    issues.push({
      code: 'app.manifest.permission_missing',
      params: { path: '/fileActions', permission: 'files.shares' },
    });
  }

  const categoryIds = new Set<string>();
  manifest.notifications?.categories.forEach((category, index) => {
    if (categoryIds.has(category.id)) {
      issues.push(duplicate(`/notifications/categories/${String(index)}/id`, category.id));
    }
    categoryIds.add(category.id);
  });
  if (manifest.notifications !== undefined && !('notifications' in manifest.permissions)) {
    issues.push({
      code: 'app.manifest.permission_missing',
      params: { path: '/notifications', permission: 'notifications' },
    });
  }

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
  }
  const root = backend?.root === true;
  for (const right of elevatedRightsOf(manifest)) {
    if (!isSupportedElevatedRight(right)) {
      issues.push({ code: 'app.elevated.unsupported', params: { right } });
    } else if (backend === undefined) {
      issues.push({ code: 'app.elevated.backend_missing', params: { right } });
    } else if (root && needsHostNamespace(right)) {
      issues.push({ code: 'app.elevated.root_conflict', params: { right } });
    } else if (!root && right.startsWith(CAPABILITY_PREFIX)) {
      issues.push({ code: 'app.elevated.root_required', params: { right } });
    }
  }
  return issues;
}
