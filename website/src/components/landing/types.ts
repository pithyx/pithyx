import type { CollectionEntry } from 'astro:content';

export type Landing = CollectionEntry<'landing'>['data'];

/** An image as the React sections get it: Astro resolves the file, the sections only render the URL. */
export interface Picture {
  src: string;
  width: number;
  height: number;
  alt: string;
  caption: string;
}
