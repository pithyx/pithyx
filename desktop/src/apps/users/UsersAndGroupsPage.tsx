import type { PithyxApp } from '@pithyx/sdk';
import { Tab, TabList, TabPanel, Tabs } from '@pithyx/ui';
import { useTranslation } from 'react-i18next';
import { GroupsPanel } from './GroupsPage';
import { useGroups, useUsers } from './queries';
import { UsersPanel } from './UsersPage';

/** Users and groups of Settings (D394): one page with a tab each, the count after the name. */
export function UsersAndGroupsPage({ app }: { app: PithyxApp }) {
  const { t } = useTranslation('users');
  const users = useUsers(app).data;
  const groups = useGroups(app).data;
  return (
    <Tabs>
      <TabList aria-label={t('tabs.label')}>
        <Tab id="users" {...(users === undefined ? {} : { count: users.length })}>
          {t('title')}
        </Tab>
        <Tab id="groups" {...(groups === undefined ? {} : { count: groups.length })}>
          {t('groups.title')}
        </Tab>
      </TabList>
      <TabPanel id="users">
        <UsersPanel app={app} />
      </TabPanel>
      <TabPanel id="groups">
        <GroupsPanel app={app} />
      </TabPanel>
    </Tabs>
  );
}
