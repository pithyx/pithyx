/** An image a Dockerfile pulls that is not pinned by digest. */
export interface UnpinnedImage {
  /** The line where the instruction starts, from 1. */
  line: number;
  image: string;
}

const PINNED = /@sha256:[a-f0-9]{64}$/;
const ESCAPE_DIRECTIVE = /^#\s*escape\s*=\s*(\S)\s*$/i;
const DIRECTIVE = /^#\s*[a-z]+\s*=/i;

/**
 * The images a Dockerfile pulls without a digest: every FROM, and every `--from` of COPY and ADD that does not name an
 * earlier stage, must be `scratch`, an earlier stage, or an image pinned with `@sha256:` and no variable. Instructions
 * may continue over lines with the escape character (`\` or the `escape` parser directive); comments are skipped.
 */
export function unpinnedImages(text: string): UnpinnedImage[] {
  const lines = text.split('\n');
  let escape = '\\';
  let first = 0;
  while (first < lines.length && DIRECTIVE.test(lines[first] ?? '')) {
    escape = ESCAPE_DIRECTIVE.exec(lines[first] ?? '')?.[1] ?? escape;
    first++;
  }

  const unpinned: UnpinnedImage[] = [];
  const stages = new Set<string>();
  let stageCount = 0;
  const check = (image: string | undefined, line: number) => {
    const name = image ?? '';
    const lower = name.toLowerCase();
    if (lower === 'scratch' || stages.has(lower)) {
      return;
    }
    if (name.includes('$') || !PINNED.test(name)) {
      unpinned.push({ line, image: name });
    }
  };
  const instruction = (words: string[], line: number) => {
    const keyword = words[0]?.toUpperCase();
    if (keyword === 'FROM') {
      const rest = words.slice(1).filter((word) => !word.startsWith('--'));
      check(rest[0], line);
      if (rest[1]?.toUpperCase() === 'AS' && rest[2] !== undefined) {
        stages.add(rest[2].toLowerCase());
      }
      stageCount++;
    } else if (keyword === 'COPY' || keyword === 'ADD') {
      for (const word of words.slice(1)) {
        if (!word.startsWith('--')) {
          break;
        }
        if (word.toLowerCase().startsWith('--from=')) {
          const source = word.slice('--from='.length);
          const stageIndex = /^[0-9]+$/.test(source) ? Number(source) : undefined;
          if (stageIndex === undefined || stageIndex >= stageCount) {
            check(source, line);
          }
        }
      }
    }
  };

  let parts: string[] = [];
  let start = 0;
  for (let index = first; index < lines.length; index++) {
    const trimmed = (lines[index] ?? '').trim();
    if (trimmed === '' || trimmed.startsWith('#')) {
      continue;
    }
    if (parts.length === 0) {
      start = index + 1;
    }
    if (trimmed.endsWith(escape)) {
      parts.push(trimmed.slice(0, -escape.length));
      continue;
    }
    parts.push(trimmed);
    instruction(parts.join(' ').trim().split(/\s+/), start);
    parts = [];
  }
  if (parts.length > 0) {
    instruction(parts.join(' ').trim().split(/\s+/), start);
  }
  return unpinned;
}
