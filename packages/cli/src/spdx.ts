/**
 * The licenses and exceptions of an SPDX license expression (`MIT`, `MIT OR Apache-2.0`,
 * `(GPL-2.0-only WITH Classpath-exception-2.0) AND BSD-3-Clause`), or undefined when it is not one. Operators are
 * upper case; a `+` after a license is kept.
 */
export function parseSpdxExpression(
  expression: string,
): { licenses: string[]; exceptions: string[] } | undefined {
  const tokens = expression.match(/\(|\)|[^\s()]+/g) ?? [];
  const licenses: string[] = [];
  const exceptions: string[] = [];
  let position = 0;
  const id = /^[A-Za-z0-9][A-Za-z0-9.-]*\+?$/;
  const operators = new Set(['AND', 'OR', 'WITH']);

  const atom = (): boolean => {
    const token = tokens[position];
    if (token === '(') {
      position++;
      if (!or() || tokens[position] !== ')') {
        return false;
      }
      position++;
      return true;
    }
    if (token === undefined || operators.has(token) || !id.test(token)) {
      return false;
    }
    licenses.push(token);
    position++;
    if (tokens[position] === 'WITH') {
      const exception = tokens[position + 1];
      if (
        exception === undefined ||
        operators.has(exception) ||
        !id.test(exception) ||
        exception.endsWith('+')
      ) {
        return false;
      }
      exceptions.push(exception);
      position += 2;
    }
    return true;
  };
  const and = (): boolean => {
    if (!atom()) {
      return false;
    }
    while (tokens[position] === 'AND') {
      position++;
      if (!atom()) {
        return false;
      }
    }
    return true;
  };
  const or = (): boolean => {
    if (!and()) {
      return false;
    }
    while (tokens[position] === 'OR') {
      position++;
      if (!and()) {
        return false;
      }
    }
    return true;
  };

  return or() && position === tokens.length ? { licenses, exceptions } : undefined;
}
