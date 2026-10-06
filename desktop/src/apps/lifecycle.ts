// Apps that want a last chance to save before the user signs out register here; the desktop waits for them, but
// never longer than the SDK promises (BEFORE_UNLOAD_TIMEOUT in @pithyx/sdk, asked as beforeUnload with `logout`).
const beforeLogout = new Set<() => Promise<unknown>>();

export function onBeforeLogout(hook: () => Promise<unknown>): () => void {
  beforeLogout.add(hook);
  return () => {
    beforeLogout.delete(hook);
  };
}

export async function runBeforeLogout(): Promise<void> {
  await Promise.allSettled([...beforeLogout].map((hook) => hook()));
}
