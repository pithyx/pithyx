import { Ajv2020, type ErrorObject, type ValidateFunction } from 'ajv/dist/2020.js';

const ajv = new Ajv2020({ allErrors: true, strict: true, allowMatchingProperties: true });
// json-schema-to-typescript reads tsType; for validation it is an annotation.
ajv.addVocabulary(['tsType']);

/** Compiles one of the schemas in `@pithyx/sdk/schema`, as the box reads them. */
export function compileSchema(schema: object): ValidateFunction {
  return ajv.compile(schema);
}

/**
 * Parses the bytes of a JSON file as the box does: UTF-8 without a byte order mark only, and an object with a key twice is refused, which
 * JSON.parse would accept by keeping the last one. Returns undefined when the bytes are not such a document.
 */
export function parseStrictJson(bytes: Uint8Array): { value: unknown } | undefined {
  try {
    const text = new TextDecoder('utf-8', { fatal: true, ignoreBOM: true }).decode(bytes);
    const value: unknown = JSON.parse(text);
    return hasDuplicateKeys(text) ? undefined : { value };
  } catch {
    return undefined;
  }
}

export function isObject(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}

/** A param value as the box shows it: strings as they are, everything else as compact JSON. */
export function display(value: unknown): string {
  return typeof value === 'string' ? value : JSON.stringify(value);
}

export function escapePointer(name: string): string {
  return name.replaceAll('~', '~0').replaceAll('/', '~1');
}

export function compareOrdinal(a: string, b: string): number {
  return a < b ? -1 : a > b ? 1 : 0;
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

/**
 * The schema violations of a value as the box lists them: one per JSON Pointer and keyword, sorted by pointer and
 * keyword, with a missing or unknown field at its own pointer.
 */
export function schemaViolations(validate: ValidateFunction, value: unknown): [string, string][] {
  if (validate(value)) {
    return [];
  }
  const seen = new Set<string>();
  const violations: [string, string][] = [];
  for (const error of validate.errors ?? []) {
    const [path, keyword] = violation(error);
    if (!seen.has(`${path}\0${keyword}`)) {
      seen.add(`${path}\0${keyword}`);
      violations.push([path, keyword]);
    }
  }
  return violations.sort(([a, x], [b, y]) =>
    a === b ? compareOrdinal(x, y) : compareOrdinal(a, b),
  );
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
