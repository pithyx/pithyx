import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { UiProvider } from '@pithyx/ui';
import { useState } from 'react';
import { FileDialog } from '../apps/host/FileDialog';
import type { FileDialogRequest } from '../apps/host/store';
import { DesktopShell } from '../shell/DesktopShell';
import { Taskbar } from '../shell/Taskbar';
import type { SceneContext } from './DesktopScreens';
import { now, startShares, useApps } from './sampleData';

const folder = [
  { name: 'Documents', kind: 'folder', size: 0, modified: '2026-10-27T09:10:00Z' },
  { name: 'Photos 2026', kind: 'folder', size: 0, modified: '2026-10-25T09:11:00Z' },
  { name: 'Budget 2026.ods', kind: 'file', size: 84_211, modified: '2026-10-24T09:16:00Z' },
  { name: 'Garden.jpg', kind: 'file', size: 4_812_004, modified: '2026-10-21T09:18:00Z' },
  { name: 'Lake at dusk.jpg', kind: 'file', size: 6_120_330, modified: '2026-10-26T09:15:00Z' },
  { name: 'Meeting notes.md', kind: 'file', size: 3_120, modified: '2026-10-23T09:17:00Z' },
  { name: 'Tax return 2025.pdf', kind: 'file', size: 2_401_118, modified: '2026-10-28T09:14:00Z' },
] as const;

/** The app file dialog of the desktop (#324) over an empty desktop, on sample shares and a sample folder. */
export function FileDialogScene({
  context,
  request,
}: {
  context: SceneContext;
  request: Omit<FileDialogRequest, 'windowId'>;
}) {
  const apps = useApps();
  const [client] = useState(() => {
    const seeded = new QueryClient({
      defaultOptions: { queries: { retry: false, staleTime: Infinity } },
    });
    seeded.setQueryData(['appHost', 'shares'], startShares);
    for (const share of startShares) {
      seeded.setQueryData(['appHost', 'list', share.id, ''], {
        pages: [
          {
            path: '',
            folderId: String(share.id),
            total: folder.length,
            entries: folder,
            nextCursor: null,
            topic: '',
          },
        ],
        pageParams: [undefined],
      });
    }
    return seeded;
  });
  return (
    <UiProvider locale={context.locale}>
      <QueryClientProvider client={client}>
        <DesktopShell
          taskbarPosition={context.taskbar}
          taskbar={
            <Taskbar
              position={context.taskbar}
              apps={[{ ...apps.files, state: 'focused' }]}
              startOpen={false}
              jobs={0}
              unread={0}
              notificationsOpen={false}
              switcherOpen={false}
              now={now}
              locale={context.locale}
            />
          }
        >
          <FileDialog request={{ ...request, windowId: 'gallery' }} />
        </DesktopShell>
      </QueryClientProvider>
    </UiProvider>
  );
}
