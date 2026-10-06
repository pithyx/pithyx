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
  IconExternalLink,
  IconNote,
  IconPencil,
  IconPlus,
  IconTrash,
} from '../ui/icons';
import { SubmenuTrigger } from 'react-aria-components';

export type ItemAction = 'open' | 'rename' | 'move' | 'delete' | 'note' | `shortcut:${string}`;

/**
 * The context menu of the desktop, at the pointer or the focused icon: for selected icons (open, rename, move,
 * remove) or for the empty desktop (new note, new shortcut).
 */
export function ItemMenu({
  x,
  y,
  kind,
  count,
  canRename,
  onAction,
  onClose,
}: {
  x: number;
  y: number;
  kind: 'desktop' | 'items';
  count: number;
  canRename: boolean;
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
        {kind === 'items' ? (
          <>
            {count === 1 && (
              <MenuItem id="open" icon={<IconExternalLink {...icon} />}>
                {t('items.open')}
              </MenuItem>
            )}
            {canRename && (
              <MenuItem id="rename" icon={<IconPencil {...icon} />} shortcut="F2">
                {t('items.rename')}
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

/** Renames a shortcut. An empty name goes back to the app's own name. */
export function RenameDialog({
  name,
  appName,
  onRename,
  onClose,
}: {
  name: string | null;
  appName: string;
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
          description={t('items.nameHint', { app: appName })}
          value={value}
          onChange={setValue}
          maxLength={100}
          autoFocus
        />
      </Form>
    </Dialog>
  );
}
