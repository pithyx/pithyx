import type { APIRoute } from 'astro';
import { llmsIndex, siteOf, textHeaders } from '../lib/docs.ts';

export const GET: APIRoute = async ({ site }) =>
  new Response(await llmsIndex('en', siteOf(site)), { headers: textHeaders });
