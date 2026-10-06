import {
  CST,
  isAlias,
  isMap,
  isPair,
  isScalar,
  isSeq,
  parseAllDocuments,
  Parser,
  type Node,
  type Pair,
} from 'yaml';
import type { Issue } from './issues';

/**
 * The YAML of a Compose file as plain data: mappings with string keys in source order, sequences and scalars with their
 * text. The daemon (`Apps/Compose/ComposeYaml.cs`) builds the same tree from YamlDotNet's events, so both validators
 * see one document.
 */
export type YamlNode = YamlMap | YamlSeq | YamlScalar;

export interface YamlMap {
  kind: 'map';
  entries: { key: string; value: YamlNode }[];
}

export interface YamlSeq {
  kind: 'seq';
  items: YamlNode[];
}

export interface YamlScalar {
  kind: 'scalar';
  value: string;
  /** Unquoted and not a block scalar, so the text may stand for null, a boolean or a number. */
  plain: boolean;
}

/** What a scalar stands for: plain scalars resolve like YAML 1.2, but only canonical decimals count as integers. */
export type ScalarKind = 'null' | 'bool' | 'int' | 'number' | 'string';

const NULL = /^(?:~|null|Null|NULL|)$/;
const BOOL = /^(?:true|True|TRUE|false|False|FALSE)$/;
const INT = /^-?(?:0|[1-9][0-9]{0,15})$/;
// Everything Go's YAML library, YAML 1.1 or YAML 1.2 would read as a number: refused wherever a value is expected,
// because the author meant either a string (quote it) or a canonical integer.
const NUMBER =
  /^(?:[-+]?(?:0[bBoOxX][0-9a-fA-F_]+|[0-9][0-9_]*(?:\.[0-9_]*)?(?:[eE][-+]?[0-9]+)?|\.[0-9][0-9_]*(?:[eE][-+]?[0-9]+)?|\.(?:inf|Inf|INF))|\.(?:nan|NaN|NAN))$/;

export function scalarKind(scalar: YamlScalar): ScalarKind {
  if (!scalar.plain) {
    return 'string';
  }
  if (NULL.test(scalar.value)) {
    return 'null';
  }
  if (BOOL.test(scalar.value)) {
    return 'bool';
  }
  if (INT.test(scalar.value)) {
    return 'int';
  }
  return NUMBER.test(scalar.value) ? 'number' : 'string';
}

export function escapePointer(name: string): string {
  return name.replaceAll('~', '~0').replaceAll('/', '~1');
}

function yamlIssue(reason: string, path?: string): Issue {
  return {
    code: 'app.compose.yaml_invalid',
    params: path === undefined ? { reason } : { reason, path },
  };
}

/**
 * Whether a code point may appear in the file: printable YAML characters, line feeds and carriage returns. Tabs, NEL,
 * the Unicode line and paragraph separators and the byte order mark are refused, because YAML 1.1 and 1.2 parsers
 * disagree about them.
 */
function isAllowedCharacter(point: number): boolean {
  return (
    point === 0x0a ||
    point === 0x0d ||
    (point >= 0x20 && point <= 0x7e) ||
    (point >= 0xa0 && point <= 0xd7ff && point !== 0x2028 && point !== 0x2029) ||
    (point >= 0xe000 && point <= 0xfffd && point !== 0xfeff) ||
    point >= 0x10000
  );
}

/** The nesting depth of collections in the concrete syntax tree, without recursion. */
function cstDepth(tokens: CST.Token[], limit: number): number {
  let deepest = 0;
  const stack: [CST.Token | null | undefined, number][] = tokens.map((token) => [token, 0]);
  while (stack.length > 0) {
    const [token, depth] = stack.pop() ?? [undefined, 0];
    if (token === undefined || token === null) {
      continue;
    }
    switch (token.type) {
      case 'document':
        stack.push([token.value, depth]);
        break;
      case 'block-map':
      case 'block-seq':
      case 'flow-collection': {
        const level = depth + 1;
        deepest = Math.max(deepest, level);
        if (deepest > limit) {
          return deepest;
        }
        const pairsInSequence = token.type === 'flow-collection' && token.start.source === '[';
        for (const item of token.items) {
          // A pair inside a flow sequence ([a: b]) is a mapping of its own.
          const pair =
            pairsInSequence &&
            (item.key !== undefined ||
              (item.sep ?? []).some((part) => part.type === 'map-value-ind'));
          const inner = pair ? level + 1 : level;
          deepest = Math.max(deepest, inner);
          stack.push([item.key, inner], [item.value, inner]);
        }
        break;
      }
      default:
        break;
    }
  }
  return deepest;
}

export type YamlResult = { root: YamlNode; issues: [] } | { root?: undefined; issues: Issue[] };

/**
 * Reads the bytes of a Compose file into a {@link YamlNode}, refusing what the box never accepts: more than
 * `limits.fileBytes`, text that is not UTF-8, tabs and ambiguous line breaks, syntax errors, other than one document,
 * nesting deeper than `limits.depth`, directives, anchors and aliases, tags, duplicate keys, keys that are not strings
 * and merge keys.
 */
export function readComposeYaml(
  bytes: Uint8Array,
  limits: { fileBytes: number; depth: number },
): YamlResult {
  if (bytes.length > limits.fileBytes) {
    return { issues: [yamlIssue('too_large')] };
  }
  let text: string;
  try {
    text = new TextDecoder('utf-8', { fatal: true, ignoreBOM: true }).decode(bytes);
  } catch {
    return { issues: [yamlIssue('not_utf8')] };
  }
  for (const character of text) {
    if (!isAllowedCharacter(character.codePointAt(0) ?? 0)) {
      return { issues: [yamlIssue('character')] };
    }
  }

  const tokens = [...new Parser().parse(text)];
  if (cstDepth(tokens, limits.depth) > limits.depth) {
    return { issues: [yamlIssue('too_deep')] };
  }
  const documents = parseAllDocuments(text, {
    schema: 'failsafe',
    uniqueKeys: false,
    merge: false,
    strict: true,
  });
  const list = Array.isArray(documents) ? documents : [];
  if (list.some((document) => document.errors.length > 0)) {
    return { issues: [yamlIssue('syntax')] };
  }
  const [document] = list;
  if (list.length !== 1 || document === undefined) {
    return { issues: [yamlIssue('documents')] };
  }

  const reasons = new Set<string>();
  if (tokens.some((token) => token.type === 'directive')) {
    reasons.add('directive');
  }
  findPropertyUse(document.contents, reasons);
  if (reasons.size > 0) {
    return {
      issues: ['directive', 'anchor', 'tag']
        .filter((reason) => reasons.has(reason))
        .map((reason) => yamlIssue(reason)),
    };
  }

  const issues: Issue[] = [];
  const root = build(document.contents, '', issues);
  return issues.length > 0 ? { issues } : { root, issues: [] };
}

function findPropertyUse(node: unknown, reasons: Set<string>): void {
  if (isAlias(node)) {
    reasons.add('anchor');
    return;
  }
  if (!isScalar(node) && !isMap(node) && !isSeq(node)) {
    return;
  }
  const { anchor, tag } = node as Node;
  if (anchor !== undefined) {
    reasons.add('anchor');
  }
  if (tag !== undefined) {
    reasons.add('tag');
  }
  if (isMap(node)) {
    for (const pair of node.items) {
      findPropertyUse(pair.key, reasons);
      findPropertyUse(pair.value, reasons);
    }
  } else if (isSeq(node)) {
    for (const item of node.items) {
      findPropertyUse(isPair(item) ? item.value : item, reasons);
      if (isPair(item)) {
        findPropertyUse(item.key, reasons);
      }
    }
  }
}

const EMPTY: YamlScalar = { kind: 'scalar', value: '', plain: true };

function build(node: unknown, path: string, issues: Issue[]): YamlNode {
  if (isScalar(node)) {
    return {
      kind: 'scalar',
      value: String(node.value),
      plain: node.type === 'PLAIN' || node.type === undefined,
    };
  }
  if (isSeq(node)) {
    return {
      kind: 'seq',
      items: node.items.map((item, index) =>
        // A pair in a flow sequence ([a: b]) is a mapping with one entry.
        isPair(item)
          ? buildMap([item], `${path}/${String(index)}`, issues)
          : build(item, `${path}/${String(index)}`, issues),
      ),
    };
  }
  return isMap(node) ? buildMap(node.items, path, issues) : EMPTY;
}

function buildMap(pairs: Pair[], path: string, issues: Issue[]): YamlMap {
  const entries: YamlMap['entries'] = [];
  const seen = new Set<string>();
  for (const pair of pairs) {
    const key = pair.key;
    if (!isScalar(key)) {
      issues.push(yamlIssue('key_not_string', path));
      continue;
    }
    const scalar: YamlScalar = {
      kind: 'scalar',
      value: String(key.value),
      plain: key.type === 'PLAIN',
    };
    const keyPath = `${path}/${escapePointer(scalar.value)}`;
    if (scalar.value === '<<') {
      issues.push(yamlIssue('merge_key', keyPath));
    } else if (scalarKind(scalar) !== 'string') {
      issues.push(yamlIssue('key_not_string', keyPath));
    } else if (seen.has(scalar.value)) {
      issues.push(yamlIssue('duplicate_key', keyPath));
    } else {
      seen.add(scalar.value);
      entries.push({ key: scalar.value, value: build(pair.value, keyPath, issues) });
    }
  }
  return { kind: 'map', entries };
}
