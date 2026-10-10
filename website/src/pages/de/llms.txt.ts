import type { APIRoute } from 'astro';
import { llmsIndex, siteOf, textHeaders } from '../../lib/docs.ts';

export const GET: APIRoute = async ({ site }) =>
  new Response(await llmsIndex('de', siteOf(site)), { headers: textHeaders });
