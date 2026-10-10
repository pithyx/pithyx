import type { APIRoute } from 'astro';
import { sectionBundlePaths, sectionBundleResponse } from '../../../lib/bundles.ts';
import { siteOf } from '../../../lib/docs.ts';

export const getStaticPaths = () => sectionBundlePaths();

export const GET: APIRoute = ({ params, site }) =>
  sectionBundleResponse('de', params.section ?? '', siteOf(site));
