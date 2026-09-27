import { useRef } from 'react';
import { Popover } from 'react-aria-components';
import { useTranslation } from 'react-i18next';
import {
  IconClipboard,
  IconCopy,
  IconDownload,
  IconExternalLink,
  IconFolderPlus,
  IconHistory,
  IconPencil,
  IconScissors,
  IconSquare,
  IconTrash,
  IconTrashX,
  IconUpload,
} from '../../ui/icons';
import { Menu, MenuItem, MenuSeparator } from '../../ui/Menu';
import menuStyles from '../../ui/Menu.module.css';
import styles from './FilesApp.module.css';

export type FilesAction =
  | 'open'
  | 'download'
  | 'cut'
  | 'copy'
  | 'paste'
  | 'rename'
  | 'versions'
  | 'delete'
  | 'deletePermanent'
  | 'newFolder'
  | 'upload'
  | 'selectAll';

/** The context menu of the file list, at the pointer or the active row: for the selection, or for the folder. */
export function FilesMenu({
  x,
  y,
  kind,
  count,
  single,
  writable,
  canPaste,
  onAction,
  onClose,
}: {
  x: number;
  y: number;
  kind: 'entries' | 'folder';
  count: number;
  /** What exactly one selected entry is; null for several. */
  single: 'file' | 'folder' | null;
  writable: boolean;
  canPaste: boolean;
  onAction: (action: FilesAction) => void;
  onClose: () => void;
}) {
  const { t } = useTranslation('files');
  const anchor = useRef<HTMLDivElement>(null);
  const icon = { size: 18, stroke: 1.75 };
  return (
    <>
      <div ref={anchor} className={styles.anchor} style={{ left: x, top: y }} aria-hidden />
      <Popover
        triggerRef={anchor}
        isOpen
        onOpenChange={(open) => {
          if (!open) {
            onClose();
          }
        }}
        placement="bottom start"
        offset={2}
        className={menuStyles.popover}
      >
        <Menu
          aria-label={kind === 'entries' ? t('menu.entries', { count }) : t('menu.folder')}
          autoFocus="first"
          onAction={(key) => {
            onAction(String(key) as FilesAction);
          }}
          onClose={onClose}
        >
          {kind === 'entries' ? (
            <>
              {single === 'folder' && (
                <MenuItem id="open" icon={<IconExternalLink {...icon} />}>
                  {t('actions.open')}
                </MenuItem>
              )}
              {single === 'file' && (
                <MenuItem id="download" icon={<IconDownload {...icon} />}>
                  {t('actions.download')}
                </MenuItem>
              )}
              <MenuItem id="copy" icon={<IconCopy {...icon} />} shortcut={t('keys.copy')}>
                {t('actions.copy')}
              </MenuItem>
              {writable && (
                <MenuItem id="cut" icon={<IconScissors {...icon} />} shortcut={t('keys.cut')}>
                  {t('actions.cut')}
                </MenuItem>
              )}
              {writable && single !== null && (
                <MenuItem id="rename" icon={<IconPencil {...icon} />} shortcut="F2">
                  {t('actions.rename')}
                </MenuItem>
              )}
              {single !== null && (
                <MenuItem id="versions" icon={<IconHistory {...icon} />}>
                  {t('actions.versions')}
                </MenuItem>
              )}
              {writable && (
                <>
                  <MenuSeparator />
                  <MenuItem
                    id="delete"
                    icon={<IconTrash {...icon} />}
                    tone="danger"
                    shortcut={t('keys.delete')}
                  >
                    {t('actions.delete')}
                  </MenuItem>
                  <MenuItem
                    id="deletePermanent"
                    icon={<IconTrashX {...icon} />}
                    tone="danger"
                    shortcut={t('keys.deletePermanent')}
                  >
                    {t('actions.deletePermanent')}
                  </MenuItem>
                </>
              )}
            </>
          ) : (
            <>
              {writable && (
                <MenuItem id="newFolder" icon={<IconFolderPlus {...icon} />}>
                  {t('actions.newFolder')}
                </MenuItem>
              )}
              {writable && (
                <MenuItem id="upload" icon={<IconUpload {...icon} />}>
                  {t('actions.upload')}
                </MenuItem>
              )}
              {writable && canPaste && (
                <MenuItem id="paste" icon={<IconClipboard {...icon} />} shortcut={t('keys.paste')}>
                  {t('actions.paste')}
                </MenuItem>
              )}
              <MenuItem
                id="selectAll"
                icon={<IconSquare {...icon} />}
                shortcut={t('keys.selectAll')}
              >
                {t('actions.selectAll')}
              </MenuItem>
            </>
          )}
        </Menu>
      </Popover>
    </>
  );
}
