// Hand-written until the OpenAPI client is generated in step 3.6.
export interface Health {
  status: 'ok';
  mode: 'real' | 'simulated';
}

export async function fetchHealth(signal: AbortSignal): Promise<Health> {
  const response = await fetch('/api/health', { signal });
  if (!response.ok) {
    throw new Error(`GET /api/health failed with ${String(response.status)}`);
  }
  return (await response.json()) as Health;
}
