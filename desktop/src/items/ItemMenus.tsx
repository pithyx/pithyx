import { useState } from 'react';
import { Form } from 'react-aria-components';
import { useTranslation } from 'react-i18next';
import { useAppName } from '../apps/names';
import { useQueryClient } from '@tanstack/react-query';
import type { components } from '@pithyx/api-client';
import { queryKeys } from '../api/queries';
import { isAppAllowed } from '../apps/access';
import { appsFor } from '../apps/registry';
import { AppIcon } from '../shell/AppIcon';
import {
  Button,
  ContextMenuPopover,
  Dialog,
  Menu,
  MenuItem,
  MenuPopover,
  MenuSeparator,
  TextField,
} from '@pithyx/ui';
import {
  IconArrowsMove,
  IconDownload,
  IconExternalLink,
  IconEye,
  IconFolderSearch,
  IconNote,
  IconPencil,
  IconPlus,
  IconRestore,
  IconTrash,
} from '../ui/icons';
import { openWithFor } from './fileTargets';
import { SubmenuTrigger } from 'react-aria-components';

export type ItemAction =
  | 'open'
  | 'rename'
  | 'originalName'
  | 'showInFiles'
  | 'move'
  | 'delete'
  | 'note'
  | `shortcut:${string}`
  | `openWith:${string}`;

/** The one selected shortcut to a file or folder, for its own entries in the menu. */
export interface FileShortcutInfo {
  /** Gone or no longer readable: only removing it is offered. */
  missing: boolean;
  folder: boolean;
  /** It has an own label, which "Use original name" clears. */
  named: boolean;
  /** The target's own name, for "Open with". */
  name: string;
}

/**
 * The context menu of the desktop, at the pointer or the focused icon: for selected icons (open, rename, move,
 * remove; for a file or folder also open with, show in Files and use original name) or for the empty desktop (new
 * note, new shortcut).
 */
export function ItemMenu({
  x,
  y,
  kind,
  count,
  canRename,
  file,
  onAction,
  onClose,
}: {
  x: number;
  y: number;
  kind: 'desktop' | 'items';
  count: number;
  canRename: boolean;
  file?: FileShortcutInfo | undefined;
  onAction: (action: ItemAction) => void;
  onClose: () => void;
}) {
  const { t } = useTranslation();
  const appName = useAppName();
  const icon = { size: 18, stroke: 1.75 };
  const user = useQueryClient().getQueryData<components['schemas']['UserInfo']>(queryKeys.session);
  const apps = appsFor((id) => isAppAllowed(id, user?.permissions));
  return (
    <ContextMenuPopover x={x} y={y} onClose={onClose}>
      <Menu
        aria-label={kind === 'desktop' ? t('items.desktopMenu') : t('items.itemMenu', { count })}
        autoFocus="first"
        onAction={(key) => {
          onAction(String(key) as ItemAction);
        }}
        onClose={onClose}
      >
        {kind === 'items' && file?.missing === true ? (
          <MenuItem id="delete" icon={<IconTrash {...icon} />} tone="danger">
            {t('items.remove', { count })}
          </MenuItem>
        ) : kind === 'items' ? (
          <>
            {count === 1 && (
              <MenuItem id="open" icon={<IconExternalLink {...icon} />}>
                {t('items.open')}
              </MenuItem>
            )}
            {file !== undefined && !file.folder && (
              <SubmenuTrigger>
                <MenuItem id="openWith" icon={<IconExternalLink {...icon} />}>
                  {t('items.openWith')}
                </MenuItem>
                <MenuPopover>
                  <Menu
                    aria-label={t('items.openWith')}
                    onAction={(key) => {
                      onAction(`openWith:${String(key)}`);
                    }}
                  >
                    {openWithFor(file.name).map((choice) => (
                      <MenuItem
                        key={choice}
                        id={choice}
                        icon={
                          choice === 'viewer' ? (
                            <IconEye {...icon} />
                          ) : choice === 'download' ? (
                            <IconDownload {...icon} />
                          ) : (
                            <IconExternalLink {...icon} />
                          )
                        }
                      >
                        {choice === 'viewer'
                          ? t('items.viewer')
                          : choice === 'download'
                            ? t('items.download')
                            : appName(choice.slice('app:'.length))}
                      </MenuItem>
                    ))}
                  </Menu>
                </MenuPopover>
              </SubmenuTrigger>
            )}
            {file !== undefined && (
              <MenuItem id="showInFiles" icon={<IconFolderSearch {...icon} />}>
                {t('items.showInFiles')}
              </MenuItem>
            )}
            {canRename && (
              <MenuItem id="rename" icon={<IconPencil {...icon} />} shortcut="F2">
                {t('items.rename')}
              </MenuItem>
            )}
            {file?.named === true && (
              <MenuItem id="originalName" icon={<IconRestore {...icon} />}>
                {t('items.originalName')}
              </MenuItem>
            )}
            <MenuItem id="move" icon={<IconArrowsMove {...icon} />}>
              {t('items.move')}
            </MenuItem>
            <MenuSeparator />
            <MenuItem
              id="delete"
              icon={<IconTrash {...icon} />}
              tone="danger"
              shortcut={t('items.deleteKey')}
            >
              {t('items.remove', { count })}
            </MenuItem>
          </>
        ) : (
          <>
            <MenuItem id="note" icon={<IconNote {...icon} />}>
              {t('items.newNote')}
            </MenuItem>
            <SubmenuTrigger>
              <MenuItem id="shortcut" icon={<IconPlus {...icon} />}>
                {t('items.newShortcut')}
              </MenuItem>
              <MenuPopover>
                <Menu
                  aria-label={t('items.newShortcut')}
                  onAction={(key) => {
                    onAction(`shortcut:${String(key)}`);
                  }}
                >
                  {apps.map((app) => (
                    <MenuItem key={app.id} id={app.id} icon={<AppIcon app={app} size={18} />}>
                      {appName(app.id)}
                    </MenuItem>
                  ))}
                </Menu>
              </MenuPopover>
            </SubmenuTrigger>
          </>
        )}
      </Menu>
    </ContextMenuPopover>
  );
}

/** Renames a shortcut. An empty name goes back to the app's or the target's own name. */
export function RenameDialog({
  name,
  appName,
  file = false,
  onRename,
  onClose,
}: {
  name: string | null;
  /** The app's name, or the target's for a shortcut to a file or folder. */
  appName: string;
  file?: boolean;
  onRename: (name: string) => void;
  onClose: () => void;
}) {
  const { t } = useTranslation();
  const [value, setValue] = useState(name ?? appName);
  return (
    <Dialog
      isOpen
      isDismissable
      onOpenChange={(open) => {
        if (!open) {
          onClose();
        }
      }}
      title={t('items.renameTitle')}
      actions={
        <>
          <Button variant="quiet" onPress={onClose}>
            {t('items.cancel')}
          </Button>
          <Button variant="primary" type="submit" form="rename-item">
            {t('items.rename')}
          </Button>
        </>
      }
    >
      <Form
        id="rename-item"
        onSubmit={(event) => {
          event.preventDefault();
          onRename(value);
        }}
      >
        <TextField
          label={t('items.name')}
          description={t(file ? 'items.nameHintFile' : 'items.nameHint', { app: appName })}
          value={value}
          onChange={setValue}
          maxLength={100}
          autoFocus
        />
      </Form>
    </Dialog>
  );
}
