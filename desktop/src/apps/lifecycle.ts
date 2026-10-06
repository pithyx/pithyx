import type { UnloadReason } from '@pithyx/sdk';

// App windows that want a last chance to save before they go away register here: the user signs out, the box shuts
// down or restarts, or Pithyx installs an update. The desktop waits for them, but never longer than the SDK promises
// (each hook asks `beforeUnload` with BEFORE_UNLOAD_TIMEOUT from @pithyx/sdk); none of them can stop it.
const beforeUnload = new Set<(reason: UnloadReason) => Promise<unknown>>();

export function onBeforeUnload(hook: (reason: UnloadReason) => Promise<unknown>): () => void {
  beforeUnload.add(hook);
  return () => {
    beforeUnload.delete(hook);
  };
}

export async function runBeforeUnload(reason: UnloadReason): Promise<void> {
  await Promise.allSettled([...beforeUnload].map((hook) => hook(reason)));
}
