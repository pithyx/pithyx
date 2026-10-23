import { unwrap } from '@pithyx/api-client';
import { create } from 'zustand';
import { api } from '../../api/client';
import type { FileDefaults, OpenWith } from './defaultAction';

interface FileDefaultsState {
  /** The user's choices by extension, from `GET /api/desktop`; the same on every device. */
  choices: FileDefaults;
}

export const useFileDefaults = create<FileDefaultsState>(() => ({ choices: {} }));

export function applyFileDefaults(list: readonly { extension: string; handler: string }[]): void {
  useFileDefaults.setState({
    choices: Object.fromEntries(list.map((item) => [item.extension, item.handler])),
  });
}

/** Saves "Always use this app" at once; a refused save puts the previous choices back and throws. */
export async function chooseFileDefault(extension: string, handler: OpenWith): Promise<void> {
  const previous = useFileDefaults.getState().choices;
  useFileDefaults.setState({ choices: { ...previous, [extension]: handler } });
  try {
    unwrap(
      await api.PUT('/api/desktop/file-defaults/{extension}', {
        params: { path: { extension } },
        body: { handler },
      }),
    );
  } catch (error) {
    useFileDefaults.setState({ choices: previous });
    throw error;
  }
}

/** Forgets the choice for an extension; a refused change puts it back and throws. */
export async function forgetFileDefault(extension: string): Promise<void> {
  const previous = useFileDefaults.getState().choices;
  useFileDefaults.setState({
    choices: Object.fromEntries(Object.entries(previous).filter(([key]) => key !== extension)),
  });
  try {
    unwrap(
      await api.DELETE('/api/desktop/file-defaults/{extension}', {
        params: { path: { extension } },
      }),
    );
  } catch (error) {
    useFileDefaults.setState({ choices: previous });
    throw error;
  }
}
