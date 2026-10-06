import { SaxesParser } from 'saxes';
import type { Issue } from './issues';

export const MAX_ICON_BYTES = 64 * 1024;

const SVG_NAMESPACE = 'http://www.w3.org/2000/svg';

const FORBIDDEN_ELEMENTS = new Set(
  ['script', 'foreignObject', 'image', 'iframe', 'embed', 'object', 'audio', 'video', 'canvas'].map(
    (name) => name.toLowerCase(),
  ),
);

const ANIMATION_ELEMENTS = new Set(['set', 'animate']);

// url( with anything but a fragment reference (#id) after optional whitespace and quote.
const EXTERNAL_URL = /url\((?![ \t\r\n\f]*['"]?#)/i;

const NUMBER = /^[+-]?([0-9]+\.?[0-9]*|\.[0-9]+)([eE][+-]?[0-9]+)?$/;

function issue(reason: string, extra: Record<string, string> = {}): Issue {
  return { code: 'app.icon.invalid', params: { reason, ...extra } };
}

function isSquareViewBox(viewBox: string | undefined): boolean {
  if (viewBox === undefined) {
    return false;
  }
  const parts = viewBox.trim().split(/[ \t\r\n,]+/);
  if (parts.length !== 4 || !parts.every((part) => NUMBER.test(part))) {
    return false;
  }
  const width = Number(parts[2]);
  const height = Number(parts[3]);
  return width > 0 && width === height;
}

/**
 * Checks an app's `icon.svg` with the same rules as the box: at most 64 KB of well-formed UTF-8 XML with an `svg` root
 * in the SVG namespace and a square `viewBox`, without a DOCTYPE, processing instructions, scripts, foreign content,
 * embedded or external images, event attributes, links outside the file, or `@import`, `url()` to anything but a
 * fragment and escapes in styles.
 */
export function validateIcon(bytes: Uint8Array): Issue[] {
  if (bytes.length > MAX_ICON_BYTES) {
    return [issue('too_large')];
  }
  let text: string;
  try {
    // The decoder drops a byte order mark.
    text = new TextDecoder('utf-8', { fatal: true }).decode(bytes);
  } catch {
    return [issue('malformed')];
  }
  // A DOCTYPE could declare entities; it is refused before parsing, wherever it appears.
  if (text.includes('<!DOCTYPE')) {
    return [issue('doctype')];
  }

  const issues: Issue[] = [];
  const add = (found: Issue) => {
    const key = JSON.stringify(found.params);
    if (!issues.some((existing) => JSON.stringify(existing.params) === key)) {
      issues.push(found);
    }
  };
  const checkStyle = (style: string) => {
    if (style.toLowerCase().includes('@import')) {
      add(issue('style_import'));
    }
    if (EXTERNAL_URL.test(style)) {
      add(issue('style_url'));
    }
    if (style.includes('\\')) {
      add(issue('style_escape'));
    }
  };

  let sawRoot = false;
  let styleDepth = 0;
  const notSvg = new Error('not svg');
  const parser = new SaxesParser({ xmlns: true });
  parser.on('opentag', (node) => {
    const element = node.local;
    if (!sawRoot) {
      sawRoot = true;
      if (element !== 'svg' || node.uri !== SVG_NAMESPACE) {
        // Stops the parser where the box stops reading.
        throw notSvg;
      }
      if (!isSquareViewBox(node.attributes.viewBox?.value)) {
        add(issue('viewbox'));
      }
    }
    if (FORBIDDEN_ELEMENTS.has(element.toLowerCase())) {
      add(issue('element_forbidden', { element }));
    }
    for (const attribute of Object.values(node.attributes)) {
      if (attribute.prefix === 'xmlns' || attribute.name === 'xmlns') {
        continue;
      }
      const { local: name, value } = attribute;
      if (name.toLowerCase().startsWith('on')) {
        add(issue('event_attribute', { attribute: name }));
      } else if (name === 'href' && !value.trim().startsWith('#')) {
        add(issue('external_href'));
      } else if (
        name === 'attributeName' &&
        ANIMATION_ELEMENTS.has(element.toLowerCase()) &&
        value.toLowerCase().includes('href')
      ) {
        add(issue('external_href'));
      } else if (name === 'style') {
        checkStyle(value);
      } else if (EXTERNAL_URL.test(value)) {
        add(issue('style_url'));
      }
    }
    if (element === 'style' && !node.isSelfClosing) {
      styleDepth++;
    }
  });
  parser.on('closetag', (node) => {
    if (node.local === 'style' && !node.isSelfClosing && styleDepth > 0) {
      styleDepth--;
    }
  });
  parser.on('text', (value) => {
    if (styleDepth > 0) {
      checkStyle(value);
    }
  });
  parser.on('cdata', (value) => {
    if (styleDepth > 0) {
      checkStyle(value);
    }
  });
  parser.on('processinginstruction', () => {
    add(issue('processing_instruction'));
  });
  try {
    parser.write(text).close();
  } catch (error) {
    return [issue(error === notSvg ? 'not_svg' : 'malformed')];
  }
  return issues;
}
