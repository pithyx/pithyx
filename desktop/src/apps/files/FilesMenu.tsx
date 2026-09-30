import { useTranslation } from 'react-i18next';
import {
  IconClipboard,
  IconCopy,
  IconDownload,
  IconExternalLink,
  IconEye,
  IconFolderPlus,
  IconHistory,
  IconPencil,
  IconScissors,
  IconSquare,
  IconTrash,
  IconTrashX,
  IconUpload,
} from '../../ui/icons';
import { ContextMenuPopover } from '../../ui/ContextMenu';
import { Menu, MenuItem, MenuSeparator } from '../../ui/Menu';

export type FilesAction =
  | 'open'
  | 'download'
  | 'openWith'
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
  viewable,
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
  /** Whether the one selected file opens in the viewer. */
  viewable: boolean;
  writable: boolean;
  canPaste: boolean;
  onAction: (action: FilesAction) => void;
  onClose: () => void;
}) {
  const { t } = useTranslation('files');
  const icon = { size: 18, stroke: 1.75 };
  return (
    <ContextMenuPopover x={x} y={y} onClose={onClose}>
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
            {single === 'file' && viewable && (
              <MenuItem id="open" icon={<IconEye {...icon} />}>
                {t('actions.open')}
              </MenuItem>
            )}
            {single === 'file' && (
              <MenuItem id="download" icon={<IconDownload {...icon} />}>
                {t('actions.download')}
              </MenuItem>
            )}
            {single === 'file' && (
              <MenuItem id="openWith" icon={<IconExternalLink {...icon} />}>
                {t('actions.openWith')}
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
            <MenuItem id="selectAll" icon={<IconSquare {...icon} />} shortcut={t('keys.selectAll')}>
              {t('actions.selectAll')}
            </MenuItem>
          </>
        )}
      </Menu>
    </ContextMenuPopover>
  );
}
