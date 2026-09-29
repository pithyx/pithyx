/** How the viewer shows a file; `null` for files it does not open (they are downloaded). */
export type ViewKind = 'image' | 'pdf' | 'text' | 'audio' | 'video';

const KINDS: Record<string, ViewKind> = {};
function add(kind: ViewKind, extensions: string) {
  for (const extension of extensions.split(' ')) {
    KINDS[extension] = kind;
  }
}
// Images the browser shows on its own; SVG only ever as an image, where its scripts never run.
add('image', 'jpg jpeg jpe png webp gif avif bmp ico svg');
add('pdf', 'pdf');
add('audio', 'mp3 m4a aac oga ogg opus wav flac');
add('video', 'mp4 m4v webm ogv mov');
// Shown as plain text, never rendered: HTML and SVG sources included when opened as text.
add(
  'text',
  'txt text md markdown log csv tsv json jsonc xml yaml yml toml ini conf cfg properties env ' +
    'html htm css scss js mjs cjs ts tsx jsx vue svelte py rb php pl lua go rs java kt swift c h cc cpp hpp cs ' +
    'fs sh bash zsh fish ps1 bat sql graphql proto dockerfile makefile gitignore editorconfig nfo srt vtt diff patch',
);

/** Extensions with a thumbnail from the daemon (it checks the content itself). */
const THUMBNAILS = new Set(['jpg', 'jpeg', 'jpe', 'png', 'webp', 'gif']);

function extensionOf(name: string): string {
  const dot = name.lastIndexOf('.');
  // Files like "Makefile" or "Dockerfile" have no extension but a well-known name.
  return (dot > 0 ? name.slice(dot + 1) : name).toLowerCase();
}

export function viewKindOf(name: string): ViewKind | null {
  return KINDS[extensionOf(name)] ?? null;
}

export function hasThumbnail(name: string): boolean {
  return THUMBNAILS.has(extensionOf(name));
}
