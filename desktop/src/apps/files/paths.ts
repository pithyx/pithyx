/** Paths inside a share: relative, `/` as separator, "" for the share root. */

export function joinPath(folder: string, name: string): string {
  return folder === '' ? name : `${folder}/${name}`;
}

export function parentOf(path: string): string {
  const slash = path.lastIndexOf('/');
  return slash < 0 ? '' : path.slice(0, slash);
}

export function nameOf(path: string): string {
  return path.slice(path.lastIndexOf('/') + 1);
}

/** The folders from the root down to `path`, each with its full path, for the breadcrumb. */
export function crumbsOf(path: string): { name: string; path: string }[] {
  if (path === '') {
    return [];
  }
  const parts = path.split('/');
  return parts.map((name, index) => ({ name, path: parts.slice(0, index + 1).join('/') }));
}

/**
 * The name the daemon would pick for a copy that keeps both: "Report.pdf" becomes "Report (2).pdf", then
 * "Report (3).pdf", whichever is not in `taken`.
 */
export function freeName(name: string, taken: (candidate: string) => boolean): string {
  const dot = name.lastIndexOf('.');
  const [stem, extension] = dot > 0 ? [name.slice(0, dot), name.slice(dot)] : [name, ''];
  for (let number = 2; ; number++) {
    const candidate = `${stem} (${String(number)})${extension}`;
    if (!taken(candidate)) {
      return candidate;
    }
  }
}
