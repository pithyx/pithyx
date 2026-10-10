// The docs areas, in sidebar order. The sidebar (astro.config.ts), the LLM bundles (/llms/<id>.txt) and the docs index
// all read this list, so a new area is added here once, with its label and summary in src/content/i18n/<locale>.json
// (section.<id>.label, section.<id>.summary).

export interface Section {
  /** Bundle id: /llms/<id>.txt. */
  id: string;
  /** Folder under src/content/docs/docs/ (and src/content/docs/de/docs/ for German). */
  directory: string;
  children?: Section[];
}

export const sections: Section[] = [
  { id: 'getting-started', directory: 'getting-started' },
  { id: 'user', directory: 'user' },
  { id: 'admin', directory: 'admin' },
  { id: 'install', directory: 'install' },
  {
    id: 'developers',
    directory: 'developers',
    children: [
      { id: 'developers-daemon', directory: 'developers/daemon' },
      { id: 'developers-desktop', directory: 'developers/desktop' },
      { id: 'developers-apps', directory: 'developers/apps' },
    ],
  },
  { id: 'sdk', directory: 'sdk' },
  { id: 'reference', directory: 'reference' },
  { id: 'project', directory: 'project' },
];

/** Every section with its children flattened, parents first. */
export const allSections: Section[] = sections.flatMap((section) => [
  section,
  ...(section.children ?? []),
]);
