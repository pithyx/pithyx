// Types for the single-icon modules imported in icons.ts; the package only types its index.
declare module '@tabler/icons-react/dist/esm/icons/*.mjs' {
  import type { TablerIcon } from '@tabler/icons-react';

  const icon: TablerIcon;
  export default icon;
}
