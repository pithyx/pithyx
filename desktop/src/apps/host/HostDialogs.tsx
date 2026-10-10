import { Button, Dialog, TextField } from '@pithyx/ui';
import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { copyText } from '../../platform/browser';
import { useDesktop } from '../../wm/store';
import { useAppName } from '../names';
import { FileDialog } from './FileDialog';
import { answerForceClose, closeManualCopy, useHost } from './store';

/** D62: an app did not answer `beforeClose` in time; the user decides whether to close its window anyway. */
function UnresponsiveDialog({ windowId }: { windowId: string }) {
  const { t } = useTranslation();
  const appName = useAppName();
  const window = useDesktop((state) => state.windows[windowId]);
  const name = window?.title ?? (window === undefined ? '' : appName(window.app));
  return (
    <Dialog
      isOpen
      isDismissable
      role="alertdialog"
      tone="danger"
      onOpenChange={(open) => {
        if (!open) {
          answerForceClose(windowId, false);
        }
      }}
      title={t('appHost.unresponsive.title', { name })}
      actions={
        <>
          <Button
            onPress={() => {
              answerForceClose(windowId, false);
            }}
          >
            {t('appHost.unresponsive.wait')}
          </Button>
          <Button
            variant="danger"
            onPress={() => {
              answerForceClose(windowId, true);
            }}
          >
            {t('appHost.unresponsive.close')}
          </Button>
        </>
      }
    >
      <p>{t('appHost.unresponsive.body')}</p>
    </Dialog>
  );
}

/** The browser refused to copy an app's text (plain HTTP); the user copies it from here. */
function CopyDialog({ text }: { text: string }) {
  const { t } = useTranslation();
  const [failed, setFailed] = useState(false);
  return (
    <Dialog
      isOpen
      isDismissable
      onOpenChange={(open) => {
        if (!open) {
          closeManualCopy();
        }
      }}
      title={t('appHost.copy.title')}
      actions={
        <>
          <Button onPress={closeManualCopy}>{t('appHost.copy.close')}</Button>
          <Button
            variant="primary"
            onPress={() => {
              void copyText(text).then((copied) => {
                if (copied) {
                  closeManualCopy();
                } else {
                  setFailed(true);
                }
              });
            }}
          >
            {t('appHost.copy.copy')}
          </Button>
        </>
      }
    >
      <TextField
        label={t('appHost.copy.label')}
        description={failed ? t('appHost.copy.manual') : t('appHost.copy.hint')}
        value={text}
        isReadOnly
        autoFocus
        onFocus={(event) => {
          if (event.target instanceof HTMLInputElement) {
            event.target.select();
          }
        }}
      />
    </Dialog>
  );
}

export function HostDialogs() {
  const unresponsive = useHost((state) => state.unresponsive[0]);
  const copy = useHost((state) => state.copy);
  const fileDialog = useHost((state) => state.fileDialog);
  if (unresponsive !== undefined) {
    return <UnresponsiveDialog key={unresponsive} windowId={unresponsive} />;
  }
  if (fileDialog !== null) {
    return <FileDialog request={fileDialog} />;
  }
  return copy === null ? null : <CopyDialog text={copy} />;
}
