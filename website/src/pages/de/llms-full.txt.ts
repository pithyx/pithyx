import type { APIRoute } from 'astro';
import { llmsBundle, siteOf, textHeaders } from '../../lib/docs.ts';

export const GET: APIRoute = async ({ site }) =>
  new Response(await llmsBundle('de', siteOf(site)), { headers: textHeaders });
