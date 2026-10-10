import { docsLoader, i18nLoader } from '@astrojs/starlight/loaders';
import { docsSchema, i18nSchema } from '@astrojs/starlight/schema';
import { defineCollection } from 'astro:content';
import { glob } from 'astro/loaders';
import { z } from 'astro/zod';
import { faqCategories } from './lib/faqCategories.ts';
import { landingIcons } from './lib/landingIcons.ts';

const link = z.object({ label: z.string(), href: z.string() });

export const collections = {
  docs: defineCollection({
    loader: docsLoader(),
    schema: docsSchema({
      extend: z.object({
        description: z.string(),
        // placeholder: only the outline exists; draft: written but not checked against the code; ready: checked.
        status: z.enum(['placeholder', 'draft', 'ready']).default('placeholder'),
        audience: z.enum(['user', 'admin', 'developer']).optional(),
        // The Pithyx version that brought the behavior, for example "0.1.0".
        since: z.string().optional(),
        // Content appended at build time, in the HTML page and in its Markdown twin.
        generated: z.enum(['overview', 'faq', 'error-codes']).optional(),
      }),
    }),
  }),
  // Starlight's own UI strings stay its built-in translations; these files add the site's strings (src/lib/i18n.ts).
  i18n: defineCollection({
    loader: i18nLoader(),
    schema: i18nSchema({ extend: z.object({}).catchall(z.string()) }),
  }),
  faq: defineCollection({
    loader: glob({ pattern: '**/[^_]*.md', base: './src/content/faq' }),
    schema: z.object({
      question: z.string(),
      category: z.enum(faqCategories),
      tags: z.array(z.string()).default([]),
      order: z.number().default(100),
      status: z.enum(['placeholder', 'draft', 'ready']).default('placeholder'),
    }),
  }),
  landing: defineCollection({
    loader: glob({ pattern: '*.yaml', base: './src/content/landing' }),
    schema: ({ image }) =>
      z.object({
        title: z.string(),
        description: z.string(),
        hero: z.object({
          title: z.string(),
          tagline: z.string(),
          primary: link,
          secondary: link,
          github: link,
        }),
        showcase: z.object({ image: image(), alt: z.string(), caption: z.string() }),
        features: z.object({
          title: z.string(),
          intro: z.string(),
          items: z
            .array(
              z.object({
                icon: z.enum(landingIcons),
                title: z.string(),
                text: z.string(),
                href: z.string().optional(),
              }),
            )
            .min(1),
        }),
        why: z.object({
          title: z.string(),
          intro: z.string(),
          items: z.array(z.object({ title: z.string(), text: z.string() })).min(1),
        }),
        specs: z.object({
          title: z.string(),
          rows: z.array(z.object({ label: z.string(), value: z.string() })).min(1),
        }),
        gallery: z.object({
          title: z.string(),
          close: z.string(),
          items: z.array(z.object({ image: image(), alt: z.string(), caption: z.string() })).min(1),
        }),
        ai: z.object({
          title: z.string(),
          intro: z.string(),
          levels: z
            .array(z.object({ title: z.string(), text: z.string(), path: z.string() }))
            .min(1),
          promptLabel: z.string(),
          prompt: z.string(),
          copy: z.string(),
          copied: z.string(),
        }),
        cta: z.object({ title: z.string(), text: z.string(), primary: link, secondary: link }),
      }),
  }),
};
